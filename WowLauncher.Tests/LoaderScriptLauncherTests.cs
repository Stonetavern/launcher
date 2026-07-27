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
            runScript: s => { ran = s; return GameLaunchResult.Ok(4242); });

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
    public async Task Launch_DelegatesToInner_WhenNoLoader()
    {
        var inner = new RecordingLauncher();
        var launcher = new LoaderScriptLauncher(inner, Serilog.Log.Logger,
            runScript: _ => GameLaunchResult.Ok(1)); // must not be used

        using var t = new TempClient(withLoader: false);
        var result = await launcher.LaunchAsync(Path.Combine(t.Dir, "WoW.exe"), t.Dir);

        Assert.True(inner.WasCalled);                 // fell back to the plain wine start
        Assert.Equal(t.Dir, inner.LastWorkingDir);
        Assert.True(result.Started);
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
