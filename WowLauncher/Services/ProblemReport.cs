namespace WowLauncher.Services;

using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using WowLauncher.Services.Platform;

/// <summary>
/// What the launcher knows about itself when a player says "it does not work".
///
/// <para><b>Why this exists.</b> A player who reports a problem can describe what they saw and
/// nothing else. They cannot say which build they run, whether the manifest signature verified,
/// where their client sits, or what the last thing was that failed — and those four answers are
/// usually the whole diagnosis. The launcher knows all of it. This collects it so the report is
/// worth answering.</para>
///
/// <para><b>What it deliberately leaves out.</b> The session token lives in its own file
/// (<c>launcher_session.dat</c>) and is never read here. The log is redacted on top of that, because
/// "the log has no secrets today" is a statement about today: a line added next year that logs a
/// header or a query string would otherwise start shipping credentials to a mailbox, silently and
/// forever. The redaction is deliberately blunt — a false positive costs a diagnostic line, a false
/// negative costs a credential.</para>
///
/// <para>Pure: it reads files and formats a string. No network, no UI, so what the player sends can
/// be asserted in a test.</para>
/// </summary>
public sealed class ProblemReport
{
    /// <summary>Log lines to include. Enough to cover a full start plus what the player did next;
    /// past that a reader is scrolling, not reading, and the mail gets refused for size.</summary>
    public const int LogLines = 250;

    /// <summary>What the player typed is capped too, so one paste of a wall of text cannot push the
    /// request over the server's size limit and lose the whole report.</summary>
    public const int MaxMessageChars = 4000;

    private readonly IAppPaths _paths;
    private readonly IConfigService _config;
    private readonly Func<string> _version;

    public ProblemReport(IAppPaths paths, IConfigService config, Func<string>? version = null)
    {
        _paths = paths;
        _config = config;
        _version = version ?? (() =>
        {
            var v = UpdateService.RunningVersion(System.Reflection.Assembly.GetExecutingAssembly());
            return $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
        });
    }

    /// <summary>
    /// Anything shaped like a credential goes, whatever the surrounding line says. Matched
    /// case-insensitively on the KEY, because the value of a token is by definition unpredictable and
    /// the only reliable handle is the name in front of it.
    /// </summary>
    // Order matters, and both orderings below were wrong once. The bearer rule has to run BEFORE the
    // key/value rule: "Authorization: Bearer <token>" otherwise matches the key rule first, which
    // treats the word "Bearer" as the value, redacts that, and leaves the token standing in plain
    // sight. Found by the test, not by reading.

    /// <summary>A bearer header, wherever it appears. Runs first, see above.</summary>
    private static readonly Regex BearerToken =
        new(@"(?i)\bbearer\s+[A-Za-z0-9._~+/=-]{6,}", RegexOptions.Compiled);

    /// <summary>key: value, key = value, "key": "value". The quotes around the KEY are optional and
    /// have to be, because a JSON-shaped log line writes the key quoted and an earlier version of
    /// this pattern let every one of those through untouched.</summary>
    private static readonly Regex KeyedSecret =
        new(@"(?i)""?\b(token|secret|password|passwd|pwd|authorization|api[_-]?key|session)\b""?\s*[:=]\s*""?[^\s""',;)]+",
            RegexOptions.Compiled);

    /// <summary>Credentials inside a URL: scheme://user:pass@host</summary>
    private static readonly Regex UrlCredentials =
        new(@"(?i)([a-z][a-z0-9+.-]*://)[^/\s:@]+:[^/\s@]+@", RegexOptions.Compiled);

    /// <summary>A query string carrying one of the above.</summary>
    private static readonly Regex QuerySecret =
        new(@"(?i)([?&](?:token|key|secret|session|auth)=)[^&\s]+", RegexOptions.Compiled);

