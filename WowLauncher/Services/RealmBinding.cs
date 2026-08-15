namespace WowLauncher.Services;

using WowLauncher.Models;
using WowLauncher.Services.Platform;

/// <summary>
/// A realm address the way the launcher carries it everywhere: a host, optionally a port
/// (<c>play.stonetavern.app</c>, <c>10.0.0.5:3725</c>, <c>[::1]:3724</c>). Parsing is deliberately
/// strict — the same whitelist <see cref="ClientService.IsValidRealmlistAddress"/> applies, because this
/// value is written into three files the client and the proxy then execute as configuration.
/// </summary>
public sealed record RealmAddress(string Host, int? Port)
{
    /// <summary>The address as one string, exactly as it goes into <c>realmlist.wtf</c>.</summary>
    public string Value => Port is int p ? $"{Host}:{p}" : Host;

    /// <summary>Parse a realmlist address, or null when it is empty or not safe to write into a .wtf
    /// file. Null is a refusal, never a repaired guess: a mangled host would produce a client that
    /// connects to nothing and says nothing (see <see cref="ClientService.IsValidRealmlistAddress"/>).</summary>
    public static RealmAddress? Parse(string? raw)
    {
        var trimmed = raw?.Trim() ?? "";
        if (!ClientService.IsValidRealmlistAddress(trimmed)) return null;

        // IPv6 in brackets keeps its brackets in the host; the port is whatever follows the last colon
        // OUTSIDE them. A bare IPv6 without brackets has many colons and no port — treated as host only.
        var host = trimmed;
        int? port = null;

        var closing = trimmed.LastIndexOf(']');
        var colon = trimmed.LastIndexOf(':');
        var hasPortColon = colon > closing && colon > 0 && colon < trimmed.Length - 1
                           && (closing >= 0 || trimmed.IndexOf(':') == colon);
        if (hasPortColon)
        {
            var portText = trimmed[(colon + 1)..];
            if (!int.TryParse(portText, out var parsed) || parsed is < 1 or > 65535) return null;
            host = trimmed[..colon];
            port = parsed;
        }

        return string.IsNullOrEmpty(host) ? null : new RealmAddress(host, port);
    }
}

/// <summary>
/// The one place that answers "which realm is this launch going to, and who has to agree about it".
///
/// <para><b>Why this exists.</b> A realm address has to reach THREE independent sinks before a client
/// actually talks to that realm, and until 2026-07-27 the launcher only served one of them — so a player
/// who added their own realm got a client that started and quietly connected to Stonetavern:</para>
///
/// <list type="number">
/// <item><b>1.12.1, the client's own config</b> — <c>realmlist.wtf</c> plus <c>SET realmList</c> in
/// <c>Config.wtf</c>. The launcher writes both (<see cref="ClientService.ConfigureClient"/>).</item>
/// <item><b>1.12.1, the loader script</b> — the tuned Classic package starts through <c>launch.sh</c>,
/// and that script writes the realmlist AGAIN from its own default
/// (<c>REALMLIST="${REALMLIST:-play.stonetavern.app}"</c>, then <c>set_cfg realmList</c> and
/// <c>echo &gt; realmlist.wtf</c>). It therefore overwrites whatever the launcher just wrote, unless the
/// launcher hands it <see cref="RealmlistEnvVar"/>. This is the silent failure the whole file is named
/// after: everything the launcher did was correct, on disk, and then undone one second later.</item>
/// <item><b>1.14.2, the proxy</b> — the modern client never speaks to the realm directly. It talks to
/// HermesProxy/JimsProxy on 127.0.0.1, and the proxy's OWN <c>ServerAddress</c> decides which realm that
/// is. It ships pinned to Stonetavern, so a custom realm was ignored outright.</item>
/// </list>
///
/// <para><b>Fail-closed.</b> When a sink cannot be pointed at the selected realm, the launch is refused
/// rather than silently sent to the shipped address. Connecting a player to a different server than the
/// one they picked is exactly the class of "plausible but wrong" outcome that must never look like
/// success.</para>
/// </summary>
public static class RealmBinding
{
    /// <summary>The variable the Classic loader script reads its realmlist from (see class remarks).
    /// Setting it makes the script write the SAME address the launcher wrote, instead of its default.</summary>
    public const string RealmlistEnvVar = "REALMLIST";

    /// <summary>Keys in the proxy config that carry the upstream realm.</summary>
    private const string ServerAddressKey = "ServerAddress";
    private const string ServerPortKey = "ServerPort";

