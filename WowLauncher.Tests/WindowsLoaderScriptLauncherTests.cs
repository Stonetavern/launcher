using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Windows pendant to <see cref="LoaderScriptLauncherTests"/>: the tuned 1.12.1 client must launch
/// through its own <c>launch.bat</c> (VanillaFixes + DXVK + display setup), never bare
/// <c>WoW.exe</c>. These prove the loader is preferred when present and that an install without one
/// keeps the plain native start (no regression).
/// </summary>
public sealed class WindowsLoaderScriptLauncherTests
{
    // Pure-function tests use forward-slash paths so they exercise the same branching on the Linux test
    // host as on Windows (Path.GetDirectoryName/Combine are host-path-semantic; the branching logic is
    // identical). The production launcher runs on Windows, where these are the on-disk paths.
    [Fact]
    public void FindLoaderScript_InWorkingDirectory()
    {
        var script = WindowsLoaderScriptLauncher.FindLoaderScript(
            exePath: "/games/wow/WoW.exe", workingDirectory: "/games/wow",
            fileExists: p => p == Path.Combine("/games/wow", "launch.bat"));
        Assert.Equal(Path.Combine("/games/wow", "launch.bat"), script);
    }

    [Fact]
    public void FindLoaderScript_NextToExe_WhenWorkingDirDiffers()
    {
        // The resolved exe can sit a level down from the working dir; the loader lives beside the exe.
        var script = WindowsLoaderScriptLauncher.FindLoaderScript(
            exePath: "/games/wow/inner/WoW.exe", workingDirectory: "/games/wow",
            fileExists: p => p == Path.Combine("/games/wow/inner", "launch.bat"));
        Assert.Equal(Path.Combine("/games/wow/inner", "launch.bat"), script);
    }

    [Fact]
    public void FindLoaderScript_NullWhenNoLoader()
    {
        var script = WindowsLoaderScriptLauncher.FindLoaderScript(
            "/games/wow/WoW.exe", "/games/wow", fileExists: _ => false);
        Assert.Null(script);
    }

    [Fact]
    public void FindLoaderScript_IgnoresTheLinuxLoaderName()
    {
        // Only launch.bat counts on Windows — a launch.sh present must NOT be picked up (that would run
        // a bash script through cmd and fail); mutation guard against a copy-pasted "launch.sh" constant.
        var script = WindowsLoaderScriptLauncher.FindLoaderScript(
            "/games/wow/WoW.exe", "/games/wow",
            fileExists: p => p == Path.Combine("/games/wow", "launch.sh"));
        Assert.Null(script);
    }

    [Fact]
    public async Task Launch_RunsTheLoader_WhenPresent()
    {
        var inner = new RecordingLauncher();
        string? ran = null;
        var launcher = new WindowsLoaderScriptLauncher(inner, Serilog.Log.Logger,
            runScript: (s, _) => { ran = s; return GameLaunchResult.Ok(4242); });

        using var t = new TempClient(withLoader: true);
        var result = await launcher.LaunchAsync(Path.Combine(t.Dir, "WoW.exe"), t.Dir);

        Assert.True(result.Started);
        Assert.Equal(4242, result.ProcessId);
        Assert.Equal(Path.Combine(t.Dir, "launch.bat"), ran); // the loader ran…
        Assert.False(inner.WasCalled);                         // …and the bare native start did NOT
    }

    [Fact]
    public async Task Launch_DelegatesToInner_WhenNoLoader_AndNoRealmIsWired()
    {
        // Standalone case only (no realm resolver): the STARTER falls back to the plain native start.
        // What this test used to also cover — a wired realm taking the same fallback untouched — moved
        // to the three tests below, because it was the fail-open Codex found (2026-08-09, finding 2).
        var inner = new RecordingLauncher();
        var launcher = new WindowsLoaderScriptLauncher(inner, Serilog.Log.Logger,
            runScript: (_, _) => GameLaunchResult.Ok(1)); // must not be used

        using var t = new TempClient(withLoader: false);
        var result = await launcher.LaunchAsync(Path.Combine(t.Dir, "WoW.exe"), t.Dir);

        Assert.True(inner.WasCalled);                 // fell back to the plain native start
        Assert.Equal(t.Dir, inner.LastWorkingDir);
        Assert.True(result.Started);
    }

