using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The one Stonetavern folder (owner 2026-09-28) and its first-start setup. What must hold, because
/// live players update into this: an existing player is never taken for a new one and never moved
/// (<see cref="LibraryClassifier"/>), the setup changes nothing outside a staging folder until the copy
/// in the new folder has started and finished it, and every way it can fail leaves the old launcher
/// running (<see cref="LibrarySetup"/>). All folders are throwaway temp folders.
/// </summary>
public sealed class StonetavernLibraryTests : IDisposable
{
    // Short on purpose: the 1.12.1 path length rule (200) applies to these folders like to a player's.
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "stl-" + Guid.NewGuid().ToString("N")[..8]);

    public StonetavernLibraryTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); }
        catch (Exception) { /* temp */ }
    }

    // ── the decision ─────────────────────────────────────────────────────────────────────────────

    private static LibraryFacts Facts(bool config = false, string? libraryRoot = null, string? legacyFolder = null,
                                      string? self = null, string? forward = null, bool ignoreCase = false,
                                      string? handoffRoot = null, string? nonce = null) =>
        new(handoffRoot, nonce, config, libraryRoot, legacyFolder, self, forward, ignoreCase);

    [Fact]
    public void NoSignOfAnEarlierInstall_IsANewPlayer()
    {
        Assert.Equal(LibraryDecisionKind.Setup, LibraryClassifier.Decide(Facts()).Kind);
    }

    /// <summary>The live Windows player: a config next to an exe in Downloads. Even a library pointer
    /// from a later download must not take that player away from their install.</summary>
    [Fact]
    public void AnyConfig_IsAnExistingPlayer_EvenWithALibraryPointerOnTheMachine()
    {
        var d = LibraryClassifier.Decide(Facts(config: true, self: "/dl/old/WowLauncher.exe", forward: "/lib/Launcher/WowLauncher.exe"));
        Assert.Equal(LibraryDecisionKind.Legacy, d.Kind);
    }

    [Fact]
    public void AClientFolderTheLauncherMade_IsAnExistingPlayer_EvenWithoutAConfig()
    {
        var d = LibraryClassifier.Decide(Facts(legacyFolder: "/home/p/Desktop/WoW-Client-5875"));
        Assert.Equal(LibraryDecisionKind.Legacy, d.Kind);
    }

    [Fact]
    public void TheLauncherInTheFolder_RunsAsTheLibrary()
    {
        var target = "/g/Stonetavern/Launcher/stonetavern-launcher.AppImage";
        var d = LibraryClassifier.Decide(Facts(config: true, libraryRoot: "/g/Stonetavern", self: target, forward: target));
        Assert.Equal(LibraryDecisionKind.Library, d.Kind);
        Assert.Equal("/g/Stonetavern", d.Root);
    }

    /// <summary>Linux: the XDG config is shared by every AppImage copy, so an old download reads the
    /// library config too and must hand over, not run a second launcher beside it.</summary>
    [Fact]
    public void AnOldCopy_OfAPlayerWithAFolder_ForwardsToTheFolder()
    {
        var target = "/g/Stonetavern/Launcher/stonetavern-launcher.AppImage";
        var d = LibraryClassifier.Decide(Facts(config: true, libraryRoot: "/g/Stonetavern", self: "/home/p/Downloads/x.AppImage", forward: target));
        Assert.Equal(LibraryDecisionKind.Forward, d.Kind);
        Assert.Equal(target, d.Target);
    }

    [Fact]
    public void WindowsOldDownload_WithoutAConfig_ForwardsThroughThePointer()
    {
        var d = LibraryClassifier.Decide(Facts(self: @"C:\Users\p\Downloads\S\WowLauncher.exe",
                                               forward: @"C:\Users\p\Games\Stonetavern\Launcher\WowLauncher.exe"));
        Assert.Equal(LibraryDecisionKind.Forward, d.Kind);
    }

    /// <summary>Never forward to oneself: that would start the same launcher over and over.</summary>
    [Fact]
    public void ForwardingToItself_NeverHappens()
    {
        var d = LibraryClassifier.Decide(Facts(config: true, libraryRoot: "/G/S", self: "/G/S/Launcher/A.AppImage",
                                               forward: "/G/S/Launcher/A.AppImage"));
        Assert.Equal(LibraryDecisionKind.Library, d.Kind);
        var win = LibraryClassifier.Decide(Facts(self: @"C:\G\S\LAUNCHER\WowLauncher.exe",
                                                 forward: @"C:\G\S\Launcher\WowLauncher.exe", ignoreCase: true));
        Assert.NotEqual(LibraryDecisionKind.Forward, win.Kind);
    }

    [Fact]
    public void ADevRun_WithALibraryConfig_IsTheLibrary_NotAForward()
    {
        var d = LibraryClassifier.Decide(Facts(config: true, libraryRoot: "/g/S", self: null, forward: "/g/S/Launcher/a.AppImage"));
        Assert.Equal(LibraryDecisionKind.Library, d.Kind);
    }

    [Fact]
    public void StartedBySetup_IsTheHandoff()
    {
        var d = LibraryClassifier.Decide(Facts(config: true, handoffRoot: "/g/S", nonce: "abc"));
        Assert.Equal(LibraryDecisionKind.Handoff, d.Kind);
        Assert.Equal("abc", d.Nonce);
    }

    // ── the probe (read-only) ────────────────────────────────────────────────────────────────────

    private sealed class Paths(string root) : IAppPaths
    {
        public string ConfigDir => Path.Combine(root, "config");
        public string StateDir => Path.Combine(root, "state");
        public string CacheDir => Path.Combine(root, "cache");
        public string LogDir => StateDir;
        public string ShareDir => Path.Combine(root, "share");
        public string ConfigFilePath => Path.Combine(ConfigDir, "launcher_config.json");
        public string NewsCacheFilePath => Path.Combine(CacheDir, "news.json");
        public string ClientInstallDir(int b) => Path.Combine(ShareDir, $"WoW-Client-{b}");
        public string ClientDownloadZip(int b) => Path.Combine(CacheDir, $"WoW-Client-{b}.zip");
        public void EnsureDirectories() { }
    }

    [Fact]
    public void TheProbe_CountsEveryConfigShape_AndWritesNothing()
    {
        var paths = new Paths(_tmp);
        Assert.Equal((false, (string?)null), LibraryProbe.ReadConfig(paths));
        Assert.False(Directory.Exists(paths.ConfigDir));   // read-only: no folder, no default config

        Directory.CreateDirectory(paths.ConfigDir);
        File.WriteAllText(paths.ConfigFilePath + ".good", "{}");
        Assert.True(LibraryProbe.ReadConfig(paths).Evidence);          // only the backup copy survived

        File.WriteAllText(paths.ConfigFilePath, "{ not json");
        Assert.Equal((true, (string?)null), LibraryProbe.ReadConfig(paths));  // damaged is still a player

        File.WriteAllText(paths.ConfigFilePath, "{\"LibraryRoot\": \"/g/Stonetavern\"}");
        Assert.Equal((true, "/g/Stonetavern"), LibraryProbe.ReadConfig(paths));
    }

    [Fact]
    public void TheProbe_FindsAClientFolderTheLauncherMade_AndIgnoresLookalikes()
    {
        var desk = Directory.CreateDirectory(Path.Combine(_tmp, "Desktop")).FullName;
        Directory.CreateDirectory(Path.Combine(desk, "WoW-Client-old"));
        Assert.Null(LibraryProbe.FindLegacyClientFolder([desk, Path.Combine(_tmp, "missing")]));

        var real = Directory.CreateDirectory(Path.Combine(desk, "WoW-Client-42597")).FullName;
        Assert.Equal(real, LibraryProbe.FindLegacyClientFolder([Path.Combine(_tmp, "missing"), desk]));
    }

    [Fact]
    public void TheHandoffArguments_AreReadInEitherOrder()
    {
        Assert.Equal(("/g/S", "n1"), LibraryProbe.HandoffFrom(["--library-root", "/g/S", "--library-handoff", "n1"]));
        Assert.Equal(((string?)null, (string?)null), LibraryProbe.HandoffFrom(["--library-handoff"]));
    }

    // ── where clients go ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ClientTarget_KeepsEveryExistingInstall_AndOnlyNewBuildsGoIntoTheFolder()
    {
        var paths = new Paths(_tmp);
        var legacy = new LauncherConfig { PreferredInstallRoot = "/home/p/Desktop/" };
        legacy.ClientInstalls[5875] = "/home/p/Desktop/WoW-Client-5875/Stonetavern-Enhanced-1.12.1";

        Assert.Equal("/home/p/Desktop/WoW-Client-5875/Stonetavern-Enhanced-1.12.1",
                     ClientInstallTarget.For(legacy, 5875, paths, freshInstall: false));
        Assert.Equal(Path.Combine("/home/p/Desktop/", "WoW-Client-42597"),
                     ClientInstallTarget.For(legacy, 42597, paths, freshInstall: true));   // byte for byte as before

        var library = new LauncherConfig { LibraryRoot = "/g/Stonetavern", PreferredInstallRoot = "/old" };
        Assert.Equal(Path.Combine("/g/Stonetavern", "Classic-1.12.1"), ClientInstallTarget.For(library, 5875, paths, true));
        Assert.Equal(Path.Combine("/g/Stonetavern", "Modern-1.14.2"), ClientInstallTarget.For(library, 42597, paths, true));

        Assert.False(ClientInstallTarget.NeedsFolderPicker(library, freshInstall: true));
        Assert.False(ClientInstallTarget.NeedsFolderPicker(legacy, freshInstall: true));
        Assert.True(ClientInstallTarget.NeedsFolderPicker(new LauncherConfig(), freshInstall: true));
    }

    // ── noexec (Linux mount table) ───────────────────────────────────────────────────────────────

    private static readonly string[] MountTable =
    [
        "22 1 0:21 / / rw,relatime shared:1 - btrfs /dev/nvme0n1p3 rw",
        "40 22 0:35 / /home rw,relatime shared:2 - btrfs /dev/nvme0n1p3 rw",
        "41 40 8:17 / /home/p/USB\\040Stick rw,nosuid,nodev,noexec,relatime shared:9 - exfat /dev/sdb1 rw",
        "42 22 0:36 / /homer rw,noexec - tmpfs tmpfs rw",
        "43 22 0:37 / /mnt/games rw,noexec - ext4 /dev/sdc1 rw",
        "44 22 0:37 / /mnt/games rw,relatime - ext4 /dev/sdc1 rw",
    ];

    [Theory]
    [InlineData("/home/p/Games/Stonetavern", false)]
    [InlineData("/home/p/USB Stick/Stonetavern", true)]    // escaped space in the mount point
    [InlineData("/homer/x", true)]
    [InlineData("/home/pUSB Stick", false)]                 // a path boundary, not a string prefix
    [InlineData("/homer2/x", false)]                        // "/homer" is noexec, "/homer2" is not under it
    [InlineData("/mnt/games/Stonetavern", false)]           // the later mount at the same point wins
    public void NoExec_IsReadFromTheMostSpecificMount(string path, bool noexec)
    {
        var mounts = LinuxMountInfo.Parse(MountTable);
        Assert.Equal(noexec, LinuxMountInfo.MountFor(mounts, path)?.Options.Contains("noexec") == true);
    }

    // ── the setup transaction ────────────────────────────────────────────────────────────────────

    private sealed class Rig
    {
        public required string Root;
        public required string AppImage;
        public Func<string, IReadOnlyList<string>, string, Func<bool>?> Start = (_, _, _) => () => false;
        public bool NoExec;
        public long? Free = 200L * 1024 * 1024 * 1024;
        public InstallHostOs Os = InstallHostOs.Linux;
        public string BaseDir = "";
        public string? ProcessPath;
        public TimeSpan Timeout = TimeSpan.FromSeconds(5);
        public List<(string Root, string? Dir)> InPlaceCommits { get; } = [];

        public LibrarySetup Build() => new(
            new LibrarySetupHost(Os, BaseDir, Os == InstallHostOs.Linux ? AppImage : null, ProcessPath,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), false,
                _ => NoExec, _ => Free, (f, a, w) => Start(f, a, w), Timeout),
            Serilog.Log.Logger,
            (root, dir) => InPlaceCommits.Add((root, dir)));
    }

    private Rig NewRig()
    {
        var dl = Directory.CreateDirectory(Path.Combine(_tmp, "Downloads")).FullName;
        var appImage = Path.Combine(dl, "stonetavern-launcher-1.9.4-x86_64.AppImage");
        File.WriteAllBytes(appImage, RandomNumberGenerator.GetBytes(64 * 1024));
        return new Rig { Root = Path.Combine(_tmp, "Games", "Stonetavern"), AppImage = appImage };
    }

    /// <summary>What the copy does when it starts: finish the handoff with a commit that writes the
    /// marker, exactly like the real <see cref="LibrarySetup.Commit"/> does last.</summary>
    private static Func<string, IReadOnlyList<string>, string, Func<bool>?> CopyThatFinishes(List<string> started) =>
        (file, args, _) =>
        {
            started.Add(file);
            var nonce = args[args.ToList().IndexOf(LibraryProbe.HandoffArg) + 1];
            var root = args[args.ToList().IndexOf(LibraryProbe.RootArg) + 1];
            Assert.True(LibrarySetup.CompleteHandoff(root, nonce,
                (r, dir) => new LibraryMarker(1, r, dir, DateTimeOffset.UtcNow).Write(), Serilog.Log.Logger));
            return () => false;
        };

    [Fact]
    public void Check_RefusesWhatCannotWork_AndWarnsWhereItMightNot()
    {
        var rig = NewRig();
        Assert.False(rig.Build().Check(rig.Root).IsBlocked);
        Assert.True(rig.Build().Check("relative/path").IsBlocked);

        rig.NoExec = true;
        Assert.Contains(rig.Build().Check(rig.Root).Findings, f => f.Code == "SETUP_NOEXEC" && f.Severity == PreflightSeverity.Block);
        rig.NoExec = false;

        rig.Free = 100L * 1024 * 1024;
        Assert.Contains(rig.Build().Check(rig.Root).Findings, f => f.Code == "SETUP_NO_SPACE");
        rig.Free = 10L * 1024 * 1024 * 1024;
        var low = rig.Build().Check(rig.Root);
        Assert.Contains(low.Findings, f => f.Code == "SETUP_LOW_SPACE" && f.Severity == PreflightSeverity.Warn);
        Assert.False(low.IsBlocked);
        rig.Free = null;                                   // unknown space is not a full disk
        Assert.False(rig.Build().Check(rig.Root).IsBlocked);

        Directory.CreateDirectory(Path.Combine(rig.Root, "Launcher"));
        File.WriteAllText(Path.Combine(rig.Root, "Launcher", "theirs.txt"), "x");
        Assert.Contains(rig.Build().Check(rig.Root).Findings, f => f.Code == "SETUP_LAUNCHER_TAKEN");
    }

    /// <summary>The suggested folder must pass the path length rule of the 1.12.1 client for ordinary
    /// user names. Windows <c>C:\Users\&lt;20 letters&gt;\Games\Stonetavern</c> is 47 characters; this root
    /// is longer, through the real check. The first version counted the package top folder twice and
    /// refused a Windows name of ten letters.</summary>
    [Fact]
    public void TheSuggestedFolder_IsNotTooLong_ForOrdinaryUserNames()
    {
        var rig = NewRig();
        rig.Root = Path.Combine(_tmp, "abcdefghijklmnopqrst", "Games", "Stonetavern");
        Assert.True(rig.Root.Length >= 47, rig.Root);

        var check = rig.Build().Check(rig.Root);

        Assert.DoesNotContain(check.Findings, f => f.Code == "PATH_TOO_LONG");
        Assert.False(check.IsBlocked);
    }

    [Fact]
    public void Check_NeverCreatesTheChosenFolder()
    {
        var rig = NewRig();
        rig.Build().Check(rig.Root);
        Assert.False(Directory.Exists(rig.Root));
    }

    [Fact]
    public async Task Run_CopiesProvesStartsAndHandsOver()
    {
        var rig = NewRig();
        var started = new List<string>();
        rig.Start = CopyThatFinishes(started);

        var result = await rig.Build().RunAsync(rig.Root, null);

        Assert.Equal(LibrarySetupOutcome.HandedOver, result.Outcome);
        var copy = Path.Combine(rig.Root, "Launcher", LibraryNames.AppImageName);
        Assert.Equal([copy], started);
        Assert.Equal(File.ReadAllBytes(rig.AppImage), File.ReadAllBytes(copy));
        Assert.True((File.GetUnixFileMode(copy) & UnixFileMode.UserExecute) != 0);
        Assert.False(File.Exists(Path.Combine(rig.Root, "Launcher", LibraryNames.OwnedMarker)));
        Assert.NotNull(LibraryMarker.TryRead(rig.Root));
        Assert.Empty(Directory.GetDirectories(rig.Root, "Launcher.staging-*"));
        Assert.True(File.Exists(rig.AppImage));   // the download is copied, never moved
    }

    [Fact]
    public async Task Run_ACopyThatDoesNotStart_ChangesNothing()
    {
        var rig = NewRig();
        rig.Start = (_, _, _) => null;

        var result = await rig.Build().RunAsync(rig.Root, null);

        Assert.Equal(LibrarySetupOutcome.Failed, result.Outcome);
        Assert.False(Directory.Exists(Path.Combine(rig.Root, "Launcher")));
        Assert.Null(LibraryMarker.TryRead(rig.Root));
    }

    [Fact]
    public async Task Run_ACopyThatDiesBeforeItsFirstFrame_IsRolledBack()
    {
        var rig = NewRig();
        rig.Start = (_, _, _) => () => true;   // started, exited, never said done

        var result = await rig.Build().RunAsync(rig.Root, null);

        Assert.Equal(LibrarySetupOutcome.Failed, result.Outcome);
        Assert.False(Directory.Exists(Path.Combine(rig.Root, "Launcher")));
        Assert.Null(LibraryMarker.TryRead(rig.Root));
    }

    /// <summary>SmartScreen held the copy longer than the original waited: the original gives up, and
    /// a copy that starts afterwards finds nothing to claim instead of half a setup.</summary>
    [Fact]
    public async Task Run_ACopyThatNeverClaims_IsAbandoned_AndCannotClaimLater()
    {
        var rig = NewRig();
        string? nonce = null;
        rig.Timeout = TimeSpan.FromMilliseconds(300);
        rig.Start = (_, args, _) => { nonce = args[1]; return () => false; };

        var result = await rig.Build().RunAsync(rig.Root, null);

        Assert.Equal(LibrarySetupOutcome.Failed, result.Outcome);
        var committed = false;
        Assert.False(LibrarySetup.CompleteHandoff(rig.Root, nonce!, (_, _) => committed = true, Serilog.Log.Logger));
        Assert.False(committed);
        Assert.Null(LibraryMarker.TryRead(rig.Root));
    }

    /// <summary>Two double clicks, two setup pages, one folder: while the first setup runs, the
    /// second is turned away by the first one's lock and touches nothing.</summary>
    [Fact]
    public async Task Run_TwoSetupsAtOnce_TheSecondIsTurnedAway()
    {
        var rig = NewRig();
        LibrarySetupResult? second = null;
        var secondRig = NewRig();
        secondRig.Root = rig.Root;
        secondRig.Start = (_, _, _) => throw new InvalidOperationException("the second setup must not start anything");
        rig.Start = (_, _, _) =>
        {
            // The first setup holds its lock right now: this is where the second double click lands.
            second = secondRig.Build().RunAsync(rig.Root, null).GetAwaiter().GetResult();
            return () => true;
        };

        await rig.Build().RunAsync(rig.Root, null);

        Assert.NotNull(second);
        Assert.Equal(LibrarySetupOutcome.Failed, second!.Outcome);
        Assert.Equal(WowLauncher.Localization.Loc.T("Setup_Busy"), second.Message);
    }

    [Fact]
    public async Task Run_LeftoversOfAnEarlierAttempt_AreReplaced_ButAFolderThatIsNotOursIsNot()
    {
        var rig = NewRig();
        var ours = Directory.CreateDirectory(Path.Combine(rig.Root, "Launcher")).FullName;
        File.WriteAllText(Path.Combine(ours, LibraryNames.OwnedMarker), "old");
        File.WriteAllText(Path.Combine(ours, "half.bin"), "x");
        rig.Start = CopyThatFinishes([]);

        Assert.Equal(LibrarySetupOutcome.HandedOver, (await rig.Build().RunAsync(rig.Root, null)).Outcome);
        Assert.False(File.Exists(Path.Combine(ours, "half.bin")));

        var other = NewRigIn("other");
        var theirs = Directory.CreateDirectory(Path.Combine(other.Root, "Launcher")).FullName;
        File.WriteAllText(Path.Combine(theirs, "mine.txt"), "keep");
        Assert.Equal(LibrarySetupOutcome.Failed, (await other.Build().RunAsync(other.Root, null)).Outcome);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(theirs, "mine.txt")));
    }

    /// <summary>The copy claimed the handoff and died before its commit (milliseconds on the real
    /// AppImage, too short to hit from outside): rolled back, nothing counts as installed, and the
    /// claimed handoff cannot be finished later.</summary>
    [Fact]
    public async Task Run_ACopyThatDiesAfterClaimingButBeforeItsCommit_IsRolledBack()
    {
        var rig = NewRig();
        string? nonce = null;
        rig.Start = (_, args, _) =>
        {
            nonce = args[args.ToList().IndexOf(LibraryProbe.HandoffArg) + 1];
            var setupDir = Path.Combine(rig.Root, LibraryNames.SetupDir);
            File.Move(Path.Combine(setupDir, $"pending-{nonce}.json"), Path.Combine(setupDir, $"claimed-{nonce}.json"));
            return () => true;
        };

        var result = await rig.Build().RunAsync(rig.Root, null);

        Assert.Equal(LibrarySetupOutcome.Failed, result.Outcome);
        Assert.False(Directory.Exists(Path.Combine(rig.Root, "Launcher")));
        Assert.Null(LibraryMarker.TryRead(rig.Root));
        Assert.Null(LibraryProbe.LauncherIn(rig.Root));
        Assert.Empty(Directory.GetFiles(Path.Combine(rig.Root, LibraryNames.SetupDir), "claimed-*"));
    }

    /// <summary>The copy committed (config, marker) and died before writing "done". Rolling back then
    /// deleted the launcher the marker points to, and every later start pointed at nothing.</summary>
    [Fact]
    public async Task Run_ACopyThatDiesRightAfterItsCommit_IsKept_AndStartedAgain()
    {
        var rig = NewRig();
        var started = new List<(string File, int Args)>();
        rig.Start = (file, args, _) =>
        {
            started.Add((file, args.Count));
            if (args.Count == 0) return () => false;   // the restart
            var nonce = args[args.ToList().IndexOf(LibraryProbe.HandoffArg) + 1];
            var setupDir = Path.Combine(rig.Root, LibraryNames.SetupDir);
            File.Move(Path.Combine(setupDir, $"pending-{nonce}.json"), Path.Combine(setupDir, $"claimed-{nonce}.json"));
            new LibraryMarker(1, rig.Root, Path.GetDirectoryName(file), DateTimeOffset.UtcNow).Write();
            return () => true;                          // gone before the owned marker and "done"
        };

        var result = await rig.Build().RunAsync(rig.Root, null);

        var copy = Path.Combine(rig.Root, "Launcher", LibraryNames.AppImageName);
        Assert.Equal(LibrarySetupOutcome.AlreadySetUp, result.Outcome);
        Assert.True(File.Exists(copy));
        Assert.False(File.Exists(Path.Combine(rig.Root, "Launcher", LibraryNames.OwnedMarker)));
        Assert.Equal(copy, LibraryProbe.LauncherIn(rig.Root));
        Assert.Equal([(copy, 4), (copy, 0)], started);
    }

    /// <summary>A given-up attempt leaves its handoff file as "abandoned-…" for the log; once a later
    /// attempt succeeded, none of them is left in the folder.</summary>
    [Fact]
    public async Task Run_AfterASuccess_NoGivenUpAttemptIsLeft()
    {
        var rig = NewRig();
        rig.Start = (_, _, _) => () => true;   // first attempt: the copy dies
        Assert.Equal(LibrarySetupOutcome.Failed, (await rig.Build().RunAsync(rig.Root, null)).Outcome);
        var setupDir = Path.Combine(rig.Root, LibraryNames.SetupDir);
        Assert.NotEmpty(Directory.GetFiles(setupDir, "abandoned-*"));

        rig.Start = CopyThatFinishes([]);
        Assert.Equal(LibrarySetupOutcome.HandedOver, (await rig.Build().RunAsync(rig.Root, null)).Outcome);

        Assert.Empty(Directory.GetFiles(setupDir, "abandoned-*"));
    }

    /// <summary>Original and copy both died after the handoff was written: the next setup gives that
    /// handoff up, so a late copy of the dead attempt cannot claim it and rewrite the folder.</summary>
    [Fact]
    public async Task Run_AHandoffOfADeadSetup_CanNoLongerBeClaimed()
    {
        var rig = NewRig();
        var setupDir = Directory.CreateDirectory(Path.Combine(rig.Root, LibraryNames.SetupDir)).FullName;
        const string dead = "0123456789abcdef0123456789abcdef";
        File.WriteAllText(Path.Combine(setupDir, $"pending-{dead}.json"), "{}");
        rig.Start = CopyThatFinishes([]);

        Assert.Equal(LibrarySetupOutcome.HandedOver, (await rig.Build().RunAsync(rig.Root, null)).Outcome);

        var committed = false;
        Assert.False(LibrarySetup.CompleteHandoff(rig.Root, dead, (_, _) => committed = true, Serilog.Log.Logger));
        Assert.False(committed);
        Assert.Empty(Directory.GetFiles(setupDir, "pending-*"));
        Assert.Empty(Directory.GetFiles(setupDir, "abandoned-*"));
    }

    /// <summary>A setup cut off while copying left its staging folder behind, a whole launcher each time.
    /// The next setup removes exactly those, nothing that only looks similar.</summary>
    [Fact]
    public async Task Run_StagingLeftByAnInterruptedCopy_IsRemoved_LookalikesAreNot()
    {
        var rig = NewRig();
        var left = Directory.CreateDirectory(Path.Combine(rig.Root, "Launcher.staging-" + Guid.NewGuid().ToString("N"))).FullName;
        File.WriteAllText(Path.Combine(left, "half.AppImage"), "x");
        var lookalike = Directory.CreateDirectory(Path.Combine(rig.Root, "Launcher.staging-mine")).FullName;
        rig.Start = CopyThatFinishes([]);

        Assert.Equal(LibrarySetupOutcome.HandedOver, (await rig.Build().RunAsync(rig.Root, null)).Outcome);

        Assert.False(Directory.Exists(left));
        Assert.True(Directory.Exists(lookalike));
    }

    /// <summary>Removing an unfinished launcher folder needs our marker in it. Without the marker the
    /// folder is somebody else's and stays, even when a rollback asks for it.</summary>
    [Fact]
    public void AFolderWithoutOurMarker_IsNeverRemoved()
    {
        var rig = NewRig();
        var dir = Directory.CreateDirectory(Path.Combine(_tmp, "someone", "Launcher")).FullName;
        File.WriteAllText(Path.Combine(dir, "mine.txt"), "keep");

        rig.Build().RemoveOwnedLeftover(dir);

        Assert.Equal("keep", File.ReadAllText(Path.Combine(dir, "mine.txt")));
    }

    private Rig NewRigIn(string name)
    {
        var rig = NewRig();
        rig.Root = Path.Combine(_tmp, name, "Stonetavern");
        return rig;
    }

    [Fact]
    public async Task Run_AFolderThatIsAlreadySetUp_OpensItsLauncher()
    {
        var rig = NewRig();
        var launcherDir = Directory.CreateDirectory(Path.Combine(rig.Root, "Launcher")).FullName;
        var existing = Path.Combine(launcherDir, LibraryNames.AppImageName);
        File.WriteAllText(existing, "x");
        new LibraryMarker(1, rig.Root, launcherDir, DateTimeOffset.UtcNow).Write();
        var started = new List<string>();
        rig.Start = (f, _, _) => { started.Add(f); return () => false; };

        Assert.Equal(LibrarySetupOutcome.AlreadySetUp, (await rig.Build().RunAsync(rig.Root, null)).Outcome);
        Assert.Equal([existing], started);

        // The same folder, but this process IS that launcher (its config was lost): no self-start loop.
        rig.AppImage = existing;
        started.Clear();
        Assert.Equal(LibrarySetupOutcome.SetUpInPlace, (await rig.Build().RunAsync(rig.Root, null)).Outcome);
        Assert.Empty(started);
        Assert.Equal([(rig.Root, (string?)launcherDir)], rig.InPlaceCommits);
    }

    [Fact]
    public async Task Run_WhereTheLauncherDoesNotMove_SetsUpInPlace()
    {
        var rig = NewRig();
        rig.Os = InstallHostOs.MacOs;

        var result = await rig.Build().RunAsync(rig.Root, null);

        Assert.Equal(LibrarySetupOutcome.SetUpInPlace, result.Outcome);
        Assert.Equal([(Path.GetFullPath(rig.Root), (string?)null)], rig.InPlaceCommits);
        Assert.False(Directory.Exists(Path.Combine(rig.Root, "Launcher")));
    }

    // ── Windows: the complete bundle, proven against the release list ─────────────────────────────

    private (Rig Rig, string BaseDir) WindowsRig()
    {
        var rig = NewRig();
        var baseDir = Directory.CreateDirectory(Path.Combine(_tmp, "Downloads", "Stonetavern-Launcher")).FullName;
        var files = new Dictionary<string, byte[]>
        {
            ["WowLauncher.exe"] = RandomNumberGenerator.GetBytes(4096),
            ["libSkiaSharp.dll"] = RandomNumberGenerator.GetBytes(2048),
            ["tools/butler/win-x64/butler.exe"] = RandomNumberGenerator.GetBytes(1024),
        };
        var list = new StringBuilder();
        foreach (var (rel, bytes) in files)
        {
            var path = Path.Combine(baseDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            list.Append(Convert.ToHexStringLower(SHA256.HashData(bytes))).Append("  ").Append(rel).Append('\n');
        }
        File.WriteAllText(Path.Combine(baseDir, LibraryNames.PayloadList), list.ToString());
        File.WriteAllText(Path.Combine(baseDir, "launcher_config.json"), "{}");   // runtime file: not copied
        rig.Os = InstallHostOs.Windows;
        rig.BaseDir = baseDir;
        rig.ProcessPath = Path.Combine(baseDir, "WowLauncher.exe");
        return (rig, baseDir);
    }

    [Fact]
    public async Task Windows_CopiesExactlyTheReleaseFiles_IncludingTheTools()
    {
        var (rig, _) = WindowsRig();
        rig.Start = CopyThatFinishes([]);

        Assert.Equal(LibrarySetupOutcome.HandedOver, (await rig.Build().RunAsync(rig.Root, null)).Outcome);

        var launcher = Path.Combine(rig.Root, "Launcher");
        Assert.True(File.Exists(Path.Combine(launcher, "WowLauncher.exe")));
        Assert.True(File.Exists(Path.Combine(launcher, "tools", "butler", "win-x64", "butler.exe")));
        Assert.True(File.Exists(Path.Combine(launcher, LibraryNames.PayloadList)));
        Assert.False(File.Exists(Path.Combine(launcher, "launcher_config.json")));
    }

    [Fact]
    public async Task Windows_AFileThatDoesNotMatchTheRelease_StopsTheSetup()
    {
        var (rig, baseDir) = WindowsRig();
        File.WriteAllBytes(Path.Combine(baseDir, "libSkiaSharp.dll"), [1, 2, 3]);   // damaged download
        var started = new List<string>();
        rig.Start = CopyThatFinishes(started);

        Assert.Equal(LibrarySetupOutcome.Failed, (await rig.Build().RunAsync(rig.Root, null)).Outcome);
        Assert.Empty(started);
        Assert.False(Directory.Exists(Path.Combine(rig.Root, "Launcher")));
        Assert.Empty(Directory.Exists(rig.Root) ? Directory.GetDirectories(rig.Root, "Launcher.staging-*") : []);
    }

    [Theory]
    [InlineData("../../evil.exe")]
    [InlineData("/etc/passwd")]
    public void Windows_AReleaseListThatPointsOutside_IsRefused(string rel)
    {
        var file = Path.Combine(_tmp, "PAYLOAD.sha256");
        File.WriteAllText(file, new string('a', 64) + "  " + rel + "\n");
        Assert.Throws<InvalidDataException>(() => LibrarySetup.ReadPayloadList(file));
    }

    [Fact]
    public async Task Windows_WithoutAReleaseList_SetsUpInPlace_InsteadOfAPartialCopy()
    {
        var (rig, baseDir) = WindowsRig();
        File.Delete(Path.Combine(baseDir, LibraryNames.PayloadList));

        Assert.Equal(LibrarySetupOutcome.SetUpInPlace, (await rig.Build().RunAsync(rig.Root, null)).Outcome);
        Assert.False(Directory.Exists(Path.Combine(rig.Root, "Launcher")));
    }

    // ── the commit ───────────────────────────────────────────────────────────────────────────────

    private sealed class MemoryConfig : IConfigService
    {
        public LauncherConfig Cfg = new();
        public LauncherConfig Load() => Cfg;
        public void Save(LauncherConfig config) => Cfg = config;
        public bool LastSaveSucceeded { get; set; } = true;
    }

    [Fact]
    public void Commit_WritesTheConfigFirstAndTheMarkerLast()
    {
        var root = Path.Combine(_tmp, "lib");
        var config = new MemoryConfig { LastSaveSucceeded = false };

        Assert.Throws<IOException>(() => LibrarySetup.Commit(config, root, Path.Combine(root, "Launcher")));
        Assert.Null(LibraryMarker.TryRead(root));   // no config, no marker

        config.LastSaveSucceeded = true;
        LibrarySetup.Commit(config, root, Path.Combine(root, "Launcher"));
        Assert.Equal(root, config.Cfg.LibraryRoot);
        Assert.Equal(Path.Combine(root, "Launcher"), LibraryMarker.TryRead(root)!.LauncherDir);
        if (OperatingSystem.IsLinux()) Assert.True(config.Cfg.DesktopIntegrationDone);
    }
}
