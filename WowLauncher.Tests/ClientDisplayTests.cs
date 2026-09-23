using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The display rule the tickets asked for (#11, #63, #92, #96, #98): the player's choice survives a
/// restart, a mode the monitor cannot show is healed instead of started into a black screen, and the
/// launcher never invents a refresh rate.
/// </summary>
public class ClientDisplayPolicyTests
{
    private static readonly DisplayMode Native = new(2560, 1440, 144);

    private static DisplayProbeResult Monitor(params DisplayMode[] extra) =>
        new(Native, new[]
        {
            Native, new DisplayMode(2560, 1440, 60), new DisplayMode(1920, 1080, 144),
            new DisplayMode(1920, 1080, 60), new DisplayMode(1280, 720, 60),
        }.Concat(extra).ToList());

    [Fact]
    public void NothingStored_GetsTheMonitorsMode()
    {
        var d = ClientDisplayPolicy.Decide(new StoredDisplay(null, null, Windowed: true), Monitor(), stamp: null);
        Assert.Equal("2560x1440", d.Resolution);
        Assert.Equal("144", d.Refresh);
    }

    [Fact]
    public void ThePlayersOwnResolution_SurvivesTheNextStart()   // ticket #96
    {
        // The launcher wrote 2560x1440@144 last time; the player then picked 1920x1080 in-game.
        var stamp = new DisplayStamp("2560x1440", "144");
        var d = ClientDisplayPolicy.Decide(new StoredDisplay("1920x1080", "144", Windowed: true), Monitor(), stamp);
        Assert.False(d.WritesAnything);
    }

    [Fact]
    public void ThePlayersRefreshRate_IsNotPulledBackToSixty()   // ticket #11
    {
        // We wrote 60 (the only thing WMI said); the player set 144 in-game. Stamp says 60, config says 144:
        // that is the player's, and the monitor confirms it exists.
        var stamp = new DisplayStamp("2560x1440", "60");
        var d = ClientDisplayPolicy.Decide(new StoredDisplay("2560x1440", "144", Windowed: false), Monitor(), stamp);
        Assert.False(d.WritesAnything);
    }

    [Fact]
    public void ALauncherWrittenValue_FollowsAMonitorChange()
    {
        var stamp = new DisplayStamp("1920x1080", "60");
        var d = ClientDisplayPolicy.Decide(new StoredDisplay("1920x1080", "60", Windowed: true), Monitor(), stamp);
        Assert.Equal("2560x1440", d.Resolution);
        Assert.Equal("144", d.Refresh);
    }

    [Fact]
    public void AnUnknownRateStamp_VouchesForNoRate_ThePlayersRateStays()   // review 2026-09-05, finding 1
    {
        // First run: the monitor gave no rate, so the stamp is "2560x1440@?". The player then set 75 Hz.
        // Next run the driver reports 144: the size is ours and unchanged, the rate is the player's.
        var stamp = DisplayStamp.Parse("2560x1440@?");
        var d = ClientDisplayPolicy.Decide(new StoredDisplay("2560x1440", "75", Windowed: false), Monitor(new DisplayMode(2560, 1440, 75)), stamp);
        Assert.False(d.WritesAnything);
    }

    [Fact]
    public void ALauncherWrittenRate_FollowsTheMonitor_WhenTheSizeIsUnchanged()
    {
        var stamp = new DisplayStamp("2560x1440", "60");
        var d = ClientDisplayPolicy.Decide(new StoredDisplay("2560x1440", "60", Windowed: true), Monitor(), stamp);
        Assert.Null(d.Resolution);
        Assert.Equal("144", d.Refresh);
    }

    [Fact]
    public void FullscreenOnASecondaryMonitorsMode_IsNotHealedAway()   // review 2026-09-05, finding 2
    {
        // Primary is 2560x1440; the player plays fullscreen on a 3840x2160 secondary screen.
        var d = ClientDisplayPolicy.Decide(new StoredDisplay("3840x2160", "60", Windowed: false), Monitor(new DisplayMode(3840, 2160, 60)), stamp: null);
        Assert.False(d.WritesAnything);
    }

    [Fact]
    public void ALauncherWrittenValue_OnTheSameMonitor_IsLeftAlone()
    {
        var stamp = DisplayStamp.For(Native);
        var d = ClientDisplayPolicy.Decide(new StoredDisplay("2560x1440", "144", Windowed: true), Monitor(), stamp);
        Assert.False(d.WritesAnything);
    }

    [Fact]
    public void FullscreenOnAModeTheMonitorDoesNotHave_IsHealed()   // tickets #63/#92/#98, the black screen
    {
        // 1707x960 is what a DPI-scaled PowerShell read off a 2560x1440 panel at 150 %. No monitor has it.
        var d = ClientDisplayPolicy.Decide(new StoredDisplay("1707x960", "60", Windowed: false), Monitor(), stamp: null);
        Assert.Equal("2560x1440", d.Resolution);
        Assert.Equal("144", d.Refresh);
        Assert.Contains("black screen", d.Reason);
    }

    [Fact]
    public void FullscreenOnARealMode_AtARateItDoesNotHave_GetsTheRateFixedOnly()
    {
        var d = ClientDisplayPolicy.Decide(new StoredDisplay("1920x1080", "1", Windowed: false), Monitor(), stamp: null);
        Assert.Null(d.Resolution);
        Assert.Equal("144", d.Refresh);
    }

    [Fact]
    public void FullscreenOnARealMode_IsKept()
    {
        var d = ClientDisplayPolicy.Decide(new StoredDisplay("1920x1080", "60", Windowed: false), Monitor(), stamp: null);
        Assert.False(d.WritesAnything);
    }

    [Fact]
    public void Fullscreen_WithoutAModeList_IsNotJudged()
    {
        var probe = new DisplayProbeResult(Native, Array.Empty<DisplayMode>());
        var d = ClientDisplayPolicy.Decide(new StoredDisplay("1707x960", "60", Windowed: false), probe, stamp: null);
        Assert.False(d.WritesAnything);
    }

    [Fact]
    public void AWindowLargerThanEveryMonitor_IsShrunk_AndTheRateIsLeft()   // the crop the scripts were built against
    {
        var d = ClientDisplayPolicy.Decide(new StoredDisplay("5120x2880", "60", Windowed: true), Monitor(), stamp: null);
        Assert.Equal("2560x1440", d.Resolution);
        Assert.Null(d.Refresh);
    }

    [Fact]
    public void AWindowThatFitsASecondaryMonitor_IsKept()
    {
        var d = ClientDisplayPolicy.Decide(new StoredDisplay("3840x2160", "60", Windowed: true), Monitor(new DisplayMode(3840, 2160, 60)), stamp: null);
        Assert.False(d.WritesAnything);
    }

    [Fact]
    public void AWindowSmallerThanTheMonitor_IsThePlayersBusiness()
    {
        var d = ClientDisplayPolicy.Decide(new StoredDisplay("1280x720", "60", Windowed: true), Monitor(), stamp: null);
        Assert.False(d.WritesAnything);
    }

    [Fact]
    public void AnUnknownRefreshRate_IsNeverWritten()
    {
        var probe = new DisplayProbeResult(new DisplayMode(1024, 768, 1), new[] { new DisplayMode(1024, 768, 1) });
        var d = ClientDisplayPolicy.Decide(new StoredDisplay(null, null, Windowed: true), probe, stamp: null);
        Assert.Equal("1024x768", d.Resolution);
        Assert.Null(d.Refresh);
    }

    [Fact]
    public void GarbageInTheConfig_IsReplaced()
    {
        var d = ClientDisplayPolicy.Decide(new StoredDisplay("wide", "60", Windowed: true), Monitor(), stamp: null);
        Assert.Equal("2560x1440", d.Resolution);
    }

    [Theory]
    [InlineData("2560x1440@144", "2560x1440", "144")]
    [InlineData("2560x1440@?", "2560x1440", null)]
    [InlineData("2560x1440", "2560x1440", null)]
    public void TheStamp_RoundTrips(string text, string res, string? hz)
    {
        var s = DisplayStamp.Parse(text);
        Assert.NotNull(s);
        Assert.Equal(res, s!.Resolution);
        Assert.Equal(hz, s.Refresh);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("@60")]
    public void ABrokenStamp_IsNoStamp(string text) => Assert.Null(DisplayStamp.Parse(text));
}

public class ClientDisplayServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "st-display-" + Guid.NewGuid().ToString("N"));
    private string Wtf => Path.Combine(_dir, "WTF");
    private string Config => Path.Combine(Wtf, "Config.wtf");

    public ClientDisplayServiceTests() => Directory.CreateDirectory(Wtf);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ } }

    private sealed class FixedProbe(DisplayProbeResult? result) : IDisplayProbe
    {
        public DisplayProbeResult? Probe() => result;
    }

    private static DisplayProbeResult Monitor2560() => new(new DisplayMode(2560, 1440, 144),
        new[] { new DisplayMode(2560, 1440, 144), new DisplayMode(1920, 1080, 60) });

    [Fact]
    public void FirstRun_WritesTheMonitor_AndStampsIt_AndKeepsEveryOtherLine()
    {
        File.WriteAllText(Config, "SET locale \"enUS\"\nSET gxWindow \"1\"\nSET gxMaximize \"1\"\nSET MasterVolume \"0.3\"\nSET mouseSpeed \"1.4\"\n");
        var svc = new ClientDisplayService(new FixedProbe(Monitor2560()), Serilog.Log.Logger);

        var r = svc.Apply(_dir);

        Assert.True(r.Handled);
        Assert.Equal("2560x1440", WtfFile.ReadVar(Config, "gxResolution"));
        Assert.Equal("144", WtfFile.ReadVar(Config, "gxRefresh"));
        Assert.Equal("0.3", WtfFile.ReadVar(Config, "MasterVolume"));
        Assert.Equal("1.4", WtfFile.ReadVar(Config, "mouseSpeed"));
        Assert.Equal("2560x1440@144", File.ReadAllText(Path.Combine(Wtf, DisplayStamp.FileName)).Trim());
    }

    [Fact]
    public void SecondRun_AfterThePlayerChangedIt_LeavesTheirChoice()
    {
        File.WriteAllText(Config, "SET gxWindow \"1\"\n");
        var svc = new ClientDisplayService(new FixedProbe(Monitor2560()), Serilog.Log.Logger);
        svc.Apply(_dir);
        // The player picks 1920x1080 in the video options; the client writes it on exit.
        WtfFile.SetVar(Config, "gxResolution", "1920x1080");
        WtfFile.SetVar(Config, "gxRefresh", "60");

        var r = svc.Apply(_dir);

        Assert.True(r.Handled);
        Assert.Equal("1920x1080", WtfFile.ReadVar(Config, "gxResolution"));
        Assert.Equal("60", WtfFile.ReadVar(Config, "gxRefresh"));
    }

    [Fact]
    public void TheManualMarker_IsHonoured_ButStillHandled()
    {
        File.WriteAllText(Config, "SET gxResolution \"800x600\"\n");
        File.WriteAllText(Path.Combine(Wtf, ClientDisplayService.ManualMarker), "");
        var svc = new ClientDisplayService(new FixedProbe(Monitor2560()), Serilog.Log.Logger);

        var r = svc.Apply(_dir);

        Assert.True(r.Handled);
        Assert.Equal("800x600", WtfFile.ReadVar(Config, "gxResolution"));
    }

    [Fact]
    public void WhenTheMonitorCannotBeRead_NothingIsTouched_AndTheScriptKeepsItsJob()
    {
        File.WriteAllText(Config, "SET gxResolution \"800x600\"\n");
        var svc = new ClientDisplayService(new FixedProbe(null), Serilog.Log.Logger);

        var r = svc.Apply(_dir);

        Assert.False(r.Handled);
        Assert.Equal("800x600", WtfFile.ReadVar(Config, "gxResolution"));
        Assert.False(File.Exists(Path.Combine(Wtf, DisplayStamp.FileName)));
    }

    [Fact]
    public void NoProbe_MeansUnsupported_AndNothingHappens()
    {
        var svc = new ClientDisplayService(probe: null, Serilog.Log.Logger);
        Assert.False(svc.IsSupported);
        Assert.False(svc.Apply(_dir).Handled);
        Assert.False(svc.ResetVideo(_dir, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void ResetVideo_PutsTheClientIntoAWorkingWindow_AndKeepsTheRest()
    {
        // The black-screen state: fullscreen on a mode that does not exist, plus the player's other settings.
        File.WriteAllText(Config, "SET gxWindow \"0\"\nSET gxMaximize \"0\"\nSET gxResolution \"1707x960\"\nSET gxRefresh \"1\"\nSET MasterVolume \"0.3\"\n");
        File.WriteAllText(Path.Combine(Wtf, ClientDisplayService.ManualMarker), "");
        var svc = new ClientDisplayService(new FixedProbe(Monitor2560()), Serilog.Log.Logger);

        Assert.True(svc.ResetVideo(_dir, out var error), error);

        Assert.Equal("1", WtfFile.ReadVar(Config, "gxWindow"));
        Assert.Equal("1", WtfFile.ReadVar(Config, "gxMaximize"));
        Assert.Equal("2560x1440", WtfFile.ReadVar(Config, "gxResolution"));
        Assert.Equal("144", WtfFile.ReadVar(Config, "gxRefresh"));
        Assert.Equal("0.3", WtfFile.ReadVar(Config, "MasterVolume"));
        Assert.False(File.Exists(Path.Combine(Wtf, ClientDisplayService.ManualMarker)));
    }

    [Fact]
    public void TheBlackScreen_IsHealedOnTheNextLaunch()
    {
        File.WriteAllText(Config, "SET gxWindow \"0\"\nSET gxResolution \"1707x960\"\nSET gxRefresh \"60\"\n");
        var svc = new ClientDisplayService(new FixedProbe(Monitor2560()), Serilog.Log.Logger);

        var r = svc.Apply(_dir);

        Assert.True(r.Handled);
        Assert.Equal("2560x1440", WtfFile.ReadVar(Config, "gxResolution"));
        Assert.Contains("black screen", r.Decision!.Reason);
    }
}

public class LinuxDisplayProbeParsingTests
{
    private const string Xrandr = """
        Screen 0: minimum 16 x 16, current 9729 x 1542, maximum 32767 x 32767
        HDMI-A-2 connected 2304x1296+7425+144 (normal left inverted right x axis y axis) 531mm x 298mm
           2304x1296     59.92*+
           1600x1200     59.87
           1024x768      59.92
        DP-1 connected primary 2560x1440+0+0 (normal left inverted right x axis y axis) 597mm x 336mm
           2560x1440     359.80*+ 240.00  144.00   60.00
           1920x1080     144.00   60.00
           1280x720      60.00
        DP-2 disconnected (normal left inverted right x axis y axis)
        """;

    [Fact]
    public void Xrandr_TakesThePrimaryOutput_ItsCurrentMode_AndRoundsTheRate()
    {
        var r = LinuxDisplayProbe.ParseXrandr(Xrandr);
        Assert.NotNull(r);
        Assert.Equal(new DisplayMode(2560, 1440, 360), r!.Current);    // 359.80 is a 360 Hz mode
        Assert.Contains(new DisplayMode(1920, 1080, 144), r.Modes);
        Assert.Contains(new DisplayMode(2304, 1296, 60), r.Modes);   // the other monitor's modes count too
    }

    [Fact]
    public void Xrandr_WithoutAPrimary_TakesTheFirstConnected()
    {
        var text = Xrandr.Replace(" connected primary ", " connected ");
        var r = LinuxDisplayProbe.ParseXrandr(text);
        Assert.Equal(new DisplayMode(2304, 1296, 60), r!.Current);
    }

    [Fact]
    public void Xrandr_WithNothingConnected_IsNoAnswer()
    {
        Assert.Null(LinuxDisplayProbe.ParseXrandr("Screen 0: minimum 16 x 16\nDP-1 disconnected\n"));
    }

    [Fact]
    public void Kscreen_TakesThePrimaryOutputsCurrentMode()
    {
        const string json = """
            {"outputs":[
              {"id":1,"enabled":true,"primary":false,"currentModeId":"a","modes":[{"id":"a","size":{"width":1920,"height":1080},"refreshRate":60.0}]},
              {"id":2,"enabled":true,"primary":true,"currentModeId":"m2","modes":[
                 {"id":"m1","size":{"width":2560,"height":1440},"refreshRate":359.8},
                 {"id":"m2","size":{"width":2560,"height":1440},"refreshRate":144.0},
                 {"id":"m3","size":{"width":1920,"height":1080},"refreshRate":60.0}]}
            ]}
            """;
        var r = LinuxDisplayProbe.ParseKscreen(json);
        Assert.NotNull(r);
        Assert.Equal(new DisplayMode(2560, 1440, 144), r!.Current);
        Assert.Contains(new DisplayMode(2560, 1440, 360), r.Modes);
        Assert.Equal(3, r.Modes.Count);   // 1920x1080@60 sits on both outputs and is listed once
    }

    [Fact]
    public void Kscreen_Garbage_IsNoAnswer() => Assert.Null(LinuxDisplayProbe.ParseKscreen("not json"));

    [Fact]
    public void TheProbe_FallsThrough_XrandrThenKscreen()
    {
        var calls = new List<string>();
        var probe = new LinuxDisplayProbe((cmd, _) => { calls.Add(cmd); return cmd == "kscreen-doctor" ? """{"outputs":[{"enabled":true,"primary":true,"currentModeId":"m","modes":[{"id":"m","size":{"width":1280,"height":800},"refreshRate":60}]}]}""" : null; });
        var r = probe.Probe();
        Assert.Equal(new[] { "xrandr", "kscreen-doctor" }, calls);
        Assert.Equal(new DisplayMode(1280, 800, 60), r!.Current);
    }
}

/// <summary>The hand-off to the packaged scripts: once the launcher judged the display, the script
/// is told to keep its hands off — and only then.</summary>
public class LoaderScriptDisplayHandoffTests
{
    private sealed class StubDisplay(bool handled) : IClientDisplayService
    {
        public bool IsSupported => true;
        public List<string> AppliedTo { get; } = new();
        public DisplayApplyResult Apply(string clientDirectory)
        {
            AppliedTo.Add(clientDirectory);
            return handled ? new DisplayApplyResult(true, DisplayDecision.Keep("test"), null) : DisplayApplyResult.NotHandled("test");
        }
        public bool ResetVideo(string clientDirectory, out string? error) { error = null; return true; }
    }

    private sealed class NeverLauncher : IGameLauncher
    {
        public Task<string?> CheckReadyAsync(string exeName) => Task.FromResult<string?>(null);
        public Task<GameLaunchResult> LaunchAsync(string exePath, string workingDirectory) =>
            Task.FromResult(GameLaunchResult.Ok(1));
    }

    private static string TempClient(string loader)
    {
        var dir = Path.Combine(Path.GetTempPath(), "st-handoff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, loader), "");
        File.WriteAllText(Path.Combine(dir, "WoW.exe"), "");
        return dir;
    }

    [Fact]
    public async Task Linux_WhenTheLauncherHandledTheDisplay_TheScriptIsToldToKeepIt()
    {
        var dir = TempClient("launch.sh");
        IReadOnlyDictionary<string, string>? env = null;
        var display = new StubDisplay(handled: true);
        var launcher = new LoaderScriptLauncher(new NeverLauncher(), Serilog.Log.Logger,
            realmAddress: () => "play.example.test",
            runScript: (_, e) => { env = e; return GameLaunchResult.Ok(7); },
            display: display);

        var r = await launcher.LaunchAsync(Path.Combine(dir, "WoW.exe"), dir);

        Assert.True(r.Started);
        Assert.Equal(dir, display.AppliedTo.Single());
        Assert.Equal("1", env![IClientDisplayService.KeepResolutionEnvVar]);
        Assert.Equal("play.example.test", env[RealmBinding.RealmlistEnvVar]);   // the realm hand-off is untouched
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Linux_WhenTheMonitorCouldNotBeRead_TheScriptDetectsAsBefore()
    {
        var dir = TempClient("launch.sh");
        IReadOnlyDictionary<string, string>? env = null;
        var launcher = new LoaderScriptLauncher(new NeverLauncher(), Serilog.Log.Logger,
            realmAddress: () => "play.example.test",
            runScript: (_, e) => { env = e; return GameLaunchResult.Ok(7); },
            display: new StubDisplay(handled: false));

        await launcher.LaunchAsync(Path.Combine(dir, "WoW.exe"), dir);

        Assert.False(env!.ContainsKey(IClientDisplayService.KeepResolutionEnvVar));
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Windows_WhenTheLauncherHandledTheDisplay_TheBatchIsToldToKeepIt()
    {
        var dir = TempClient("launch.bat");
        IReadOnlyDictionary<string, string>? env = null;
        var display = new StubDisplay(handled: true);
        var launcher = new WindowsLoaderScriptLauncher(new NeverLauncher(), Serilog.Log.Logger,
            realmAddress: () => "play.example.test",
            runScript: (_, e) => { env = e; return GameLaunchResult.Ok(7); },
            display: display);

        var r = await launcher.LaunchAsync(Path.Combine(dir, "WoW.exe"), dir);

        Assert.True(r.Started);
        Assert.Equal(dir, display.AppliedTo.Single());
        Assert.Equal("1", env![IClientDisplayService.KeepResolutionEnvVar]);
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task TheModernClient_IsNeverJudged()
    {
        var dir = TempClient("launch.sh");
        File.WriteAllText(Path.Combine(dir, "WowClassic.exe"), "");
        var display = new StubDisplay(handled: true);
        var launcher = new LoaderScriptLauncher(new NeverLauncher(), Serilog.Log.Logger,
            realmAddress: () => "play.example.test",
            runScript: (_, _) => GameLaunchResult.Ok(7),
            display: display);

        await launcher.LaunchAsync(Path.Combine(dir, "WowClassic.exe"), dir);

        Assert.Empty(display.AppliedTo);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void TheVcRuntimeCheck_LooksNextToTheClientToo()
    {
        var present = HardwareFacts.VcRuntimeX86Present("/client",
            p => p.Replace('\\', '/').EndsWith("/client/vcruntime140.dll") || p.Replace('\\', '/').EndsWith("/client/msvcp140.dll"));
        // On Linux the check is a no-op (true); on Windows the app-local copy counts.
        Assert.True(present);
    }
}
