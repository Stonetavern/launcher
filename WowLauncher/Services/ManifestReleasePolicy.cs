namespace WowLauncher.Services;

using System.Text.Json;
using WowLauncher.Models;
using WowLauncher.Services.Platform;

/// <summary>Why a signed manifest was admitted or turned away. Separate from
/// <see cref="SignatureVerdict"/> on purpose: a valid signature and an admissible release are two
/// different questions, and conflating them is exactly the mistake this type exists to correct.</summary>
public readonly record struct ReleaseVerdict(bool Ok, string Reason)
{
    public static ReleaseVerdict Admitted { get; } = new(true, "release admitted");

    public static ReleaseVerdict Refuse(string reason) => new(false, reason);
}

/// <summary>
/// Remembers the highest manifest serial this installation ever accepted, so a later replay of an
/// older — perfectly valid, perfectly signed — manifest can be recognised as old.
///
/// <para><b>Why its own file and not <c>launcher_config.json</c>.</b> The config is the player's
/// document: they edit it, copy it between machines, and the launcher rewrites it on every setting
/// change. An anti-rollback floor stored there would be trivially reset by hand and would churn with
/// unrelated saves. This lives under <see cref="IAppPaths.StateDir"/> as launcher-owned state.</para>
/// </summary>
public interface IManifestTrustStore
{
    /// <summary>The highest serial ever accepted (0 when nothing was ever recorded), or <c>null</c>
    /// when a file exists but could not be read. Null is a refusal, never "start from zero" — an
    /// unreadable floor would silently disable rollback protection, which is the precise failure an
    /// attacker with local write access would engineer.</summary>
    long? ReadHighestSerial();

    /// <summary>Raises the stored floor to <paramref name="serial"/> (never lowers it).</summary>
    void Remember(long serial);
}

/// <inheritdoc cref="IManifestTrustStore"/>
public sealed class ManifestTrustStore : IManifestTrustStore
{
    private readonly string _path;
    private readonly Serilog.ILogger _log;

    public ManifestTrustStore(IAppPaths paths, Serilog.ILogger log)
    {
        _path = Path.Combine(paths.StateDir, "manifest-trust.json");
        _log = log;
    }

    /// <summary>Public so an operator-facing message can name the exact file to delete on recovery.</summary>
    public string StatePath => _path;

    public long? ReadHighestSerial()
    {
        try
        {
            if (!File.Exists(_path))
                return 0; // first run: no floor yet, any serial is a legitimate starting point

            var json = File.ReadAllText(_path);
            var state = JsonSerializer.Deserialize<TrustState>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (state is null)
            {
                _log.Error("Anti-Rollback-Zustand {Path} ist leer/unlesbar — Update wird verweigert", _path);
                return null;
            }
            return state.HighestSerial;
        }
        catch (Exception ex)
        {
            // Writes are atomic (temp + rename), so a corrupt file here is not the ordinary result of a
            // power cut — it is a signal. Refusing costs updates until the operator or player deletes
            // the file; accepting would hand an attacker the rollback protection for free.
            _log.Error(ex, "Anti-Rollback-Zustand {Path} nicht lesbar — Update wird verweigert. " +
                "Behebung: Datei löschen, der Launcher legt sie neu an", _path);
            return null;
        }
    }

    public void Remember(long serial)
    {
        try
        {
            var current = ReadHighestSerial();
            if (current is not null && current >= serial)
                return; // never lower the floor, and never rewrite the file for nothing

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            // Temp + rename: a half-written floor would be read as corrupt on the next start and (by
            // the rule above) block updates. Same pattern ConfigService uses, same reason.
            var tmp = $"{_path}.{Environment.ProcessId}.tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new TrustState { HighestSerial = serial }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            // A floor we cannot persist means the next start re-accepts the same serial — no worse than
            // today, and not a reason to refuse an update that already passed every check.
            _log.Warning(ex, "Anti-Rollback-Zustand {Path} nicht schreibbar", _path);
        }
    }

    private sealed class TrustState
    {
        [System.Text.Json.Serialization.JsonPropertyName("highest_serial")]
        public long HighestSerial { get; set; }
    }
}

