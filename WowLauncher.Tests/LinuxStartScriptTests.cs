using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// 2026-09-22: the 1.12.1 package ships START.sh, which brings its own pinned GE-Proton. These prove
/// the launcher prefers it, asks IT whether the machine is ready (not the system Wine), runs its
/// --prepare step with visible progress, and hands its reasons to the player unchanged.
/// </summary>
public sealed class LinuxStartScriptTests
{
    private static readonly Serilog.ILogger Log = Serilog.Log.Logger;

    [Fact]
    public void FindLoaderScript_PrefersStartSh_OverLaunchSh()
    {
        var script = LoaderScriptLauncher.FindLoaderScript("/g/WoW.exe", "/g",
            fileExists: p => p == Path.Combine("/g", "START.sh") || p == Path.Combine("/g", "launch.sh"));
        Assert.Equal(Path.Combine("/g", "START.sh"), script);
    }

    [Fact]
    public void FindLoaderScript_FallsBackToLaunchSh_WithoutStartSh()
    {
        var script = LoaderScriptLauncher.FindLoaderScript("/g/WoW.exe", "/g",
            fileExists: p => p == Path.Combine("/g", "launch.sh"));
        Assert.Equal(Path.Combine("/g", "launch.sh"), script);
    }

    [Theory]
    [InlineData("STPROGRESS proton 90 Unpacking GE-Proton", "proton", 90, "Unpacking GE-Proton")]
    [InlineData("STPROGRESS runtime 0 Downloading the Valve runtime (one time, about 660 MB)", "runtime", 0,
        "Downloading the Valve runtime (one time, about 660 MB)")]
    [InlineData("STPROGRESS start 250 x", "start", 100, "x")]
    public void ParseProgress_ReadsTheScriptLines(string line, string step, int pct, string text)
    {
        var p = LinuxStartScript.ParseProgress(line);
        Assert.NotNull(p);
        Assert.Equal((step, pct, text), p!.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Downloading GE-Proton (one time)...")]
    [InlineData("STPROGRESS proton notanumber text")]
    [InlineData("XSTPROGRESS proton 1 x")]
    public void ParseProgress_IgnoresEverythingElse(string? line) => Assert.Null(LinuxStartScript.ParseProgress(line));

    [Fact]
    public void PlayerMessage_KeepsTheReasonLines_InOrder()
    {
        var msg = LinuxStartScript.PlayerMessage(new[]
        {
            "some noise", "x Python 3.10 or newer is missing.", "x Vulkan is missing.", "",
            "Stonetavern: This machine is missing something the game needs (see above).",
        });
        Assert.Equal("x Python 3.10 or newer is missing.\nx Vulkan is missing.\n" +
                     "Stonetavern: This machine is missing something the game needs (see above).", msg);
    }

    [Fact]
    public void PlayerMessage_NeverEmpty()
    {
        Assert.Equal("b\nc\nd", LinuxStartScript.PlayerMessage(new[] { "a", "b", "", "c", "d" }));
        Assert.False(string.IsNullOrWhiteSpace(LinuxStartScript.PlayerMessage(Array.Empty<string>())));
    }

    [Fact]
    public void Provisioner_NeedsRuntime_OnlyForTheLegacyClient_WithStartSh()
    {
        var withScript = new LinuxStartScriptProvisioner(Log, p => p.EndsWith("START.sh", StringComparison.Ordinal), null);
        var without = new LinuxStartScriptProvisioner(Log, _ => false, null);

        Assert.True(withScript.NeedsRuntimeForExe("/g/WoW.exe"));
        // Modern client: its own proxy/Arctium path, never START.sh — even if one happens to lie there.
        Assert.False(withScript.NeedsRuntimeForExe("/g/World of Warcraft/_classic_era_/WowClassic.exe"));
        Assert.False(without.NeedsRuntimeForExe("/g/WoW.exe"));
        // The name-only question cannot find a script, and must not pretend it did.
        Assert.False(withScript.NeedsRuntime("WoW.exe"));
    }

    [Fact]
    public async Task Provisioner_RunsPrepare_AndReportsSteps()
    {
        string? arg = null;
        var steps = new List<string>();
        var prov = new LinuxStartScriptProvisioner(Log, _ => true, (s, a, onOut, ct) =>
        {
            arg = a;
            onOut?.Invoke("STPROGRESS proton 0 Downloading GE-Proton (one time)");
            onOut?.Invoke("noise");
            onOut?.Invoke("STPROGRESS proton 100 GE-Proton ready");
            return Task.FromResult((0, (IReadOnlyList<string>)Array.Empty<string>()));
        });
        var ready = await prov.EnsureForExeAsync("/g/WoW.exe", step: new SyncProgress(steps));
        Assert.True(ready.Ok);
        Assert.Equal("--prepare", arg);
        Assert.Equal(new[] { "Downloading GE-Proton (one time)", "GE-Proton ready" }, steps);
    }

    [Fact]
    public async Task Provisioner_Failure_CarriesTheScriptReason()
    {
        var prov = new LinuxStartScriptProvisioner(Log, _ => true, (s, a, onOut, ct) =>
            Task.FromResult((1, (IReadOnlyList<string>)new[] { "x Vulkan is missing.", "Stonetavern: stop." })));
        var ready = await prov.EnsureForExeAsync("/g/WoW.exe");
        Assert.False(ready.Ok);
        Assert.Equal("x Vulkan is missing.\nStonetavern: stop.", ready.Error);
    }

    [Fact]
    public async Task CheckReady_AsksStartSh_NotTheSystemWine()
    {
        if (!OperatingSystem.IsLinux()) return; // runs a real bash script
        using var t = new TempDir();
        File.WriteAllText(Path.Combine(t.Path, "START.sh"),
            "#!/usr/bin/env bash\n[ \"$1\" = --check ] || exit 9\necho 'x Vulkan is missing.' >&2\nexit 1\n");
        var inner = new InnerSaysReady();
        var launcher = new LoaderScriptLauncher(inner, Log, legacyClientDir: () => t.Path);

        var problem = await launcher.CheckReadyAsync("WoW.exe");

        Assert.Equal("x Vulkan is missing.", problem);
        Assert.False(inner.Asked); // the system-Wine probe was not the judge
    }

    [Fact]
    public async Task CheckReady_WithoutStartSh_StillAsksTheInnerLauncher()
    {
        using var t = new TempDir();
        var inner = new InnerSaysReady();
        var launcher = new LoaderScriptLauncher(inner, Log, legacyClientDir: () => t.Path);
        Assert.Null(await launcher.CheckReadyAsync("WoW.exe"));
        Assert.True(inner.Asked);
    }

    [Fact]
    public async Task CheckReady_ScriptSaysOk_IsReady()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var t = new TempDir();
        File.WriteAllText(Path.Combine(t.Path, "START.sh"), "#!/usr/bin/env bash\n[ \"$1\" = --check ] && exit 0\nexit 9\n");
        var launcher = new LoaderScriptLauncher(new InnerSaysReady(), Log, legacyClientDir: () => t.Path);
        Assert.Null(await launcher.CheckReadyAsync("WoW.exe"));
    }

    private sealed class SyncProgress(List<string> sink) : IProgress<string>
    {
        public void Report(string value) => sink.Add(value);
    }

    private sealed class InnerSaysReady : IGameLauncher
    {
        public bool Asked { get; private set; }
        public Task<GameLaunchResult> LaunchAsync(string exePath, string workingDirectory) =>
            Task.FromResult(GameLaunchResult.Ok(1));
        public Task<string?> CheckReadyAsync(string exeName) { Asked = true; return Task.FromResult<string?>(null); }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "st-start-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { /* temp */ } }
    }
}
