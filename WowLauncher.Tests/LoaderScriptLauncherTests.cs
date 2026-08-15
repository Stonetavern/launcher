using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The tuned 1.12.1 client must launch through its own <c>launch.sh</c> (VanillaFixes + DXVK + display
/// setup), never bare <c>wine WoW.exe</c>. These prove the loader is preferred when present and that an
/// install without one keeps the plain wine start (no regression).
/// </summary>
public sealed class LoaderScriptLauncherTests
{
    [Fact]
    public void FindLoaderScript_InWorkingDirectory()
    {
        var script = LoaderScriptLauncher.FindLoaderScript(
            exePath: "/games/wow/WoW.exe", workingDirectory: "/games/wow",
            fileExists: p => p == Path.Combine("/games/wow", "launch.sh"));
        Assert.Equal(Path.Combine("/games/wow", "launch.sh"), script);
    }

    [Fact]
    public void FindLoaderScript_NextToExe_WhenWorkingDirDiffers()
    {
        // The resolved exe can sit a level down from the working dir; the loader lives beside the exe.
        var script = LoaderScriptLauncher.FindLoaderScript(
            exePath: "/games/wow/inner/WoW.exe", workingDirectory: "/games/wow",
            fileExists: p => p == Path.Combine("/games/wow/inner", "launch.sh"));
        Assert.Equal(Path.Combine("/games/wow/inner", "launch.sh"), script);
    }

    [Fact]
    public void FindLoaderScript_NullWhenNoLoader()
    {
        var script = LoaderScriptLauncher.FindLoaderScript(
            "/games/wow/WoW.exe", "/games/wow", fileExists: _ => false);
        Assert.Null(script);
    }

    [Fact]
    public async Task Launch_RunsTheLoader_WhenPresent()
    {
        var inner = new RecordingLauncher();
        string? ran = null;
        var launcher = new LoaderScriptLauncher(inner, Serilog.Log.Logger,
            runScript: (s, _) => { ran = s; return GameLaunchResult.Ok(4242); });

        // A folder that "has" a launch.sh — but the launcher uses File.Exists internally, so point it at a
        // real temp dir with a launch.sh in it.
        using var t = new TempClient(withLoader: true);
        var result = await launcher.LaunchAsync(Path.Combine(t.Dir, "WoW.exe"), t.Dir);

        Assert.True(result.Started);
        Assert.Equal(4242, result.ProcessId);
        Assert.Equal(Path.Combine(t.Dir, "launch.sh"), ran); // the loader ran…
        Assert.False(inner.WasCalled);                        // …and the bare wine start did NOT
    }

    [Fact]
    public async Task Launch_DelegatesToInner_WhenNoLoader_AndNoRealmIsWired()
    {
        // Standalone case only (no realm resolver): the STARTER falls back to the plain wine start.
        // What this test used to also cover — a wired realm taking the same fallback untouched — moved
        // to the three tests below, because it was the fail-open Codex found (2026-08-09, finding 2),
        // the Linux twin of the Windows one closed in 891b94f.
        var inner = new RecordingLauncher();
        var launcher = new LoaderScriptLauncher(inner, Serilog.Log.Logger,
            runScript: (_, _) => GameLaunchResult.Ok(1)); // must not be used

        using var t = new TempClient(withLoader: false);
        var result = await launcher.LaunchAsync(Path.Combine(t.Dir, "WoW.exe"), t.Dir);

        Assert.True(inner.WasCalled);                 // fell back to the plain wine start
        Assert.Equal(t.Dir, inner.LastWorkingDir);
        Assert.True(result.Started);
    }

    // --- No launch.sh is not a licence to skip the realm binding ----------------------------------
    // Codex review 2026-08-09, finding 2. The "no loader script" branch used to return into the inner
    // launcher immediately — before RealmAddress.Parse, before WriteClientRealm. A player using the
    // supported "Locate installed WoW" on a 1.12.1 install without a script therefore started on
    // whatever realm the files carried, while the launcher showed another one. ConfigureClient does not
    // cover this: it logs-and-returns on an invalid address and swallows write failures.
    //
    // The expectation these three encode is deliberately the OPPOSITE of what the old
    // Launch_DelegatesToInner_WhenNoLoader asserted for a wired realm: fail-closed, not fail-open.

    [Fact]
    public async Task Launch_WithoutALoader_StillWritesTheRealm_BeforeTheWineStart()
    {
        var inner = new RecordingLauncher();
        var launcher = new LoaderScriptLauncher(
            inner, Serilog.Log.Logger,
            realmAddress: () => "play.example.invalid:3725",
            runScript: (_, _) => GameLaunchResult.Ok(1)); // no script, so this must not be used

        using var t = new TempClient(withLoader: false);
        Directory.CreateDirectory(Path.Combine(t.Dir, "WTF"));
        File.WriteAllText(Path.Combine(t.Dir, "WTF", "Config.wtf"),
            "SET realmList \"play.stonetavern.app\"\n");

        var result = await launcher.LaunchAsync(Path.Combine(t.Dir, "WoW.exe"), t.Dir);

        Assert.True(result.Started);
        Assert.True(inner.WasCalled);   // still the plain wine start…
        var config = File.ReadAllText(Path.Combine(t.Dir, "WTF", "Config.wtf"));
        Assert.Contains("SET realmList \"play.example.invalid:3725\"", config, System.StringComparison.Ordinal);
        Assert.DoesNotContain("stonetavern", config, System.StringComparison.OrdinalIgnoreCase);
        Assert.Equal("set realmlist play.example.invalid:3725\n",
            File.ReadAllText(Path.Combine(t.Dir, "realmlist.wtf")));  // …but pointed at the right realm
    }

    [Fact]
    public async Task Launch_WithoutALoader_RefusesAnInvalidRealm_InsteadOfStartingWine()
    {
        var inner = new RecordingLauncher();
        var launcher = new LoaderScriptLauncher(
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
        var launcher = new LoaderScriptLauncher(
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
            Dir = Path.Combine(Path.GetTempPath(), "mechagon-loader-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, "WoW.exe"), "stub");
            if (withLoader) File.WriteAllText(Path.Combine(Dir, "launch.sh"), "#!/usr/bin/env bash\n");
        }
        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* temp */ }
        }
    }
}
