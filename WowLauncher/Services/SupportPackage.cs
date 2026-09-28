using System.IO.Compression;
using System.Text;
using WowLauncher.Services.Platform;

namespace WowLauncher.Services;

/// <summary>
/// "Save a support package" (owner 2026-09-28, TODO C4): one zip with every log the launcher and the
/// game leave behind, so a player can hand support everything at once instead of being asked for one
/// file after the other. Above all the realm proxy logs (JimsProxy / HermesProxy): a proxy failure
/// looks like a clean launch in the launcher log, and the proxy log is the only place it explains itself.
///
/// <para>What goes in: the launcher logs and state files, the proxy output logs, each installed
/// client's <c>Logs/</c>, its <c>WTF/Config.wtf</c> and the proxy logs in its package, the start
/// report, and the launcher config. What does not: passwords and tokens (never in these files, and
/// <see cref="ProblemReport.Redact"/> runs over every text anyway), the account name (removed), and the
/// home folder, which is written as <c>~</c> so the user name does not travel either.</para>
///
/// <para>Nothing is sent anywhere. The player gets a file and decides where it goes.</para>
/// </summary>
public sealed class SupportPackage
{
    /// <summary>Per file: the END of a longer file, which is where the failure is.</summary>
    public const long MaxFileBytes = 4L * 1024 * 1024;

    private readonly IAppPaths _paths;
    private readonly IConfigService _config;
    private readonly StartReport? _startReport;
    private readonly Serilog.ILogger _log;
    private readonly string _home;
    private readonly Func<DateTimeOffset> _now;

    public SupportPackage(IAppPaths paths, IConfigService config, StartReport? startReport, Serilog.ILogger log,
                          string? home = null, Func<DateTimeOffset>? now = null)
    {
        _paths = paths;
        _config = config;
        _startReport = startReport;
        _log = log;
        _home = home ?? LibraryProbe.HomeDir();
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <summary>The folder the launcher writes its logs to, for "Open log folder".</summary>
    public string LogDir => _paths.LogDir;

    /// <summary>Where the package lands: the desktop, where a player finds it again, else home.</summary>
    public static string DefaultDestination()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return !string.IsNullOrEmpty(desktop) && Directory.Exists(desktop) ? desktop : LibraryProbe.HomeDir();
    }