    /// <summary>Replace anything credential-shaped with a marker that keeps the line readable, so a
    /// reader can see that something was there rather than wondering why a line looks truncated.</summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var result = BearerToken.Replace(text, "bearer [redacted]");
        result = KeyedSecret.Replace(result, m => m.Groups[1].Value + ": [redacted]");
        result = UrlCredentials.Replace(result, "$1[redacted]@");
        result = QuerySecret.Replace(result, "$1[redacted]");
        return result;
    }

    /// <summary>The tail of the current log file, redacted. Empty when there is no log yet — which is
    /// itself worth reporting, so it is not treated as an error.</summary>
    public string RecentLog()
    {
        try
        {
            var newest = new DirectoryInfo(_paths.LogDir)
                .GetFiles("launcher*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is null) return "";

            // Read through a shared handle: Serilog holds the file open while the launcher runs, and
            // a report that throws because the app is still writing its own log would be absurd.
            using var stream = new FileStream(newest.FullName, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new Queue<string>(LogLines);
            while (reader.ReadLine() is { } line)
            {
                lines.Enqueue(line);
                if (lines.Count > LogLines) lines.Dequeue();
            }
            return Redact(string.Join('\n', lines));
        }
        catch (Exception)
        {
            // A report without a log is still a report. Never let collecting diagnostics be the
            // reason a player cannot tell anyone that something is broken.
            return "";
        }
    }

    /// <summary>
    /// The facts the player cannot give, gathered into what the endpoint expects.
    ///
    /// <para>The client state is read from the configuration rather than passed in from the view
    /// model. That is where the launcher records it, so it is the same answer the launcher acts on —
    /// and a report that disagreed with the launcher's own state would send someone hunting a
    /// difference that does not exist.</para>
    /// </summary>
    public ProblemReportPayload Build(string message, string contact)
    {
        var cfg = _config.Load();
        var trimmed = (message ?? "").Trim();
        if (trimmed.Length > MaxMessageChars) trimmed = trimmed[..MaxMessageChars];

        // The build the launcher would start: the one it has an install recorded for. More than one
        // can be installed, so report the highest, which is the newest client.
        var build = cfg.ClientInstalls.Count > 0 ? cfg.ClientInstalls.Keys.Max() : 0;

        return new ProblemReportPayload(
            LauncherVersion: _version(),
            Os: $"{Environment.OSVersion.VersionString} {(Environment.Is64BitOperatingSystem ? "x64" : "x86")}",
            Realm: cfg.SelectedRealmId ?? "",
            ClientBuild: build,
            ClientInstalled: build != 0,
            Message: Redact(trimmed),
            Contact: (contact ?? "").Trim(),
            Log: RecentLog());
    }

    /// <summary>What the player is about to send, as they will see it before they send it. Showing
    /// this is not decoration: a diagnostic bundle a player cannot inspect is one they are right to
    /// refuse, and the log carries their folder names.</summary>
    public static string Preview(ProblemReportPayload p)
    {
        var sb = new StringBuilder();
        sb.Append("Launcher ").Append(p.LauncherVersion).Append('\n');
        sb.Append(p.Os).Append('\n');
        sb.Append("Realm: ").Append(string.IsNullOrEmpty(p.Realm) ? "none" : p.Realm).Append('\n');
        sb.Append("Client: ").Append(p.ClientInstalled ? $"build {p.ClientBuild}" : "not installed").Append('\n');
        if (!string.IsNullOrEmpty(p.Contact)) sb.Append("Contact: ").Append(p.Contact).Append('\n');
        sb.Append('\n');
        if (!string.IsNullOrEmpty(p.Message)) sb.Append(p.Message).Append("\n\n");
        sb.Append("Log, last ").Append(p.Log.Split('\n').Length).Append(" lines:\n");
        sb.Append(p.Log);
        return sb.ToString();
    }
}

/// <summary>
/// The body of POST /api/launcher/report. The names are spelled out rather than left to a naming
/// policy: this is a contract with another codebase, and a serializer option changed three years
/// from now must not silently rename a field the server matches on.
/// </summary>
public sealed record ProblemReportPayload(
    [property: JsonPropertyName("launcherVersion")] string LauncherVersion,
    [property: JsonPropertyName("os")] string Os,
    [property: JsonPropertyName("realm")] string Realm,
    [property: JsonPropertyName("clientBuild")] int ClientBuild,
    [property: JsonPropertyName("clientInstalled")] bool ClientInstalled,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("contact")] string Contact,
    [property: JsonPropertyName("log")] string Log);