    /// <summary>
    /// The address a launch must actually use. The realm the player selected wins — including a preset
    /// whose address they edited — and the manifest's <c>realmlist</c> is only consulted for a preset
    /// that still carries its shipped address, which is how the operator can move a realm without
    /// shipping a new launcher. A player's own realm is never overridden by a manifest.
    /// </summary>
    /// <param name="realm">The selected realm entry.</param>
    /// <param name="shippedAddress">The address this realm ships with, when it is a preset (null for
    /// custom realms and for presets the launcher no longer ships).</param>
    /// <param name="manifestRealmlist">The realmlist the manifest names for the active phase, if any.</param>
    public static string Effective(RealmEntry realm, string? shippedAddress, string? manifestRealmlist)
    {
        ArgumentNullException.ThrowIfNull(realm);
        var own = realm.RealmlistAddress?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(manifestRealmlist)) return own;

        var isUntouchedPreset = realm.IsPreset
                                && !string.IsNullOrWhiteSpace(shippedAddress)
                                && string.Equals(own, shippedAddress!.Trim(), StringComparison.OrdinalIgnoreCase);

        return isUntouchedPreset ? manifestRealmlist!.Trim() : own;
    }

    /// <summary>
    /// Point the proxy at <paramref name="address"/> and PROVE it landed: rewrite
    /// <c>ServerAddress</c> (and <c>ServerPort</c> when the address names one) in the proxy config, then
    /// read the file back and confirm the values. Returns false with a player-facing
    /// <paramref name="error"/> when the config is missing, unwritable, or does not read back as
    /// expected — the caller then refuses the launch instead of starting a proxy aimed elsewhere.
    /// </summary>
    public static bool PointProxyAtRealm(string configPath, RealmAddress address, out string error)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (!File.Exists(configPath))
        {
            error = ProxyConfigMissingMessage(configPath);
            return false;
        }

        var wanted = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ServerAddressKey] = address.Host,
        };
        // Only touch the port when the realm actually names one. A realm given as a bare host keeps the
        // port the package ships with (3724 for a vMaNGOS auth server) rather than having it guessed.
        if (address.Port is int port) wanted[ServerPortKey] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (!ProxyEndpointConfig.WriteKeys(configPath, wanted))
        {
            error = ProxyConfigUnwritableMessage(configPath);
            return false;
        }

        foreach (var (key, value) in wanted)
        {
            var readBack = ProxyEndpointConfig.ReadKey(configPath, key);
            if (!string.Equals(readBack?.Trim(), value, StringComparison.OrdinalIgnoreCase))
            {
                error = ProxyReadbackMessage(configPath, key, value, readBack);
                return false;
            }
        }

        error = "";
        return true;
    }

    /// <summary>
    /// Write the selected realm into the 1.12.1 client itself and PROVE it landed: <c>SET realmList</c>
    /// in <c>WTF/Config.wtf</c> plus <c>set realmlist &lt;addr&gt;</c> in <c>realmlist.wtf</c> — byte-for-byte
    /// the two writes <c>launch.sh</c> performs (<c>set_cfg realmList</c>, then
    /// <c>echo "set realmlist $REALMLIST" &gt; realmlist.wtf</c>), LF line endings included, because the
    /// client reads both files and a CRLF-quoted value is not what the proven package produces.
    ///
    /// <para><b>Why the launcher does this itself and does not trust the loader script.</b> The Windows
    /// <c>launch.bat</c> shipped in every client package up to 2026-08-09 reads <see cref="RealmlistEnvVar"/>
    /// nowhere — it only detects the display and starts VanillaFixes. A player who already has such a
    /// package would silently land on whatever realm the files carry, no matter which realm the launcher
    /// shows. Handing the batch its environment is necessary but not sufficient; the launcher must own
    /// the write for a file it does control. A newer batch that writes the same value from the same
    /// variable is idempotent with this.</para>
    ///
    /// <para>Returns false with a player-facing <paramref name="error"/> when the client folder is
    /// read-only or the value does not read back — the caller then refuses the launch instead of starting
    /// a client aimed at a different realm.</para>
    /// </summary>
    public static bool WriteClientRealm(string clientDirectory, RealmAddress address, out string error)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (string.IsNullOrWhiteSpace(clientDirectory))
        {
            error = ClientRealmUnwritableMessage(clientDirectory ?? "", "no client folder was resolved");
            return false;
        }

        var configPath = Path.Combine(clientDirectory, "WTF", "Config.wtf");
        var realmlistPath = Path.Combine(clientDirectory, "realmlist.wtf");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            WtfFile.SetVar(configPath, "realmList", address.Value);
            File.WriteAllText(realmlistPath, $"set realmlist {address.Value}\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException)
        {
            error = ClientRealmUnwritableMessage(clientDirectory, ex.Message);
            return false;
        }

        // "written" is not "landed": read both back. A read-only file on Windows can fail without an
        // exception path we recognise, and a stale value here is the exact silent wrong-realm start.
        //
        // The READBACK itself can throw (Codex review 2026-08-09, finding 5): the write can succeed and
        // the read a moment later still fail — an antivirus or the game holding the file exclusively,
        // ACLs changed between the two calls. Outside a try that exception left LaunchAsync as an
        // unhandled crash of the Play path: no message, and the UI stuck in "launching". So the read is
        // inside the guard and a failed read becomes the same readable refusal as a failed write.
        //
        // EVERY realmList line has to agree, not just the first (finding 1) — WtfFile.ReadVar returns
        // null on an ambiguous file, and the values are fetched separately so the error can name what
        // is actually in there instead of claiming "nothing".
        IReadOnlyList<string> configValues;
        string? realmlistValue;
        try
        {
            configValues = WtfFile.ReadValues(configPath, "realmList");
            realmlistValue = File.ReadAllText(realmlistPath).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException)
        {
            error = ClientRealmUnwritableMessage(clientDirectory, ex.Message);
            return false;
        }

        var agreeing = configValues.Count > 0
                       && configValues.All(v => string.Equals(v.Trim(), address.Value, StringComparison.OrdinalIgnoreCase));
        if (!agreeing)
        {
            error = ClientRealmReadbackMessage(
                configPath, address.Value,
                configValues.Count == 0 ? null : string.Join(", ", configValues));
            return false;
        }

        var expectedLine = $"set realmlist {address.Value}";
        if (!string.Equals(realmlistValue, expectedLine, StringComparison.OrdinalIgnoreCase))
        {
            error = ClientRealmReadbackMessage(realmlistPath, expectedLine, realmlistValue);
            return false;
        }

        error = "";
        return true;
    }

    /// <summary>The environment the loader script must run with so its own realmlist write agrees with
    /// the launcher's. Empty when there is no valid address — the caller has already refused by then.</summary>
    public static IReadOnlyDictionary<string, string> LoaderEnvironment(RealmAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return new Dictionary<string, string>(StringComparer.Ordinal) { [RealmlistEnvVar] = address.Value };
    }

    private static string ClientRealmUnwritableMessage(string clientDirectory, string reason) =>
        "The launcher could not write the realm address into the client, so it did not start the game " +
        "(it would otherwise connect to a different server than the one you picked).\n" +
        $"Client folder: {clientDirectory}\n" +
        $"Reason: {reason}\n" +
        "Check that the client folder is not read-only, then try again.";

    private static string ClientRealmReadbackMessage(string path, string expected, string? actual) =>
        "The realm address did not stick in the client's configuration, so the launcher did not start " +
        "the game (it would otherwise connect to a different server than the one you picked).\n" +
        $"Expected: {expected}, found {(string.IsNullOrWhiteSpace(actual) ? "nothing" : actual)}\n" +
        $"File: {path}";

    private static string ProxyConfigMissingMessage(string configPath) =>
        "The realm proxy has no configuration file, so the launcher cannot point it at this realm.\n" +
        $"Expected: {configPath}\n" +
        "Download the client package again and extract all of it.";

    private static string ProxyConfigUnwritableMessage(string configPath) =>
        "The launcher could not set the realm address for the proxy, so it did not start the client " +
        "(it would otherwise connect to a different server than the one you picked).\n" +
        $"Config file: {configPath}\n" +
        "Check that the client folder is not read-only, then try again.";

    private static string ProxyReadbackMessage(string configPath, string key, string expected, string? actual) =>
        "The realm address for the proxy did not stick, so the launcher did not start the client (it " +
        "would otherwise connect to a different server than the one you picked).\n" +
        $"{key}: expected {expected}, found {(string.IsNullOrWhiteSpace(actual) ? "nothing" : actual)}\n" +
        $"Config file: {configPath}";
}
