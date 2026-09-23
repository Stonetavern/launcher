using System;
using System.Collections.Generic;
using System.Linq;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Proofs for release 1.8.11's "Stolperfallen-Preflight": the launcher runs without a signature on
/// player machines, so the typical install/download/self-update traps (elevation, a synced or system
/// folder, a path too long for the 32-bit legacy client, a folder that cannot be written to, the game
/// still running, a translocated or quarantined macOS bundle, a privileged AppImage location, a clock
/// far enough off to make a fresh manifest look expired) must be caught BEFORE any byte moves, with a
/// plain message instead of a failed download or a silent update loop.
///
/// One rule = one Fact for the positive case, one for the negative — every rule in the release ticket
/// table is represented, plus the mutual-exclusion between TRANSLOCATED and QUARANTINED and the
/// severity contract (Block for the ones that would otherwise corrupt a client or run a doomed swap,
/// Warn for the ones that are merely worth a heads-up).
/// </summary>
public sealed class InstallEnvironmentPreflightTests
{
    private static readonly IReadOnlyDictionary<string, string> NoEnv =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>A fully "clean" install: no rule should fire. Every test starts from this and changes
    /// exactly the one fact its rule reads, so a failure always points at a single cause.</summary>
    private static InstallEnvironmentFacts Clean(
        InstallHostOs os = InstallHostOs.Windows,
        string launcherBaseDir = @"C:\Users\Player\AppData\Local\Stonetavern",
        string targetInstallDir = @"C:\Users\Player\Games\Stonetavern-WoW",
        bool isElevated = false,
        IReadOnlyDictionary<string, string>? env = null,
        bool hasQuarantineAttr = false,
        bool writeProbeSucceeded = true,
        int longestRelativePathInManifest = 40,
        bool gameProcessRunning = false) =>
        new(os, launcherBaseDir, targetInstallDir, isElevated, env ?? NoEnv, hasQuarantineAttr,
            writeProbeSucceeded, longestRelativePathInManifest, gameProcessRunning);

    private static PreflightFinding? Find(IReadOnlyList<PreflightFinding> findings, string code) =>
        findings.FirstOrDefault(f => f.Code == code);

    // ─── ELEVATED ───────────────────────────────────────────────────────────

    [Fact]
    public void Elevated_OnWindows_IsWarn()
    {
        var findings = InstallEnvironmentPreflight.Evaluate(Clean(isElevated: true));

        var finding = Find(findings, "ELEVATED");
        Assert.NotNull(finding);
        Assert.Equal(PreflightSeverity.Warn, finding!.Severity);
    }

    [Fact]
    public void NotElevated_NoFinding()
    {
        var findings = InstallEnvironmentPreflight.Evaluate(Clean(isElevated: false));

        Assert.Null(Find(findings, "ELEVATED"));
    }

    [Fact]
    public void Elevated_OffWindows_NeverFires()
    {
        // IsElevated is a Windows-only concept for these rules; a mac/Linux probe can never even
        // observe it truthfully, so it must not be reported there regardless of the flag's value.
        var findings = InstallEnvironmentPreflight.Evaluate(
            Clean(os: InstallHostOs.Linux, isElevated: true));

        Assert.Null(Find(findings, "ELEVATED"));
    }

    // ─── PATH_SYSTEM ────────────────────────────────────────────────────────