    // --- No launch.bat is not a licence to skip the realm binding ---------------------------------
    // Codex review 2026-08-09, finding 2. The "no loader batch" branch used to return into the inner
    // launcher immediately — before RealmAddress.Parse, before WriteClientRealm. A player using the
    // supported "Locate installed WoW" on a 1.12.1 install without a batch therefore started on
    // whatever realm the files carried, while the launcher showed another one. ConfigureClient does not
    // cover this: it logs-and-returns on an invalid address and swallows write failures.
    //
    // The expectation these three encode is deliberately the OPPOSITE of what the old
    // Launch_DelegatesToInner_WhenNoLoader asserted for a wired realm: fail-closed, not fail-open.

    [Fact]
    public async Task Launch_WithoutALoader_StillWritesTheRealm_BeforeTheNativeStart()
    {
        var inner = new RecordingLauncher();
        var launcher = new WindowsLoaderScriptLauncher(
            inner, Serilog.Log.Logger,
            realmAddress: () => "play.example.invalid:3725",
            runScript: (_, _) => GameLaunchResult.Ok(1)); // no batch, so this must not be used

        using var t = new TempClient(withLoader: false);
        Directory.CreateDirectory(Path.Combine(t.Dir, "WTF"));
        File.WriteAllText(Path.Combine(t.Dir, "WTF", "Config.wtf"),
            "SET realmList \"play.stonetavern.app\"\n");

        var result = await launcher.LaunchAsync(Path.Combine(t.Dir, "WoW.exe"), t.Dir);

        Assert.True(result.Started);
        Assert.True(inner.WasCalled);   // still the plain native start…
        var config = File.ReadAllText(Path.Combine(t.Dir, "WTF", "Config.wtf"));
        Assert.Contains("SET realmList \"play.example.invalid:3725\"", config, System.StringComparison.Ordinal);
        Assert.DoesNotContain("stonetavern", config, System.StringComparison.OrdinalIgnoreCase);
        Assert.Equal("set realmlist play.example.invalid:3725\n",
            File.ReadAllText(Path.Combine(t.Dir, "realmlist.wtf")));  // …but pointed at the right realm
    }

    [Fact]
    public async Task Launch_WithoutALoader_RefusesAnInvalidRealm_InsteadOfStartingNatively()
    {
        var inner = new RecordingLauncher();
        var launcher = new WindowsLoaderScriptLauncher(
            inner, Serilog.Log.Logger,
            realmAddress: () => "play.example.invalid\nSET portal \"evil\"");

        using var t = new TempClient(withLoader: false);
        File.WriteAllText(Path.Combine(t.Dir, "realmlist.wtf"), "set realmlist play.stonetavern.app\n");

        var result = await launcher.LaunchAsync(Path.Combine(t.Dir, "WoW.exe"), t.Dir);

        Assert.False(result.Started);
        Assert.False(inner.WasCalled);
        Assert.Contains("realm address", result.Error, System.StringComparison.OrdinalIgnoreCase);
        Assert.Equal("set realmlist play.stonetavern.app\n",
            File.ReadAllText(Path.Combine(t.Dir, "realmlist.wtf")));  // nothing written before the refusal
    }

