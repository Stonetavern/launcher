using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Three findings, all rooted in the same bug: the 1.14.2 Classic Era client is <c>WowClassic.exe</c>,
/// not <c>WoW.exe</c>, and <see cref="ClientVersion.ExeNames"/> is the one place that is supposed to
/// know that. Two call sites in <see cref="ClientService"/> used to bypass it with a hardcoded
/// "WoW.exe" — a silent miss (Befund 1: <c>IsValidWowDir</c> never recognised an installed 1.14.2
/// client) and a safety gap (Befund 2: <c>ResolveKnownWowExe</c> never handed the Linux running-game
/// detector a path for that build, so the "don't overwrite files the game has open" guard never
/// fired for it). Befund 3 is dead code (<c>StubGameLauncher</c>) plus stale "WP2" comments; guarded
/// here by an assembly scan so it cannot silently reappear.
/// </summary>
public sealed class ClientExeNameTests
{
    // ── shared temp-dir plumbing ────────────────────────────────────────────────────────────────

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"exe-name-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort tmp cleanup */ }
    }

    /// <summary>Writes a minimal client dir: the given exe name plus a Data/ folder with one .MPQ,
    /// which is exactly what <c>IsValidWowDir</c> requires.</summary>
    private static string WriteClientDir(string exeName)
    {
        var dir = NewTempDir();
        File.WriteAllText(Path.Combine(dir, exeName), "not-a-real-pe");
        var data = Path.Combine(dir, "Data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "patch.MPQ"), "mpq-content");
        return dir;
    }

    /// <summary>Reflection into the private static <c>IsValidWowDir</c> — the acceptance criterion
    /// (§1) requires it to keep using <see cref="ClientVersion.ExeNames"/> without becoming instance
    /// state or public API, so the only honest way to test the exact predicate is to call it directly
    /// rather than go through the build-detection heuristic in <c>DetectBuild</c> (which cannot infer
    /// build 42597 from a fake, non-PE exe and would mask the very check under test).</summary>
    private static bool InvokeIsValidWowDir(string dir)
    {
        var method = typeof(ClientService).GetMethod("IsValidWowDir", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ClientService.IsValidWowDir not found — signature changed?");
        return (bool)method.Invoke(null, [dir])!;
    }

    [Fact]
    public void IsValidWowDir_RecognisesClassicEra_WowClassicExe()
    {
        var dir = WriteClientDir("WowClassic.exe");
        try
        {
            Assert.True(InvokeIsValidWowDir(dir),
                "A 1.14.2 Classic Era install (WowClassic.exe + Data/*.MPQ) must be recognised as a valid client dir. " +
                "Befund 1: IsValidWowDir hardcoded 'WoW.exe' and silently never detected this build.");
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public void IsValidWowDir_StillRecognisesClassicExeName()
    {
        // Guards against a fix that only special-cases WowClassic.exe and drops the original WoW.exe case.
        var dir = WriteClientDir("WoW.exe");
        try
        {
            Assert.True(InvokeIsValidWowDir(dir), "WoW.exe (1.12.1/2.4.3/3.3.5a) must remain a valid client exe name.");
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public void IsValidWowDir_RejectsDirWithoutAnyKnownExe()
    {
        var dir = NewTempDir();
        try
        {
            var data = Path.Combine(dir, "Data");
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "patch.MPQ"), "mpq-content");
            // No exe of any known name written.
            Assert.False(InvokeIsValidWowDir(dir));
        }
        finally { DeleteDir(dir); }
    }

    // ── Befund 2: ResolveKnownWowExe / IsGameRunning must hand the detector a WowClassic.exe path ──

    private sealed class MemoryConfig(LauncherConfig config) : IConfigService
    {
        public LauncherConfig Load() => config;
        public void Save(LauncherConfig c) { }
        public bool LastSaveSucceeded => true;
    }

    private sealed class EmptyRoots : IInstallRootsProvider
    {
        public IEnumerable<string> ExeSearchPaths(string configuredPath) => [];
        public IEnumerable<string> CommonInstallRoots() => [];
        public IReadOnlyList<string> InstallFolderNames() => [];
    }

    /// <summary>Records exactly which path the launcher handed to the "is the game running?" detector,
    /// so the test can assert it names the real installed exe rather than null (which would mean the
    /// concurrency guard degrades to the path-agnostic, less precise Windows-style scan or — on Linux,
    /// where the detector requires a path — never fires at all).</summary>
    private sealed class RecordingDetector : IGameProcessDetector
    {
        public string? LastExpectedExePath;
        public bool IsGameRunning(string? expectedExePath)
        {
            LastExpectedExePath = expectedExePath;
            return false;
        }
    }

    private sealed class NullLauncher : IGameLauncher
    {
        public Task<GameLaunchResult> LaunchAsync(string exePath, string workingDirectory) =>
            Task.FromResult(GameLaunchResult.Failed("unused"));
    }

    [Fact]
    public void IsGameRunning_ResolvesClassicEraInstall_ToWowClassicExePath()
    {
        var dir = WriteClientDir("WowClassic.exe");
        try
        {
            var config = new LauncherConfig
            {
                ClientInstalls = new Dictionary<int, string> { [42597] = dir },
            };
            var detector = new RecordingDetector();
            var service = new ClientService(
                new MemoryConfig(config),
                new Serilog.LoggerConfiguration().CreateLogger(),
                new EmptyRoots(),
                detector,
                new NullLauncher());

            service.IsGameRunning();

            Assert.NotNull(detector.LastExpectedExePath);
            Assert.Equal("WowClassic.exe", Path.GetFileName(detector.LastExpectedExePath));
        }
        finally { DeleteDir(dir); }
    }

    // ── Befund 3: StubGameLauncher is dead code and must not resurface ─────────────────────────────

    [Fact]
    public void StubGameLauncher_NoLongerExists()
    {
        var found = typeof(IGameLauncher).Assembly.GetTypes().Any(t => t.Name == "StubGameLauncher");
        Assert.False(found,
            "StubGameLauncher was unreferenced dead code (WineGameLauncher is the real Linux impl, " +
            "registered in DependencyInjection.cs) with a stale 'WP2' comment. It must stay removed.");
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // Codex adversarial-review findings (arbitrated by the orchestrator, all four real):
    // the previous pass fixed the SENDER side (ClientService now hands out a WowClassic.exe path) but
    // not the RECEIVER side (the detectors that path is handed to) — a running 1.14.2 client still was
    // not recognised as running, and IsValidWowDir's own fix opened a new mislabel (WowClassic.exe with
    // an unreadable version falling through to the Vanilla MPQ heuristic and registering as 5875).
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    // ── Finding 1 (KRITISCH): both real process detectors must recognise a running WowClassic ─────

    private static string? FindRealBinary(params string[] candidates) =>
        candidates.FirstOrDefault(File.Exists);

    /// <summary>Copies a real, long-running ELF binary to <paramref name="destFileName"/> inside a
    /// fresh temp dir and starts it detached (arg makes it sleep long enough for the assertions).
    /// Returns (path, process) so the caller can kill it in a finally.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static (string path, System.Diagnostics.Process proc) StartRenamedProcess(string destFileName)
    {
        var dir = NewTempDir();
        try
        {
            var binary = FindRealBinary("/usr/bin/sleep", "/bin/sleep")
                ?? throw new InvalidOperationException("no sleep binary found for the test double");
            var dest = Path.Combine(dir, destFileName);
            File.Copy(binary, dest);
            File.SetUnixFileMode(dest, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            var psi = new System.Diagnostics.ProcessStartInfo(dest) { UseShellExecute = false };
            psi.ArgumentList.Add("20"); // seconds — long enough for the assertions below
            var proc = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
            return (dest, proc);
        }
        catch
        {
            // Setup itself failed (missing binary, copy/chmod/spawn error) — don't leak the temp dir.
            DeleteDir(dir);
            throw;
        }
    }

    private static void KillAndCleanup(System.Diagnostics.Process proc, string exePath)
    {
        try { if (!proc.HasExited) proc.Kill(); } catch { /* best effort */ }
        try { proc.WaitForExit(2000); } catch { /* best effort */ }
        DeleteDir(Path.GetDirectoryName(exePath)!);
    }

    /// <summary>Befund 1a: <see cref="WindowsGameProcessDetector"/> derives its process-name scan from
    /// <see cref="ClientVersion.ExeNames"/> (extension stripped). <c>Process.GetProcessesByName</c> is
    /// cross-platform in .NET (backed by /proc on Linux), so the REAL detector class can be exercised
    /// here without a Windows host: a real process whose executable is literally named "WowClassic"
    /// must be found — before the fix, only a process named "WoW" ever matched.</summary>
    [SkippableFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void WindowsGameProcessDetector_FindsRunning_WowClassicProcess()
    {
        Skip.IfNot(FindRealBinary("/usr/bin/sleep", "/bin/sleep") is not null, "no sleep binary available on this host");

        var (path, proc) = StartRenamedProcess("WowClassic"); // no .exe — this is the OS process name
        try
        {
            var detector = new WindowsGameProcessDetector(new Serilog.LoggerConfiguration().CreateLogger());
            // Poll instead of a fixed sleep — the OS needs a moment to register the process under its
            // new comm/name, and a fixed wait is flaky on a slow/loaded machine (Orchestrator non-blocker).
            var found = PollUntil(() => detector.IsGameRunning(null), TimeSpan.FromSeconds(5));

            Assert.True(found,
                "A running process named 'WowClassic' (the 1.14.2 client) must be detected — " +
                "Befund 1: the scan used to hardcode process name 'WoW' only.");
        }
        finally { KillAndCleanup(proc, path); }
    }

    /// <summary>Befund 1b: <see cref="LinuxGameProcessDetector.IsWowExeLeaf"/> (private, exercised via
    /// the public <c>IsGameRunning</c>) must accept every <see cref="ClientVersion.ExeNames"/> leaf, not
    /// only "WoW.exe". Real /proc scan, real spawned process — no wine needed for the direct case (a
    /// natively-executed ELF whose on-disk name happens to be WowClassic.exe, mirroring how a
    /// hand-copied client dir would look before Wine translation even enters the picture).</summary>
    [SkippableFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void LinuxGameProcessDetector_FindsRunning_WowClassicExe()
    {
        Skip.IfNot(FindRealBinary("/usr/bin/sleep", "/bin/sleep") is not null, "no sleep binary available on this host");

        var (path, proc) = StartRenamedProcess("WowClassic.exe");
        try
        {
            var detector = new LinuxGameProcessDetector(new Serilog.LoggerConfiguration().CreateLogger());
            var found = PollUntil(() => detector.IsGameRunning(path), TimeSpan.FromSeconds(5));

            Assert.True(found,
                "A running process whose image is literally 'WowClassic.exe' must be found via /proc — " +
                "Befund 1: IsWowExeLeaf used to accept only 'WoW.exe', so a 1.14.2 client running under " +
                "Wine (or natively) was invisible to the concurrency guard.");
        }
        finally { KillAndCleanup(proc, path); }
    }

    private static bool PollUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition()) return true;
            System.Threading.Thread.Sleep(100);
        }
        return condition();
    }

    // ── Finding 2 (HOCH): exe name must outrank the Vanilla MPQ heuristic ───────────────────────────

    private sealed class FixedRoots(IReadOnlyList<string> roots) : IInstallRootsProvider
    {
        public IEnumerable<string> ExeSearchPaths(string configuredPath) => [];
        public IEnumerable<string> CommonInstallRoots() => roots;
        public IReadOnlyList<string> InstallFolderNames() => [];
    }

    [Fact]
    public void DetectInstalls_WowClassicExeWithPatchMpq_RegistersAs42597_NotVanilla5875()
    {
        // Same fixture Codex flagged: WowClassic.exe + a generic patch.MPQ (no lichking/expansion MPQ),
        // which is exactly what InferBuildFromData's fallback maps to 5875 when it (wrongly) gets to run.
        var dir = WriteClientDir("WowClassic.exe");
        try
        {
            var config = new LauncherConfig(); // no prior known installs — forces the filesystem scan
            var service = new ClientService(
                new MemoryConfig(config),
                new Serilog.LoggerConfiguration().CreateLogger(),
                new FixedRoots([dir]),
                new RecordingDetector(),
                new NullLauncher());

            var found = service.DetectInstalls(new Dictionary<int, string>());

            Assert.True(found.TryGetValue(42597, out var registeredDir),
                "A WowClassic.exe + Data/patch.MPQ install must register under build 42597 (1.14.2). " +
                "Befund 2: an unreadable exe version used to fall back to the Vanilla MPQ heuristic and " +
                "mislabel this as build 5875.");
            Assert.Equal(Path.GetFullPath(dir), registeredDir);
            Assert.False(found.ContainsKey(5875), "must NOT also (or instead) register as Vanilla 5875.");
        }
        finally { DeleteDir(dir); }
    }

    // ── Finding 3 (MITTEL): FindWowExe must fall back through every known exe name ──────────────────

    [Fact]
    public void FindWowExe_FindsWowClassicExe_ViaNeutralProviderFallbackPaths()
    {
        // NeutralInstallRootsProvider.ExeSearchPaths yields AppContext.BaseDirectory/<exe> for every
        // known exe name — drop a WowClassic.exe there (the test binary's own output dir) and confirm
        // the real provider + real ClientService.FindWowExe fall-back path finds it via that route
        // rather than the (irrelevant here) configuredPath/ResolveExeInDir branch.
        var probeExe = Path.Combine(AppContext.BaseDirectory, "WowClassic.exe");
        var alreadyThere = File.Exists(probeExe);
        if (!alreadyThere) File.WriteAllText(probeExe, "not-a-real-pe");
        try
        {
            var config = new LauncherConfig();
            var service = new ClientService(
                new MemoryConfig(config),
                new Serilog.LoggerConfiguration().CreateLogger(),
                new NeutralInstallRootsProvider(),
                new RecordingDetector(),
                new NullLauncher());

            var result = service.FindWowExe(configuredPath: "definitely-does-not-exist-anywhere");

            Assert.NotNull(result);
            Assert.Equal("WowClassic.exe", Path.GetFileName(result));
        }
        finally { if (!alreadyThere) { try { File.Delete(probeExe); } catch { /* best effort */ } } }
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // Orchestrator Round 2 — arbitration of Codex's Round-2 adversarial review. Two blockers:
    // Blocker 1: UniqueBuildForExeName must only outrank the WEAKER evidence (an unreadable/zero
    // version, or the MPQ heuristic) — never the STRONGER evidence of a readable, non-zero version
    // that names a genuinely different (if unknown) build. Blocker 2: FindWowExeForBuild's stale-
    // registration fallback must not hand back an exe that unambiguously belongs to ANOTHER build.
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    // ── Blocker 1: a readable version naming an unknown build must win over the exe name ──────────

    /// <summary>The real, already-built <c>WowLauncher.dll</c> — a genuine PE with a genuine, readable
    /// Windows-style file-version resource (<c>FileVersion 1.0.1.0</c> per WowLauncher.csproj's
    /// <c>&lt;FileVersion&gt;</c>), so <c>FileBuildPart</c> is 1: readable, non-zero, and NOT one of
    /// the launcher's known builds (5875/8606/12340/42597). This is the one case where fabricating a
    /// real Windows PE-with-version-resource test fixture from scratch is not needed — the build already
    /// produced one. Copying it and renaming it WowClassic.exe reproduces exactly the scenario Codex
    /// named: "a real other Classic-line client that happens to share the exe name".</summary>
    /// <summary>
    /// A real PE whose version resource is READABLE, whose build field is non-zero, and whose build
    /// the launcher does not know. Exactly the shape DetectBuild must answer "null" for.
    ///
    /// <para>This used to be hardcoded to the launcher assembly, and the case only existed because
    /// the launcher happened to be versioned 1.0.<b>1</b>. Bumping it to 1.1.<b>0</b> made the third
    /// field zero, DetectBuild fell through to the exe-name branch, and the test went red without a
    /// single product bug: the fixture had quietly stopped being a fixture. Picking any suitable
    /// assembly from the output folder (the Avalonia ones are versioned 11.x.y.z) makes the test
    /// independent of what WE are versioned as, forever.</para>
    /// </summary>
    private static string? RealPeWithReadableUnknownVersion
    {
        get
        {
            var dir = Path.GetDirectoryName(typeof(ClientService).Assembly.Location);
            if (string.IsNullOrEmpty(dir)) return null;
            foreach (var dll in Directory.EnumerateFiles(dir, "*.dll").OrderBy(f => f, StringComparer.Ordinal))
            {
                int build;
                try { build = System.Diagnostics.FileVersionInfo.GetVersionInfo(dll).FileBuildPart; }
                catch { continue; }
                if (build == 0) continue;
                if (ClientVersion.All.Any(c => c.Build == build)) continue;
                return dll;
            }
            return null;
        }
    }

    [SkippableFact]
    public void DetectBuild_ReadableVersionNamingUnknownBuild_ReturnsNull_NameDoesNotOverride()
    {
        var dir = NewTempDir();
        try
        {
            var src = RealPeWithReadableUnknownVersion;
            Skip.If(src is null,
                "no assembly next to the tests carries a readable, non-zero, unknown build field");
            File.Copy(src!, Path.Combine(dir, "WowClassic.exe"));
            // No Data/*.MPQ needed — DetectBuild only looks at the exe.

            var service = new ClientService(
                new MemoryConfig(new LauncherConfig()),
                new Serilog.LoggerConfiguration().CreateLogger(),
                new EmptyRoots(),
                new RecordingDetector(),
                new NullLauncher());

            var build = service.DetectBuild(dir);

            Assert.Null(build);
            // Documents exactly what must NOT happen — the WowClassic.exe name pulling this to 42597
            // even though its (readable!) version says build 1, a build the launcher does not know.
            Assert.NotEqual(42597, build);
        }
        finally { DeleteDir(dir); }
    }

    private static void Skip_IfMissing(string path) =>
        Skip.IfNot(File.Exists(path), $"expected the built WowLauncher.dll at {path} — run a build first");


    // ── Blocker 2: the stale-registration fallback must not start the WRONG client ──────────────────

    [Fact]
    public void FindWowExeForBuild_RejectsWoWExeFallback_ForBuild42597()
    {
        // A player registered this dir for 42597 before the launcher knew the build, but the dir only
        // has WoW.exe (a leftover Vanilla install, or simply never had WowClassic.exe at all). WoW.exe
        // unambiguously belongs to 5875/8606/12340 — starting it for a 1.14.2 realm is the wrong client.
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "WoW.exe"), "not-a-real-pe");
            var service = new ClientService(
                new MemoryConfig(new LauncherConfig()),
                new Serilog.LoggerConfiguration().CreateLogger(),
                new EmptyRoots(),
                new RecordingDetector(),
                new NullLauncher());

            var result = service.FindWowExeForBuild(42597, new Dictionary<int, string> { [42597] = dir });

            Assert.Null(result);
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public void FindWowExeForBuild_StillAcceptsWoWExeFallback_ForVanillaBuild_WhenShippedNameMissing()
    {
        // Regression guard: the tightened fallback must not become so strict it breaks the ORIGINAL
        // reason the fallback exists — a player who registered a Vanilla dir before the launcher had
        // WowClassic.exe in its vocabulary, where the actually-installed exe is a differently-named
        // but still build-5875-eligible one is out of scope here; what must keep working is the plain
        // case: build 5875's own shipped name (WoW.exe) is itself found via the fallback list.
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "WoW.exe"), "not-a-real-pe");
            var service = new ClientService(
                new MemoryConfig(new LauncherConfig()),
                new Serilog.LoggerConfiguration().CreateLogger(),
                new EmptyRoots(),
                new RecordingDetector(),
                new NullLauncher());

            var result = service.FindWowExeForBuild(5875, new Dictionary<int, string> { [5875] = dir });

            Assert.NotNull(result);
            Assert.Equal("WoW.exe", Path.GetFileName(result));
        }
        finally { DeleteDir(dir); }
    }
}
