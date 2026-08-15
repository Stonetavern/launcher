namespace WowLauncher.Tests;

using System;
using System.IO;
using System.Linq;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using Xunit;

/// <summary>
/// The problem report is the one feature that takes a player's own files and sends them somewhere.
/// Everything here is about that: it must carry enough to diagnose, and nothing that could log a
/// player in.
///
/// <para>The redaction is tested harder than the rest on purpose. A false positive costs one
/// diagnostic line; a false negative mails a credential to an inbox and keeps doing it for every
/// player, silently, until someone reads a mail carefully.</para>
///
/// <para>Every credential-shaped string below is invented for this file and matches nothing real.</para>
/// </summary>
public sealed class ProblemReportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "st-report-" + Guid.NewGuid().ToString("N"));

    public ProblemReportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private ProblemReport Subject() =>
        new(new Paths(_dir), new MemoryConfig(), () => "1.6.3");

    // ── Redaction ───────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("token: NOT-A-REAL-VALUE-1111", "NOT-A-REAL-VALUE-1111")] // gitleaks:allow
    [InlineData("\"session\": \"NOT-A-REAL-VALUE-2222\"", "NOT-A-REAL-VALUE-2222")] // gitleaks:allow
    [InlineData("password=NOT-A-REAL-VALUE-3333", "NOT-A-REAL-VALUE-3333")] // gitleaks:allow
    [InlineData("Api-Key = NOT-A-REAL-VALUE-4444", "NOT-A-REAL-VALUE-4444")] // gitleaks:allow
    [InlineData("Authorization: Bearer NOT-A-REAL-VALUE-5555", "NOT-A-REAL-VALUE-5555")] // gitleaks:allow
    [InlineData("GET https://stonetavern.app/api/x?token=NOT-A-REAL-VALUE-6666&page=2", "NOT-A-REAL-VALUE-6666")] // gitleaks:allow
    [InlineData("connecting to https://user:NOT-A-REAL-VALUE-7777@db.invalid/x", "NOT-A-REAL-VALUE-7777")] // gitleaks:allow
    public void AnythingCredentialShaped_DoesNotSurvive(string line, string secret)
    {
        var redacted = ProblemReport.Redact(line);

        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.Contains("redacted", redacted, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The lines that actually diagnose things must survive, or the report is useless and
    /// the redaction has quietly defeated the feature.</summary>
    [Theory]
    [InlineData("2026-08-02 11:53:38 [INF] Manifest: v1.12.1-classic (2026-07-23)")]
    [InlineData("[WRN] WoW.exe not found in any search path")]
    [InlineData("[INF] Addon catalog: 584 entries from https://stonetavern.app/api/launcher/addons")]
    [InlineData("[INF] Launcher update available: 1.6.1 -> 1.6.2")]
    [InlineData("[INF] SHA256 verified: C:\\Users\\andre\\Stonetavern\\WowLauncher.exe.download")]
    public void OrdinaryLogLines_AreLeftAlone(string line) =>
        Assert.Equal(line, ProblemReport.Redact(line));

    /// <summary>What the player types goes through the same filter. Players paste logs into that box,
    /// and a credential does not become safe because a human moved it.</summary>
    [Fact]
    public void ThePlayersOwnText_IsRedactedToo()
    {
        var report = Subject().Build(
            message: "it fails, my log says token: NOT-A-REAL-VALUE-8888", // gitleaks:allow
            contact: "");

        Assert.DoesNotContain("NOT-A-REAL-VALUE-8888", report.Message, StringComparison.Ordinal);
    }

    // ── The log tail ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OnlyTheTailOfTheLogIsTaken_AndItIsTheRecentEnd()
    {
        File.WriteAllLines(Path.Combine(_dir, "launcher20260802.log"),
            Enumerable.Range(1, ProblemReport.LogLines + 500).Select(i => $"line {i}"));

        var log = Subject().RecentLog();

        var lines = log.Split('\n');
        Assert.Equal(ProblemReport.LogLines, lines.Length);
        Assert.Equal($"line {ProblemReport.LogLines + 500}", lines[^1]);   // the newest line is there
        Assert.DoesNotContain("line 1\n", log, StringComparison.Ordinal);  // the oldest is not
    }

    /// <summary>Serilog holds its file open while the launcher runs. Reading it must not need
    /// exclusive access, or the report would fail in exactly the situation it exists for.</summary>
    [Fact]
    public void TheLogIsReadable_WhileSomethingElseHoldsItOpen()
    {
        var path = Path.Combine(_dir, "launcher20260802.log");
        File.WriteAllText(path, "[INF] still running\n");

        using var held = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        Assert.Contains("still running", Subject().RecentLog(), StringComparison.Ordinal);
    }

    /// <summary>No log, no crash. A player whose launcher never got far enough to write one is
    /// exactly who needs to be able to report a problem.</summary>
    [Fact]
    public void AMissingLog_IsNotAnError() =>
        Assert.Equal("", Subject().RecentLog());

    // ── The payload ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ThePayloadCarriesWhatThePlayerCannotTellYou()
    {
        var report = Subject().Build("does not start", "me@example.invalid");

        Assert.Equal("1.6.3", report.LauncherVersion);
        Assert.Equal(0, report.ClientBuild);          // no client recorded in this config
        Assert.False(report.ClientInstalled);
        Assert.Equal("stonetavern", report.Realm);
        Assert.Contains("does not start", report.Message, StringComparison.Ordinal);
        Assert.Equal("me@example.invalid", report.Contact);
        Assert.False(string.IsNullOrWhiteSpace(report.Os));
    }

    /// <summary>One pasted wall of text must not push the request past the server's size limit and
    /// take the whole report down with it.</summary>
    [Fact]
    public void AnEnormousMessage_IsCapped()
    {
        var report = Subject().Build(new string('x', ProblemReport.MaxMessageChars * 3), "");

        Assert.Equal(ProblemReport.MaxMessageChars, report.Message.Length);
    }

    /// <summary>The player sees what they send before they send it. A bundle nobody can inspect is
    /// one they are right to refuse.</summary>
    [Fact]
    public void ThePreviewShowsTheFacts_AndTheLog()
    {
        File.WriteAllText(Path.Combine(_dir, "launcher20260802.log"), "[WRN] WoW.exe not found\n");
        var report = Subject().Build("nothing happens", "");

        var preview = ProblemReport.Preview(report);

        Assert.Contains("1.6.3", preview, StringComparison.Ordinal);
        Assert.Contains("not installed", preview, StringComparison.Ordinal);
        Assert.Contains("nothing happens", preview, StringComparison.Ordinal);
        Assert.Contains("WoW.exe not found", preview, StringComparison.Ordinal);
    }

    // ── Doubles ─────────────────────────────────────────────────────────────────────────────────

    private sealed class Paths(string dir) : IAppPaths
    {
        public string ConfigDir => dir;
        public string StateDir => dir;
        public string CacheDir => dir;
        public string LogDir => dir;
        public string ShareDir => dir;
        public string ConfigFilePath => Path.Combine(dir, "launcher_config.json");
        public string NewsCacheFilePath => Path.Combine(dir, "news-cache.json");
        public string ClientInstallDir(int gameBuild) => Path.Combine(dir, $"WoW-Client-{gameBuild}");
        public string ClientDownloadZip(int gameBuild) => Path.Combine(dir, $"WoW-Client-{gameBuild}.zip");
        public void EnsureDirectories() => Directory.CreateDirectory(dir);
    }

    private sealed class MemoryConfig : IConfigService
    {
        private LauncherConfig _c = new() { SelectedRealmId = "stonetavern" };
        public LauncherConfig Load() => _c;
        public void Save(LauncherConfig config) => _c = config;
        public bool LastSaveSucceeded => true;
    }
}
