namespace WowLauncher.Tests;

using System;
using System.IO;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using Xunit;

/// <summary>
/// The brake against the SECOND endless update loop found on 2026-08-01 (external review): the swap
/// runs in a detached helper after this process exits, so a swap that never lands is invisible from
/// here — the old binary comes back, sees the same newer manifest, and downloads it again forever.
/// </summary>
public class UpdateAttemptLedgerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "stonetavern-ledger-" + Guid.NewGuid().ToString("N"));

    private static readonly Serilog.ILogger Log = Serilog.Core.Logger.None;

    private UpdateAttemptLedger NewLedger()
    {
        Directory.CreateDirectory(_dir);
        return new UpdateAttemptLedger(new FixedPaths(_dir), Log);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
        GC.SuppressFinalize(this);
    }

    private static readonly Version Running = new(1, 5, 1);
    private static readonly Version Target = new(1, 6, 1);

    [Fact]
    public void FirstAttempt_IsAllowed()
    {
        var ledger = NewLedger();
        Assert.False(ledger.IsExhausted(Target, Running));
    }

    /// <summary>THE loop test. Three swaps that never arrive, then auto-apply must stand down —
    /// otherwise the player watches the launcher restart itself indefinitely.</summary>
    [Fact]
    public void AfterMaxAttempts_TheSameVersionIsNoLongerAutoApplied()
    {
        var ledger = NewLedger();
        for (var i = 0; i < UpdateAttemptLedger.MaxAttempts; i++)
        {
            Assert.False(ledger.IsExhausted(Target, Running), $"attempt {i + 1} must still be allowed");
            ledger.RecordAttempt(Target);
        }
        Assert.True(ledger.IsExhausted(Target, Running));
    }

    /// <summary>A broken release must not disable updating forever: a NEWER target gets fresh attempts.</summary>
    [Fact]
    public void ANewerTarget_GetsAFreshBudget()
    {
        var ledger = NewLedger();
        for (var i = 0; i < UpdateAttemptLedger.MaxAttempts; i++) ledger.RecordAttempt(Target);
        Assert.True(ledger.IsExhausted(Target, Running));

        Assert.False(ledger.IsExhausted(new Version(1, 7, 0), Running));
    }

    /// <summary>The success case: once the launcher actually runs the version it was trying to reach,
    /// the note is cleared, so an old count is never charged against a later update.</summary>
    [Fact]
    public void ReachingTheTarget_ClearsTheLedger()
    {
        var ledger = NewLedger();
        for (var i = 0; i < UpdateAttemptLedger.MaxAttempts; i++) ledger.RecordAttempt(Target);

        // Now running the version we were trying to install — the swap worked after all.
        Assert.False(ledger.IsExhausted(Target, running: Target));
        // And the exhausted state is gone, not merely bypassed.
        Assert.False(ledger.IsExhausted(Target, Running));
    }

    [Fact]
    public void ACorruptNote_NeverBlocksUpdating()
    {
        var ledger = NewLedger();
        File.WriteAllText(Path.Combine(_dir, "update-attempts.txt"), "not-a-version\r\nnot-a-number\r\n");
        Assert.False(ledger.IsExhausted(Target, Running));
    }

    /// <summary>A count that PARSES but could never have been written by this class must be thrown away,
    /// not trusted. "-2147483648" is a valid int; counting up from there takes two billion launches to
    /// reach the ceiling, which is the endless loop restored through the very mechanism meant to stop
    /// it. Found by the pre-release review of 1.6.1, not by the first round of tests.</summary>
    [Theory]
    [InlineData("-2147483648")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("2147483647")]
    public void AnImplausibleCount_IsTreatedAsCorrupt_AndTheCeilingStaysReachable(string count)
    {
        var ledger = NewLedger();
        File.WriteAllText(Path.Combine(_dir, "update-attempts.txt"), $"{Target}\r\n{count}\r\n");

        // Discarded, so the budget starts over — and, crucially, the ceiling is reached in
        // MaxAttempts launches rather than never.
        for (var i = 0; i < UpdateAttemptLedger.MaxAttempts; i++)
        {
            Assert.False(ledger.IsExhausted(Target, Running), $"attempt {i + 1} must still be allowed");
            ledger.RecordAttempt(Target);
        }
        Assert.True(ledger.IsExhausted(Target, Running));
    }

    /// <summary>The written count must always be one this class would accept back. If it ever wrote past
    /// the ceiling, the next start would reject it as corrupt and hand out a fresh budget — the brake
    /// silently switching itself off.</summary>
    [Fact]
    public void TheWrittenCount_NeverExceedsWhatItWillAcceptBack()
    {
        var ledger = NewLedger();
        for (var i = 0; i < UpdateAttemptLedger.MaxAttempts + 5; i++) ledger.RecordAttempt(Target);

        var lines = File.ReadAllLines(Path.Combine(_dir, "update-attempts.txt"));
        Assert.Equal(UpdateAttemptLedger.MaxAttempts, int.Parse(lines[1]));
        Assert.True(ledger.IsExhausted(Target, Running));
    }

    /// <summary>Only the launcher's state directory is needed here — the rest of IAppPaths is irrelevant
    /// to the ledger and is pointed at the same temp folder so nothing escapes it.</summary>
    private sealed class FixedPaths(string dir) : IAppPaths
    {
        public string ConfigDir => dir;
        public string StateDir => dir;
        public string CacheDir => dir;
        public string LogDir => dir;
        public string ShareDir => dir;
        public string ConfigFilePath => Path.Combine(dir, "launcher_config.json");
        public string NewsCacheFilePath => Path.Combine(dir, "news-cache.json");
        public string ClientInstallDir(int build) => Path.Combine(dir, $"client-{build}");
        public string ClientDownloadZip(int build) => Path.Combine(dir, $"client-{build}.zip");
        public void EnsureDirectories() => Directory.CreateDirectory(dir);
    }
}