/// <summary>
/// The release policy that runs AFTER the signature check, on the manifest parsed from the very bytes
/// that signature covered. Codex review 2026-07-27: a signature answers "did we publish this?" and
/// nothing else. Three bindings turn that into "may this client install this, now?":
///
/// <list type="number">
/// <item><b>Anti-Rollback</b> — a monotone <c>serial</c> against a locally remembered high-water mark.
/// Without it, whoever controls delivery replays yesterday's correctly signed manifest and pins every
/// player on a vulnerable build; the signature check waves it through because it IS genuine.</item>
/// <item><b>Expiry</b> — an <c>expires</c> timestamp, so a captured manifest stops being usable on its
/// own instead of living until somebody notices.</item>
/// <item><b>Channel</b> — the manifest names the channel it belongs to and must match this build's.
/// The day the same key signs staging, a production client must not install it.</item>
/// </list>
///
/// A missing field is a refusal in every case. Treating absence as "policy not applicable" would let
/// an attacker disable each check by deleting a line — and the manifest is attacker-influenced input.
/// </summary>
public sealed class ManifestReleasePolicy
{
    /// <summary>The channel this build belongs to. A constant today because the launcher ships one
    /// channel; when beta/canary arrive this reads from the config (and the config value is then a
    /// player-visible setting, not a security boundary — the manifest still has to agree).</summary>
    public const string LauncherChannel = "stable";

    /// <summary>
    /// How far past <c>expires</c> a manifest is still accepted. 24 h, chosen against the two clocks
    /// that are actually wrong in the field: a machine with a dead CMOS battery or a resumed VM can sit
    /// hours off, and a Windows box configured with UTC-vs-local confusion lands a whole timezone out.
    /// Anything under a day would turn a correctly published manifest into "no updates" for those
    /// players; anything much larger stops bounding the replay window meaningfully. It only ever
    /// EXTENDS a manifest's life — a manifest is never accepted before it exists.
    /// </summary>
    public static readonly TimeSpan ExpiryGrace = TimeSpan.FromHours(24);

    private readonly IManifestTrustStore _store;
    private readonly Serilog.ILogger _log;
    private readonly string _channel;
    private readonly TimeProvider _time;

    public ManifestReleasePolicy(IManifestTrustStore store, Serilog.ILogger log,
        string? channel = null, TimeProvider? time = null)
    {
        _store = store;
        _log = log;
        _channel = channel ?? LauncherChannel;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Decides whether an already-signature-verified <paramref name="manifest"/> may be acted on, and
    /// — only on success — raises the stored anti-rollback floor. Persisting inside the admission is
    /// deliberate: a caller that had to remember a second call is a caller that will eventually forget,
    /// and a rejected manifest must never touch the floor.
    /// </summary>
    public ReleaseVerdict Admit(ServerManifest manifest)
    {
        if (manifest.Serial is not { } serial)
            return ReleaseVerdict.Refuse("manifest has no serial (anti-rollback field is mandatory)");
        if (serial < 0)
            return ReleaseVerdict.Refuse($"manifest serial {serial} is negative");

        if (string.IsNullOrWhiteSpace(manifest.Channel))
            return ReleaseVerdict.Refuse("manifest has no channel");
        // Case-insensitive: "Stable" instead of "stable" is an authoring slip that would otherwise
        // brick a release, and leniency here buys an attacker nothing — the value is signed either way.
        if (!string.Equals(manifest.Channel.Trim(), _channel, StringComparison.OrdinalIgnoreCase))
            return ReleaseVerdict.Refuse(
                $"manifest is for channel '{manifest.Channel}', this launcher is on '{_channel}'");

        if (string.IsNullOrWhiteSpace(manifest.Expires))
            return ReleaseVerdict.Refuse("manifest has no expires timestamp");
        if (!DateTimeOffset.TryParse(manifest.Expires, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var expires))
            return ReleaseVerdict.Refuse($"manifest expires '{manifest.Expires}' is not an ISO-8601 timestamp");
        var now = _time.GetUtcNow();
        if (now > expires + ExpiryGrace)
            return ReleaseVerdict.Refuse($"manifest expired at {expires:O} (now {now:O})");

        var floor = _store.ReadHighestSerial();
        if (floor is null)
            return ReleaseVerdict.Refuse("anti-rollback state unreadable");
        if (serial < floor)
            return ReleaseVerdict.Refuse(
                $"manifest serial {serial} is older than the highest already accepted ({floor}) — replay refused");

        // Equal serial is the ordinary case: the same release fetched again on the next start.
        _store.Remember(serial);
        _log.Information("Manifest-Release zugelassen: serial {Serial}, Kanal {Channel}, gültig bis {Expires}",
            serial, manifest.Channel, expires);
        return ReleaseVerdict.Admitted;
    }
}
