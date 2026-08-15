namespace WowLauncher.Services;

using System.Text;
using WowLauncher.Models;
using WowLauncher.Services.Platform;

/// <summary>
/// The facts that decide why a start went the way it did, in one block a player can copy.
///
/// <para><b>Why this exists.</b> The most common report is "I am logged in but I never get into the
/// world", and it arrives as a screenshot of a window that shows none of the six things that answer
/// it: which build is installed, which address the realm points at, which addon set is on disk, which
/// Wine actually runs the game, whether the realm goes through the proxy, and where the config lives.
/// Every one of those is known to the launcher and to nobody else. A screenshot costs an evening of
/// back and forth; this costs one click.</para>
///
/// <para><b>Why it is not the problem report.</b> <see cref="ProblemReport"/> sends a log to us and
/// needs a network, a mailbox and a working account. This one never leaves the machine: it is text,
/// the player pastes it wherever they are already asking (Discord, a forum, a friend). It also stays
/// readable, so nobody is asked to forward something they cannot check.</para>
///
/// <para><b>What it leaves out.</b> No log, no token, no account name. Only the state, and only the
/// state the launcher would act on right now. It reads the addon set from the DISK marker rather than
/// from the config, because the disk is what the game loads and a config that disagrees is exactly the
/// kind of plausible wrong answer that sends someone hunting the wrong thing.</para>
///
/// <para>Pure formatting on top of a plain record, so what a player pastes can be asserted in a
/// test.</para>
/// </summary>
public sealed record StartReportFacts(
    string LauncherVersion,
    string Os,
    string Desktop,
    string RealmName,
    string RealmAddress,
    bool RealmIsPreset,
    string ClientLabel,
    string ClientPath,
    string AddonSet,
    string RuntimeLine,
    string ProxyLine,
    string ConfigPath,
    string UpdateCheckLine);

/// <summary>Collects <see cref="StartReportFacts"/> from the launcher's live state and formats them.</summary>
public sealed class StartReport
{
    /// <summary>Stands in for anything the launcher genuinely does not know. Spelled out rather than
    /// left blank: an empty line reads as "nothing to report" when it means "not set up yet", and the
    /// difference is usually the answer.</summary>
    public const string Unknown = "not set";

    private readonly IConfigService _config;
    private readonly IAppPaths _paths;
    private readonly Func<string> _version;
    private readonly Func<string?> _addonSet;
    private readonly Func<ClientVersion, string> _runtimeFor;
    private readonly IUpdateCheckLog? _checkLog;

    /// <param name="addonSet">Reads the set marker off the client folder. Null when there is no client
    /// or no marker.</param>
    /// <param name="runtimeFor">Which binary would start the given build, already described. Empty off
    /// Linux, where the question does not exist.</param>
    /// <param name="checkLog">When the launcher last reached the update server, and what was on
    /// offer then. Optional; without it the report simply carries no such line.</param>
    public StartReport(IConfigService config, IAppPaths paths,
        Func<string?>? addonSet = null,
        Func<ClientVersion, string>? runtimeFor = null,
        Func<string>? version = null,
        IUpdateCheckLog? checkLog = null)
    {
        _checkLog = checkLog;
        _config = config;
        _paths = paths;
        _addonSet = addonSet ?? (() => null);
        _runtimeFor = runtimeFor ?? (_ => "");
        _version = version ?? (() =>
        {
            var v = UpdateService.RunningVersion(System.Reflection.Assembly.GetExecutingAssembly());
            return $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
        });
    }

    public StartReportFacts Build()
    {
        var cfg = _config.Load();
        var realm = RealmRegistry.All(cfg).FirstOrDefault(r => r.Id == cfg.SelectedRealmId);
        var client = realm?.Client ?? ClientVersion.Default;

        var installed = cfg.ClientInstalls.TryGetValue(client.Build, out var dir) && dir.Length > 0;

        return new StartReportFacts(
            LauncherVersion: _version(),
            Os: OsLine(),
            Desktop: DesktopName(),
            RealmName: realm?.Name ?? Unknown,
            // The flat field every service reads, projected from the selected realm by
            // RealmRegistry.ApplyActiveRealm - so this is the address the launch itself acts on, not a
            // second reading of the realm list beside it.
            //
            // Empty is a real state and a diagnosis in itself: a stored realm entry with no address
            // starts into nothing. It has to SAY that, because an empty line after "Address:" reads as
            // a formatting glitch and gets skipped by the person who could have spotted it.
            RealmAddress: cfg.RealmlistAddress.Trim().Length > 0 ? cfg.RealmlistAddress.Trim() : Unknown,
            RealmIsPreset: realm?.IsPreset ?? false,
            ClientLabel: client.PreciseLabel,
            ClientPath: installed ? dir! : Unknown,
            AddonSet: _addonSet() ?? Unknown,
            RuntimeLine: _runtimeFor(client),
            ProxyLine: client.NeedsModernRuntime ? "through the realm proxy" : "direct",
            ConfigPath: _paths.ConfigFilePath,
            // Die Zeile, die "warum bekomme ich den Fix nicht" beantwortet, ohne dass jemand nachfragen
            // muss. Ein Datum statt "vor N Tagen": der Bericht wird gelesen, wann er gelesen wird, und
            // eine relative Angabe waere dann falsch.
            UpdateCheckLine: _checkLog is null ? "" : DescribeCheck(_checkLog));
    }