    /// <summary>Every file that goes in, with its name inside the zip. Read-only.</summary>
    internal IReadOnlyList<(string Source, string Entry)> Collect()
    {
        var files = new List<(string, string)>();
        void AddMatching(string dir, string entryDir, params string[] patterns)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var pattern in patterns)
                    foreach (var f in Directory.EnumerateFiles(dir, pattern))
                        files.Add((f, $"{entryDir}/{Path.GetFileName(f)}"));
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "Support package: could not list {Dir}", dir);
            }
        }

        // The launcher: its logs (incl. the crash log and the swap/forward notes) and update state.
        AddMatching(_paths.LogDir, "launcher", "*.log", "*.log.1", "update-attempts.txt", "update-check.json");
        if (!string.Equals(_paths.StateDir, _paths.LogDir, StringComparison.Ordinal))
            AddMatching(_paths.StateDir, "launcher", "update-attempts.txt", "update-check.json", "*.log");

        // Each installed client: its own logs, its settings, and the proxy logs in its package.
        foreach (var (build, dir) in _config.Load().ClientInstalls)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
            var prefix = $"client-{build}";
            AddMatching(Path.Combine(dir, "Logs"), $"{prefix}/Logs", "*");
            AddMatching(Path.Combine(dir, "WTF"), $"{prefix}/WTF", "Config.wtf");
            foreach (var hermes in ProxyDirsFor(dir))
            {
                AddMatching(hermes, $"{prefix}/proxy", "*.log", "*.txt", "*.config");
                AddMatching(Path.Combine(hermes, "Logs"), $"{prefix}/proxy/Logs", "*");
            }
        }

        return files.DistinctBy(f => f.Item2, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The proxy folder of a client package: <c>Hermes/</c> next to the client, or up to two
    /// levels above it (1.14.2 lives in <c>World of Warcraft/_classic_era_</c> inside the package).</summary>
    private static IEnumerable<string> ProxyDirsFor(string clientDir)
    {
        var dir = new DirectoryInfo(clientDir);
        for (var i = 0; i < 3 && dir is not null; i++, dir = dir.Parent)
        {
            var hermes = Path.Combine(dir.FullName, "Hermes");
            if (Directory.Exists(hermes)) yield return hermes;
        }
    }

    /// <summary>Write the package into <paramref name="destDir"/>; returns its path.</summary>
    public async Task<string> CreateAsync(string destDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destDir);
        var path = Path.Combine(destDir, $"stonetavern-support-{_now():yyyyMMdd-HHmmss}.zip");
        var tmp = path + ".part";
        var account = _config.Load().LastAccountName;

        await using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var (source, entry) in Collect())
            {
                ct.ThrowIfCancellationRequested();
                var text = await ReadTailAsync(source, ct).ConfigureAwait(false);
                if (text is null) continue;
                if (entry.EndsWith("Config.wtf", StringComparison.OrdinalIgnoreCase)) text = RedactWtf(text);
                await WriteEntryAsync(zip, entry, Clean(text, account), ct).ConfigureAwait(false);
            }

            await WriteEntryAsync(zip, "launcher/launcher_config.json", Clean(ConfigText(), account), ct).ConfigureAwait(false);
            await WriteEntryAsync(zip, "system.txt", Clean(SystemText(), account), ct).ConfigureAwait(false);
        }

        File.Move(tmp, path, overwrite: true);
        _log.Information("Support package written to {Path}", path);
        return path;
    }

    private static async Task WriteEntryAsync(ZipArchive zip, string name, string text, CancellationToken ct)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        await w.WriteAsync(text.AsMemory(), ct).ConfigureAwait(false);
    }

    /// <summary>The file, or its last <see cref="MaxFileBytes"/> when longer (marked as cut), read
    /// through a shared handle: the launcher and the proxy may be writing to it right now.</summary>
    private static async Task<string?> ReadTailAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var cut = fs.Length > MaxFileBytes;
            if (cut) fs.Seek(-MaxFileBytes, SeekOrigin.End);
            using var r = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = await r.ReadToEndAsync(ct).ConfigureAwait(false);
            return cut ? $"[only the last {MaxFileBytes / (1024 * 1024)} MB of this file]\n" + text : text;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Credentials (the problem report rules), the account name, and the home folder.</summary>
    internal string Clean(string text, string? account)
    {
        text = ProblemReport.Redact(text);
        if (!string.IsNullOrWhiteSpace(account) && account.Length >= 3)
            text = text.Replace(account, "<account>", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(_home) && _home.Length > 1)
        {
            text = text.Replace(_home, "~", StringComparison.Ordinal);
            // Windows paths also appear with the other slash and as Z:\ under Wine.
            text = text.Replace(_home.Replace('\\', '/'), "~", StringComparison.OrdinalIgnoreCase)
                       .Replace("Z:" + _home.Replace('/', '\\'), "~", StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }

    /// <summary>Config.wtf keeps the last account and character: those lines go, the graphics and
    /// realmlist lines (what support needs) stay.</summary>
    internal static string RedactWtf(string text) =>
        string.Join('\n', text.Split('\n').Select(line =>
        {
            var t = line.TrimStart();
            return t.StartsWith("SET accountName", StringComparison.OrdinalIgnoreCase)
                   || t.StartsWith("SET accountList", StringComparison.OrdinalIgnoreCase)
                   || t.StartsWith("SET lastCharacterIndex", StringComparison.OrdinalIgnoreCase)
                ? "SET <removed>"
                : line;
        }));

    private string ConfigText()
    {
        try
        {
            var cfg = _config.Load();
            cfg.LastAccountName = "";
            return System.Text.Json.JsonSerializer.Serialize(cfg, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return "config could not be read: " + ex.GetType().Name;
        }
    }

    private string SystemText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Created: {_now():O}");
        sb.AppendLine($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})");
        sb.AppendLine($".NET: {Environment.Version}");
        sb.AppendLine($"Stonetavern folder: {Program.Library.Kind} {Program.Library.Root}");
        try
        {
            if (_startReport is not null) sb.AppendLine().AppendLine(StartReport.Format(_startReport.Build()));
        }
        catch (Exception ex)
        {
            sb.AppendLine("start report failed: " + ex.GetType().Name);
        }
        return sb.ToString();
    }
}
