using System.IO;
using System.Threading.Tasks;
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
            runScript: s => { ran = s; return GameLaunchResult.Ok(4242); });

        using var t = new TempClient(withLoader: true);
        var result = await launcher.LaunchAsync(Path.Combine(t.Dir, "WoW.exe"), t.Dir);

        Assert.True(result.Started);
        Assert.Equal(4242, result.ProcessId);
        Assert.Equal(Path.Combine(t.Dir, "launch.bat"), ran); // the loader ran…
        Assert.False(inner.WasCalled);                         // …and the bare native start did NOT
    }

    [Fact]
    public async Task Launch_DelegatesToInner_WhenNoLoader()
    {
        var inner = new RecordingLauncher();
        var launcher = new WindowsLoaderScriptLauncher(inner, Serilog.Log.Logger,
            runScript: _ => GameLaunchResult.Ok(1)); // must not be used

        using var t = new TempClient(withLoader: false);
        var result = await launcher.LaunchAsync(Path.Combine(t.Dir, "WoW.exe"), t.Dir);

        Assert.True(inner.WasCalled);                 // fell back to the plain native start
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