    [Fact]
    public void InstallUnderProgramFiles_IsBlocked()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ProgramFiles"] = @"C:\Program Files",
        };
        var findings = InstallEnvironmentPreflight.Evaluate(Clean(
            targetInstallDir: @"C:\Program Files\Stonetavern-WoW", env: env));

        var finding = Find(findings, "PATH_SYSTEM");
        Assert.NotNull(finding);
        Assert.Equal(PreflightSeverity.Block, finding!.Severity);
    }

    [Fact]
    public void InstallUnderUserProfile_IsNotBlockedAsSystemPath()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ProgramFiles"] = @"C:\Program Files",
        };
        var findings = InstallEnvironmentPreflight.Evaluate(Clean(
            targetInstallDir: @"C:\Users\Player\Games\Stonetavern-WoW", env: env));

        Assert.Null(Find(findings, "PATH_SYSTEM"));
    }

    [Fact]
    public void InstallUnderUsrOnLinux_IsBlocked()
    {
        var findings = InstallEnvironmentPreflight.Evaluate(Clean(
            os: InstallHostOs.Linux, targetInstallDir: "/usr/local/stonetavern"));

        Assert.Equal(PreflightSeverity.Block, Find(findings, "PATH_SYSTEM")!.Severity);
    }

    // ─── PATH_SYNCED ────────────────────────────────────────────────────────

    [Fact]
    public void InstallUnderOneDriveFolderName_IsWarn()
    {
        var findings = InstallEnvironmentPreflight.Evaluate(Clean(
            targetInstallDir: @"C:\Users\Player\OneDrive\Games\Stonetavern-WoW"));

        var finding = Find(findings, "PATH_SYNCED");
        Assert.NotNull(finding);
        Assert.Equal(PreflightSeverity.Warn, finding!.Severity);
    }

    [Fact]
    public void InstallUnderOneDriveEnvRoot_IsWarn_EvenWithoutTheNameInThePath()
    {
        // Some OneDrive setups rename the folder; the env var is the authoritative root regardless of
        // what the folder is called on disk.
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["OneDrive"] = @"C:\Users\Player\MyCloudStuff",
        };
        var findings = InstallEnvironmentPreflight.Evaluate(Clean(
            targetInstallDir: @"C:\Users\Player\MyCloudStuff\Games\Stonetavern-WoW", env: env));

        Assert.Equal(PreflightSeverity.Warn, Find(findings, "PATH_SYNCED")!.Severity);
    }

    [Fact]
    public void InstallOutsideAnySyncedFolder_NoFinding()
    {
        var findings = InstallEnvironmentPreflight.Evaluate(Clean(
            targetInstallDir: @"C:\Users\Player\Games\Stonetavern-WoW"));

        Assert.Null(Find(findings, "PATH_SYNCED"));
    }

    // ─── PATH_PROTECTED ─────────────────────────────────────────────────────

    [Fact]
    public void InstallUnderDocuments_OnWindows_IsWarn()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["USERPROFILE"] = @"C:\Users\Player",
        };
        var findings = InstallEnvironmentPreflight.Evaluate(Clean(
            targetInstallDir: @"C:\Users\Player\Documents\Stonetavern-WoW", env: env));

        Assert.Equal(PreflightSeverity.Warn, Find(findings, "PATH_PROTECTED")!.Severity);
    }

    [Fact]
    public void InstallUnderGamesFolder_IsNotProtected()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["USERPROFILE"] = @"C:\Users\Player",
        };
        var findings = InstallEnvironmentPreflight.Evaluate(Clean(
            targetInstallDir: @"C:\Users\Player\Games\Stonetavern-WoW", env: env));

        Assert.Null(Find(findings, "PATH_PROTECTED"));
    }

    // ─── PATH_TOO_LONG ──────────────────────────────────────────────────────

    [Fact]
    public void InstallDirPlusManifestPath_OverTheLimit_IsBlocked()
    {
        var longDir = @"C:\" + new string('a', 190); // 193 chars
        var findings = InstallEnvironmentPreflight.Evaluate(Clean(
            targetInstallDir: longDir, longestRelativePathInManifest: 20)); // 193 + 20 + 1 > 200

        var finding = Find(findings, "PATH_TOO_LONG");
        Assert.NotNull(finding);
        Assert.Equal(PreflightSeverity.Block, finding!.Severity);
    }

    [Fact]
    public void InstallDirPlusManifestPath_UnderTheLimit_NoFinding()
    {
        var findings = InstallEnvironmentPreflight.Evaluate(Clean(
            targetInstallDir: @"C:\Games\Stonetavern-WoW", longestRelativePathInManifest: 20));

        Assert.Null(Find(findings, "PATH_TOO_LONG"));
    }

    // ─── PATH_NOT_WRITABLE ──────────────────────────────────────────────────

    [Fact]
    public void FailedWriteProbe_IsBlocked()
    {
        var findings = InstallEnvironmentPreflight.Evaluate(Clean(writeProbeSucceeded: false));

        var finding = Find(findings, "PATH_NOT_WRITABLE");
        Assert.NotNull(finding);
        Assert.Equal(PreflightSeverity.Block, finding!.Severity);
    }

    [Fact]
    public void SucceededWriteProbe_NoFinding()
    {
        var findings = InstallEnvironmentPreflight.Evaluate(Clean(writeProbeSucceeded: true));

        Assert.Null(Find(findings, "PATH_NOT_WRITABLE"));
    }

    // ─── GAME_RUNNING ───────────────────────────────────────────────────────

    [Fact]
    public void GameRunning_IsBlocked()
    {
        var findings = InstallEnvironmentPreflight.Evaluate(Clean(gameProcessRunning: true));

        var finding = Find(findings, "GAME_RUNNING");
        Assert.NotNull(finding);
        Assert.Equal(PreflightSeverity.Block, finding!.Severity);
    }

    [Fact]
    public void GameNotRunning_NoFinding()
    {
        var findings = InstallEnvironmentPreflight.Evaluate(Clean(gameProcessRunning: false));

        Assert.Null(Find(findings, "GAME_RUNNING"));
    }

    // ─── TRANSLOCATED ───────────────────────────────────────────────────────

    [Fact]
    public void TranslocatedMacBundle_IsBlocked()
    {
        var findings = InstallEnvironmentPreflight.Evaluate(Clean(
            os: InstallHostOs.MacOs,
            launcherBaseDir: "/private/var/folders/zz/AppTranslocation/ABCD/d/Stonetavern.app/Contents/MacOS"));

        var finding = Find(findings, "TRANSLOCATED");
        Assert.NotNull(finding);
        Assert.Equal(PreflightSeverity.Block, finding!.Severity);
    }

    [Fact]
    public void NonTranslocatedMacBundle_NoFinding()
    {
        var findings = InstallEnvironmentPreflight.Evaluate(Clean(
            os: InstallHostOs.MacOs,
            launcherBaseDir: "/Applications/Stonetavern.app/Contents/MacOS"));

        Assert.Null(Find(findings, "TRANSLOCATED"));
    }

    // ─── QUARANTINED ────────────────────────────────────────────────────────

    [Fact]
    public void QuarantinedMacBundle_NotTranslocated_IsWarn()
    {
        var findings = InstallEnvironmentPreflight.Evaluate(Clean(
            os: InstallHostOs.MacOs,
            launcherBaseDir: "/Applications/Stonetavern.app/Contents/MacOS",
            hasQuarantineAttr: true));

        var finding = Find(findings, "QUARANTINED");
        Assert.NotNull(finding);
        Assert.Equal(PreflightSeverity.Warn, finding!.Severity);
    }

    [Fact]
    public void NotQuarantined_NoFinding()
    {
        var findings = InstallEnvironmentPreflight.Evaluate(Clean(
            os: InstallHostOs.MacOs,
            launcherBaseDir: "/Applications/Stonetavern.app/Contents/MacOS",
            hasQuarantineAttr: false));

        Assert.Null(Find(findings, "QUARANTINED"));
    }

    [Fact]
    public void TranslocatedAndQuarantined_ReportsOnlyTranslocated()
    {
        // The same underlying situation (a bundle nobody moved into Applications yet) should not
        // repeat the same "move it into Applications" instruction twice under two different codes.
        var findings = InstallEnvironmentPreflight.Evaluate(Clean(
            os: InstallHostOs.MacOs,
            launcherBaseDir: "/private/var/folders/zz/AppTranslocation/ABCD/d/Stonetavern.app/Contents/MacOS",
            hasQuarantineAttr: true));

        Assert.NotNull(Find(findings, "TRANSLOCATED"));
        Assert.Null(Find(findings, "QUARANTINED"));
    }

    // ─── APPIMAGE_PRIVILEGED ────────────────────────────────────────────────

    [Fact]
    public void AppImageUnderUsr_OnLinux_IsWarn()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["APPIMAGE"] = "/usr/local/bin/Stonetavern.AppImage",
        };
        var findings = InstallEnvironmentPreflight.Evaluate(Clean(os: InstallHostOs.Linux, env: env));

        var finding = Find(findings, "APPIMAGE_PRIVILEGED");
        Assert.NotNull(finding);
        Assert.Equal(PreflightSeverity.Warn, finding!.Severity);
    }

    [Fact]
    public void AppImageUnderHome_OnLinux_NoFinding()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["APPIMAGE"] = "/home/player/.local/bin/Stonetavern.AppImage",
        };
        var findings = InstallEnvironmentPreflight.Evaluate(Clean(os: InstallHostOs.Linux, env: env));

        Assert.Null(Find(findings, "APPIMAGE_PRIVILEGED"));
    }

    // ─── CLOCK_SKEW (separate entry point — not part of Evaluate) ──────────

    [Fact]
    public void ExpiredManifestRejection_MoreThanAWeekPast_IsWarn()
    {
        var release = ReleaseVerdict.Refuse(
            "manifest expired at 2026-08-01T00:00:00.0000000+00:00 (now 2026-08-10T00:00:00.0000000+00:00)");
        var now = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);

        var finding = InstallEnvironmentPreflight.CheckClockSkew(release, "2026-08-01T00:00:00Z", now);

        Assert.NotNull(finding);
        Assert.Equal(PreflightSeverity.Warn, finding!.Severity);
        Assert.Equal("CLOCK_SKEW", finding.Code);
    }

    [Fact]
    public void ExpiredManifestRejection_WithinAWeek_NoFinding()
    {
        var release = ReleaseVerdict.Refuse(
            "manifest expired at 2026-08-08T00:00:00.0000000+00:00 (now 2026-08-10T00:00:00.0000000+00:00)");
        var now = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);

        Assert.Null(InstallEnvironmentPreflight.CheckClockSkew(release, "2026-08-08T00:00:00Z", now));
    }

    [Fact]
    public void AcceptedManifest_NeverProducesClockSkew()
    {
        var now = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);

        Assert.Null(InstallEnvironmentPreflight.CheckClockSkew(ReleaseVerdict.Admitted, "2026-01-01T00:00:00Z", now));
    }

    [Fact]
    public void RejectionForADifferentReason_NeverProducesClockSkew()
    {
        // Anti-rollback / channel refusals are not a clock symptom; CheckClockSkew must not fire on
        // the shape of the reason string for anything but the "expired" prefix ManifestReleasePolicy
        // formats its own refusal with.
        var release = ReleaseVerdict.Refuse("manifest serial 5 is older than the highest already accepted (9) — replay refused");
        var now = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);

        Assert.Null(InstallEnvironmentPreflight.CheckClockSkew(release, "2026-08-01T00:00:00Z", now));
    }

    // ─── Self-update swap: only TRANSLOCATED + PATH_NOT_WRITABLE apply ─────

    [Fact]
    public void SelfUpdateSwap_IgnoresPathSystem_UnlikeThePlainEvaluate()
    {
        // A launcher that already lives under Program Files ships and self-updates today; Evaluate()
        // would BLOCK it as PATH_SYSTEM (that rule is about where a client install lives), but the
        // self-update swap entry point must not newly break an existing, working install.
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ProgramFiles"] = @"C:\Program Files",
        };
        var facts = Clean(targetInstallDir: @"C:\Program Files\Stonetavern", env: env);

        var swapFindings = InstallEnvironmentPreflight.EvaluateSelfUpdateSwap(facts);
        var plainFindings = InstallEnvironmentPreflight.Evaluate(facts);

        Assert.Empty(swapFindings);
        Assert.NotEmpty(plainFindings); // sanity: PATH_SYSTEM really does fire for the general rule set
    }

    [Fact]
    public void SelfUpdateSwap_StillBlocksTranslocated()
    {
        var facts = Clean(
            os: InstallHostOs.MacOs,
            launcherBaseDir: "/private/var/folders/zz/AppTranslocation/ABCD/d/Stonetavern.app/Contents/MacOS");

        var swapFindings = InstallEnvironmentPreflight.EvaluateSelfUpdateSwap(facts);

        Assert.Equal(PreflightSeverity.Block, Find(swapFindings, "TRANSLOCATED")!.Severity);
    }

    [Fact]
    public void SelfUpdateSwap_StillBlocksNotWritable()
    {
        var facts = Clean(writeProbeSucceeded: false);

        var swapFindings = InstallEnvironmentPreflight.EvaluateSelfUpdateSwap(facts);

        Assert.Equal(PreflightSeverity.Block, Find(swapFindings, "PATH_NOT_WRITABLE")!.Severity);
    }

    // ─── Catalog: every finding resolves to a non-empty, non-key sentence ──

    [Fact]
    public void EveryRuleCode_ResolvesToADisplayedSentence_NotItsCatalogKey()
    {
        // Placebo guard for the localisation wiring itself: DisplayText must resolve through Loc, not
        // fall through to echoing the raw MessageKey (which would still compile, still return a
        // string, and still pass a weaker "is not empty" check).
        var codes = new[]
        {
            "ELEVATED", "PATH_SYSTEM", "PATH_SYNCED", "PATH_PROTECTED", "PATH_TOO_LONG",
            "PATH_NOT_WRITABLE", "GAME_RUNNING", "TRANSLOCATED", "QUARANTINED",
            "APPIMAGE_PRIVILEGED", "CLOCK_SKEW",
        };

        foreach (var code in codes)
        {
            var finding = new PreflightFinding(PreflightSeverity.Warn, code, $"Preflight_{code}");
            var text = InstallEnvironmentPreflight.DisplayText(finding);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.NotEqual(finding.MessageKey, text);
        }
    }
}