    private static string DescribeCheck(IUpdateCheckLog log)
    {
        if (log.LastSuccess is not { } when) return "never reached the update server";
        var offered = log.LastOffered.Length > 0 ? log.LastOffered : "no version named";
        return $"{when.UtcDateTime:yyyy-MM-dd HH:mm} UTC, offered {offered}";
    }

    /// <summary>
    /// The system, named the way its owner would name it.
    ///
    /// <para>Without the distribution this line reads <c>Unix 7.1.4.204 x64</c>, which is true and
    /// useless: on Linux almost every start problem is a packaging question (which Wine, which vulkan
    /// driver, which glibc), and those are answered by the distribution, not by a kernel number. The
    /// kernel stays because it is the one thing a bug report about a driver needs.</para>
    /// </summary>
    private static string OsLine()
    {
        var arch = Environment.Is64BitOperatingSystem ? "x64" : "x86";
        var distro = OperatingSystem.IsLinux() ? PrettyName(ReadOsRelease()) : "";
        return distro.Length > 0
            ? $"{distro}, kernel {Environment.OSVersion.Version} {arch}"
            : $"{Environment.OSVersion.VersionString} {arch}";
    }

    private static string? ReadOsRelease()
    {
        try
        {
            // /usr/lib is the fallback the standard names for images that keep /etc minimal.
            foreach (var path in new[] { "/etc/os-release", "/usr/lib/os-release" })
                if (File.Exists(path)) return File.ReadAllText(path);
        }
        catch (Exception)
        {
            // A report without a distribution name is still a report.
        }
        return null;
    }

    /// <summary>Pulls PRETTY_NAME out of an os-release file. Separate and internal so the parsing can
    /// be tested without a machine that happens to run the right distribution.</summary>
    internal static string PrettyName(string? osRelease)
    {
        if (string.IsNullOrWhiteSpace(osRelease)) return "";
        foreach (var raw in osRelease.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal)) continue;
            return line["PRETTY_NAME=".Length..].Trim().Trim('"');
        }
        return "";
    }

    /// <summary>The desktop the launcher runs on, which decides more than it should (tray behaviour,
    /// window decorations, the file picker). Empty off Linux, where the answer is the OS line already.</summary>
    private static string DesktopName()
    {
        if (!OperatingSystem.IsLinux()) return "";
        var xdg = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
        var session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        var parts = new[] { xdg, session }.Where(s => !string.IsNullOrWhiteSpace(s));
        return string.Join(" ", parts);
    }

    /// <summary>
    /// What lands on the clipboard. Plain text on purpose: it has to survive being pasted into
    /// Discord, a forum box and a mail client without any of them turning it into a table.
    /// </summary>
    public static string Format(StartReportFacts f)
    {
        var sb = new StringBuilder();
        sb.Append("Stonetavern launcher ").Append(f.LauncherVersion).Append('\n');
        sb.Append(f.Os);
        if (f.Desktop.Length > 0) sb.Append(", ").Append(f.Desktop);
        sb.Append('\n');
        sb.Append("Realm: ").Append(f.RealmName).Append(" (")
          .Append(f.RealmIsPreset ? "shipped" : "added by hand").Append(")\n");
        sb.Append("Address: ").Append(f.RealmAddress).Append('\n');
        sb.Append("Connection: ").Append(f.ProxyLine).Append('\n');
        sb.Append("Client: ").Append(f.ClientLabel).Append('\n');
        sb.Append("Client folder: ").Append(f.ClientPath).Append('\n');
        sb.Append("Addon set: ").Append(f.AddonSet).Append('\n');
        if (f.RuntimeLine.Length > 0) sb.Append("Runner: ").Append(f.RuntimeLine).Append('\n');
        sb.Append("Config: ").Append(f.ConfigPath).Append('\n');
        if (f.UpdateCheckLine.Length > 0)
            sb.Append("Update check: ").Append(f.UpdateCheckLine).Append('\n');
        return sb.ToString();
    }
}