    [Fact]
    public async Task Launch_WithoutALoader_RefusesWhenTheRealmCannotBeWritten()
    {
        var inner = new RecordingLauncher();
        var launcher = new WindowsLoaderScriptLauncher(
            inner, Serilog.Log.Logger,
            realmAddress: () => "realm.example.invalid");

        using var t = new TempClient(withLoader: false);
        // A directory where realmlist.wtf must go: the write cannot land, so the launch must not happen.
        Directory.CreateDirectory(Path.Combine(t.Dir, "realmlist.wtf"));

        var result = await launcher.LaunchAsync(Path.Combine(t.Dir, "WoW.exe"), t.Dir);

        Assert.False(result.Started);
        Assert.False(inner.WasCalled);
        Assert.Contains(t.Dir, result.Error, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task Launch_PassesTheSelectedRealmToTheLoader()
    {
        var inner = new RecordingLauncher();
        IReadOnlyDictionary<string, string>? passed = null;
        var launcher = new WindowsLoaderScriptLauncher(
            inner, Serilog.Log.Logger,
            realmAddress: () => "play.example.invalid:3725",
            runScript: (_, environment) => { passed = environment; return GameLaunchResult.Ok(4242); });

        using var t = new TempClient(withLoader: true);
        var result = await launcher.LaunchAsync(Path.Combine(t.Dir, "WoW.exe"), t.Dir);

        Assert.True(result.Started);
        Assert.NotNull(passed);
        Assert.Equal("play.example.invalid:3725", passed![RealmBinding.RealmlistEnvVar]);
    }

    [Fact]
    public async Task Launch_RefusesAnInvalidRealmInsteadOfUsingTheLoaderDefault()
    {
        var inner = new RecordingLauncher();
        var called = false;
        var launcher = new WindowsLoaderScriptLauncher(
            inner, Serilog.Log.Logger,
            realmAddress: () => "play.example.invalid\nSET portal \"evil\"",
            runScript: (_, _) => { called = true; return GameLaunchResult.Ok(4242); });

        using var t = new TempClient(withLoader: true);
        var result = await launcher.LaunchAsync(Path.Combine(t.Dir, "WoW.exe"), t.Dir);

        Assert.False(result.Started);
        Assert.False(called);
        Assert.False(inner.WasCalled);
        Assert.Contains("realm address", result.Error, System.StringComparison.OrdinalIgnoreCase);
    }

    // --- The launcher writes the realm into the client itself -------------------------------------
    // The packaged launch.bat reads REALMLIST nowhere (verified 2026-08-09 against
    // /mnt/data/wow/clients/1.12.1-vanilla-enhanced/launch.bat), so the environment alone proves
    // nothing. These tests hold the FILES against the selected realm, not the exit code.

    [Fact]
    public async Task Launch_WritesTheSelectedRealmIntoTheClientFiles()
    {
        var launcher = new WindowsLoaderScriptLauncher(
            new RecordingLauncher(), Serilog.Log.Logger,
            realmAddress: () => "play.example.invalid:3725",
            runScript: (_, _) => GameLaunchResult.Ok(4242));

        using var t = new TempClient(withLoader: true);
        // Stale values from a previously shipped package — these are exactly what would silently win.
        Directory.CreateDirectory(Path.Combine(t.Dir, "WTF"));
        File.WriteAllText(Path.Combine(t.Dir, "WTF", "Config.wtf"),
            "SET locale \"deDE\"\nSET realmList \"play.stonetavern.app\"\n");
        File.WriteAllText(Path.Combine(t.Dir, "realmlist.wtf"), "set realmlist play.stonetavern.app\n");

        var result = await launcher.LaunchAsync(Path.Combine(t.Dir, "WoW.exe"), t.Dir);

        Assert.True(result.Started);
        // Same semantics as launch.sh: quoted SET line in Config.wtf, bare line in realmlist.wtf, LF.
        var config = File.ReadAllText(Path.Combine(t.Dir, "WTF", "Config.wtf"));
        Assert.Contains("SET realmList \"play.example.invalid:3725\"", config, System.StringComparison.Ordinal);
        Assert.DoesNotContain("stonetavern", config, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SET locale \"deDE\"", config, System.StringComparison.Ordinal); // rest preserved
        Assert.Equal("set realmlist play.example.invalid:3725\n",
            File.ReadAllText(Path.Combine(t.Dir, "realmlist.wtf")));
    }

    [Fact]
    public async Task Launch_CreatesTheWtfConfig_WhenTheClientHasNone()
    {
        var launcher = new WindowsLoaderScriptLauncher(
            new RecordingLauncher(), Serilog.Log.Logger,
            realmAddress: () => "realm.example.invalid",
            runScript: (_, _) => GameLaunchResult.Ok(7));

        using var t = new TempClient(withLoader: true);
        var result = await launcher.LaunchAsync(Path.Combine(t.Dir, "WoW.exe"), t.Dir);

        Assert.True(result.Started);
        Assert.Equal("SET realmList \"realm.example.invalid\"\n",
            File.ReadAllText(Path.Combine(t.Dir, "WTF", "Config.wtf")));
    }

    [Fact]
    public async Task Launch_WritesNothing_WhenTheRealmIsInvalid()
    {
        var launcher = new WindowsLoaderScriptLauncher(
            new RecordingLauncher(), Serilog.Log.Logger,
            realmAddress: () => "play.example.invalid\nSET portal \"evil\"",
            runScript: (_, _) => GameLaunchResult.Ok(1));

        using var t = new TempClient(withLoader: true);
        File.WriteAllText(Path.Combine(t.Dir, "realmlist.wtf"), "set realmlist play.stonetavern.app\n");

        var result = await launcher.LaunchAsync(Path.Combine(t.Dir, "WoW.exe"), t.Dir);

        Assert.False(result.Started);
        // Refusal comes BEFORE any write: the old file is untouched and no WTF/ was created.
        Assert.Equal("set realmlist play.stonetavern.app\n",
            File.ReadAllText(Path.Combine(t.Dir, "realmlist.wtf")));
        Assert.False(Directory.Exists(Path.Combine(t.Dir, "WTF")));
    }

    [Fact]
    public async Task Launch_RefusesReadably_WhenTheClientCannotBeWritten()
    {
        var launcher = new WindowsLoaderScriptLauncher(
            new RecordingLauncher(), Serilog.Log.Logger,
            realmAddress: () => "realm.example.invalid",
            runScript: (_, _) => GameLaunchResult.Ok(1));

        using var t = new TempClient(withLoader: true);
        // A directory where realmlist.wtf must go: the write fails, and a failed write must never
        // become a silent start on whatever realm the client already carried.
        Directory.CreateDirectory(Path.Combine(t.Dir, "realmlist.wtf"));

        var result = await launcher.LaunchAsync(Path.Combine(t.Dir, "WoW.exe"), t.Dir);

        Assert.False(result.Started);
        Assert.Contains("realm address", result.Error, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains(t.Dir, result.Error, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task Launch_LeavesTheClientAlone_WhenNoRealmResolverIsWired()
    {
        // Standalone-client behaviour (no resolver): unchanged, the launcher writes nothing.
        var launcher = new WindowsLoaderScriptLauncher(
            new RecordingLauncher(), Serilog.Log.Logger,
            runScript: (_, _) => GameLaunchResult.Ok(5));

        using var t = new TempClient(withLoader: true);
        var result = await launcher.LaunchAsync(Path.Combine(t.Dir, "WoW.exe"), t.Dir);

        Assert.True(result.Started);
        Assert.False(File.Exists(Path.Combine(t.Dir, "realmlist.wtf")));
        Assert.False(Directory.Exists(Path.Combine(t.Dir, "WTF")));
    }

    private sealed class RecordingLauncher : IGameLauncher
    {
        public bool WasCalled { get; private set; }
        public string? LastWorkingDir { get; private set; }
        public Task<GameLaunchResult> LaunchAsync(string exePath, string workingDirectory)
        {
            WasCalled = true;
            LastWorkingDir = workingDirectory;
            return Task.FromResult(GameLaunchResult.Ok(99));
        }
    }

    private sealed class TempClient : System.IDisposable
    {
        public string Dir { get; }
        public TempClient(bool withLoader)
        {
            Dir = Path.Combine(Path.GetTempPath(), "mechagon-winloader-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, "WoW.exe"), "stub");
            if (withLoader) File.WriteAllText(Path.Combine(Dir, "launch.bat"), "@echo off\r\n");
        }
        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* temp */ }
        }
    }
}
