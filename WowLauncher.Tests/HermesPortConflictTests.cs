using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// 2026-09-27, Linux: the 1.14.2 client hung on "Connecting". HermesProxy bound 127.0.0.1:1119, then
/// died on 8081 (a local llama-server held it: "SocketException (98): Address already in use",
/// "Failed to start BnetRestApiSession service"), printed "Press enter to close" and waited on stdin
/// while still holding 1119. The launcher only probed 1119 and reported the proxy ready.
///
/// These tests run a FAKE proxy (a small python3 script) that does exactly what the real one does in
/// that order: bind the BNet port, then the REST/realm/instance ports from its config and any
/// <c>--set Key=Value</c>, and on a failed bind print the proxy's own words and block on stdin while
/// its sockets stay open. Real sockets, real process, real stdin — no probe is faked. None of the
/// ports used are the real 1119/8081 (a llama-server of another session lives on 8081 here).
/// </summary>
public sealed class HermesPortConflictTests
{
    private static Serilog.ILogger Log() => new Serilog.LoggerConfiguration().CreateLogger();

    private const string FakeProxy = """
import os, socket, sys, time
args = sys.argv[1:]
cfg = {}
for line in open("HermesProxy.config"):
    if 'key="' in line:
        k = line.split('key="')[1].split('"')[0]; v = line.split('value="')[1].split('"')[0]; cfg[k] = v
i = 0
while i < len(args):
    if args[i] == "--set":
        k, v = args[i + 1].split("=", 1); cfg[k] = v; i += 2
    else:
        i += 1
open(os.environ["FAKE_ARGS_OUT"], "w").write(" ".join(sys.argv[1:]))
import stat as _st
_m = os.fstat(0).st_mode
open(os.environ["FAKE_ARGS_OUT"] + ".stdin", "w").write(
    "fifo" if _st.S_ISFIFO(_m) else "chr" if _st.S_ISCHR(_m) else "other")
held = []
def listen(port):
    s = socket.socket(); s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    s.bind(("127.0.0.1", int(port))); s.listen(8); held.append(s)
listen(cfg["BNetPort"])
print("Server | BnetTcpSession | started", flush=True)
for key, name in (("RestPort", "BnetRestApiSession"), ("RealmPort", "RealmSocket"), ("InstancePort", "WorldSocket")):
    try:
        if os.environ.get("FAKE_FAIL") == key: raise OSError(98, "Address already in use")
        listen(cfg[key])
    except OSError as e:
        print("Network | SocketManager | StartNetwork failed to Start AsyncAcceptor", flush=True)
        print("Unhandled exception: System.Exception: Failed to start %s service" % name, file=sys.stderr, flush=True)
        print("Press enter to close", flush=True)
        try:
            input()
        except EOFError:
            pass
        # the real proxy may linger a moment after the prompt; the launcher must not rely on its exit
        time.sleep(float(os.environ.get("FAKE_LINGER", "0")))
        sys.exit(1)
print("ready", flush=True)
while True:
    time.sleep(1)
""";

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static bool Listening(int port)
    {
        try { using var c = new TcpClient(); c.Connect(IPAddress.Loopback, port); return true; }
        catch { return false; }
    }

    private sealed record Rig(string Dir, string ArgsOut, int Bnet, int Rest, int Realm, int Instance);

    private static Rig MakeRig()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"st-hermes-fake-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var rig = new Rig(dir, Path.Combine(dir, "args.txt"), FreePort(), FreePort(), FreePort(), FreePort());
        File.WriteAllText(Path.Combine(dir, "fake_hermes.py"), FakeProxy);
        File.WriteAllText(Path.Combine(dir, "HermesProxy.config"), $"""
<?xml version="1.0" encoding="utf-8"?>
<configuration><appSettings>
  <add key="RestPort" value="{rig.Rest}" />
  <add key="BNetPort" value="{rig.Bnet}" />
  <add key="RealmPort" value="{rig.Realm}" />
  <add key="InstancePort" value="{rig.Instance}" />
</appSettings></configuration>
""");
        return rig;
    }

    private static HermesProxyRunner Runner(Rig rig, bool relocate, Dictionary<string, string>? env = null)
    {
        env ??= [];
        env["FAKE_ARGS_OUT"] = rig.ArgsOut;
        return new HermesProxyRunner(Log(), "/usr/bin/python3", [Path.Combine(rig.Dir, "fake_hermes.py")],
            Path.Combine(rig.Dir, "hermes.pid"), HermesProxyRunner.TcpPortProbeAsync, env,
            workingDirectory: rig.Dir, relocateAuxiliaryPorts: relocate, stopGrace: TimeSpan.FromSeconds(2));
    }

    private static bool CanRun => OperatingSystem.IsLinux() && File.Exists("/usr/bin/python3");

    /// <summary>The 2026-09-27 case exactly: something else holds the REST port. The launcher moves it to
    /// a free port, hands it to the proxy with --set, and the proxy comes up on all four ports.</summary>
    [Fact]
    public async Task ATakenRestPort_IsMovedToAFreeOne_AndTheProxyComesUp()
    {
        if (!CanRun) return;
        var rig = MakeRig();
        var squatter = new TcpListener(IPAddress.Loopback, rig.Rest); // the "llama-server"
        squatter.Start();
        var runner = Runner(rig, relocate: true);
        try
        {
            var result = await runner.StartAndWaitForPortAsync(rig.Bnet, TimeSpan.FromSeconds(10));

            Assert.True(result.Ready, result.Error);
            var rest = runner.AuxiliaryPorts!["RestPort"];
            Assert.NotEqual(rig.Rest, rest);
            Assert.Contains($"--set RestPort={rest}", File.ReadAllText(rig.ArgsOut));
            Assert.True(Listening(rest));
            Assert.Equal(rig.Realm, runner.AuxiliaryPorts["RealmPort"]); // free ones stay where they are
        }
        finally
        {
            await runner.StopAsync();
            squatter.Stop();
        }
        Assert.False(Listening(rig.Bnet));
    }

    /// <summary>A proxy that opened the BNet port and then died on its second listener must be REPORTED
    /// and closed — never "ready", never left holding the BNet port on "Press enter to close".</summary>
    [Fact]
    public async Task AProxyThatFailsOnALaterListener_IsReported_Closed_AndFreesItsPorts()
    {
        if (!CanRun) return;
        var rig = MakeRig();
        // Linger after the prompt: the launcher must act on what the proxy SAID, not wait for its exit.
        var runner = Runner(rig, relocate: true, new() { ["FAKE_FAIL"] = "RestPort", ["FAKE_LINGER"] = "30" });

        var sw = Stopwatch.StartNew();
        var result = await runner.StartAndWaitForPortAsync(rig.Bnet, TimeSpan.FromSeconds(20));
        sw.Stop();

        Assert.False(result.Ready);
        Assert.Contains("Failed to start BnetRestApiSession service", result.Error);
        Assert.Contains("Press Play to try once more", result.Error);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed} — it waited instead of reading the proxy");
        Assert.False(Listening(rig.Bnet)); // the port the client would have hung on is free again
        Assert.False(runner.IsProcessAlive == true);
    }

    /// <summary>Without relocation (a caller that passes its own ports) a taken REST port must still end
    /// in a clean failure — the regression of 2026-09-27 in its pure form.</summary>
    [Fact]
    public async Task WithoutRelocation_ATakenRestPort_EndsInACleanFailure_NotInReady()
    {
        if (!CanRun) return;
        var rig = MakeRig();
        var squatter = new TcpListener(IPAddress.Loopback, rig.Rest);
        squatter.Start();
        var runner = Runner(rig, relocate: false);
        try
        {
            var result = await runner.StartAndWaitForPortAsync(rig.Bnet, TimeSpan.FromSeconds(10));
            // Without relocation the runner only knows the BNet port, which DID open. It may therefore
            // answer "ready" before the proxy prints its failure — but the re-check right before the
            // client starts must then refuse, because the proxy has said it is dead.
            if (result.Ready)
            {
                await Task.Delay(1500);
                Assert.False(await runner.VerifyStillListeningAsync(rig.Bnet),
                    "the proxy said it failed on its REST port, and the launcher would still start the client");
            }
        }
        finally
        {
            await runner.StopAsync();
            squatter.Stop();
        }
    }

    /// <summary>Closing stdin is what lets a failed proxy exit on its own: "Press enter to close" reads
    /// stdin, and an INHERITED stdin (the launcher's terminal or session pipe) never delivers EOF — that
    /// is how the proxy of 2026-09-27 sat there holding 1119. The runner must hand the proxy a pipe of
    /// its own and close it. Measured on the child: its fd 0 is a FIFO, and it reads EOF at once.</summary>
    [Fact]
    public async Task TheProxyGetsItsOwnClosedStdinPipe_SoItsPressEnterPromptReturnsAtOnce()
    {
        if (!CanRun) return;
        var rig = MakeRig();
        var runner = Runner(rig, relocate: false, new() { ["FAKE_FAIL"] = "RestPort" });
        _ = await runner.StartAndWaitForPortAsync(rig.Bnet, TimeSpan.FromSeconds(10));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!File.Exists(rig.ArgsOut + ".stdin") && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Equal("fifo", File.ReadAllText(rig.ArgsOut + ".stdin"));
        while (Listening(rig.Bnet) && DateTime.UtcNow < deadline) await Task.Delay(100);
        Assert.False(Listening(rig.Bnet));
        await runner.StopAsync();
    }

    // ── Against the REAL proxy (opt-in: ST_REAL_HERMES_DIR = a COPY of a Hermes/linux folder) ───────
    // Local Linux acceptance, not CI: it needs the shipped binary, which is not in this repo.
    //   ST_REAL_HERMES_DIR=/path/to/copy dotnet test --filter RealProxy

    private static string? RealHermesDir =>
        Environment.GetEnvironmentVariable("ST_REAL_HERMES_DIR") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private static HermesProxyRunner RealRunner(string dir, bool relocate) =>
        new(Log(), Path.Combine(dir, "JimsProxy"), [], Path.Combine(dir, "accept.pid"),
            workingDirectory: dir, outputLogPath: Path.Combine(dir, "accept-proxy.log"),
            relocateAuxiliaryPorts: relocate);

    [Fact]
    public async Task RealProxy_WithTheRestPortTaken_MovesIt_AndListensOnAllFourPorts()
    {
        if (!OperatingSystem.IsLinux() || RealHermesDir is not { } dir) return;
        var rest = HermesPortPlan.ReadConfigured(Path.Combine(dir, "HermesProxy.config"))["RestPort"];
        var squatter = new TcpListener(IPAddress.Loopback, rest); // stands in for the llama-server
        squatter.Start();
        var runner = RealRunner(dir, relocate: true);
        try
        {
            var result = await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromSeconds(60));
            Assert.True(result.Ready, result.Error);
            Assert.NotEqual(rest, runner.AuxiliaryPorts!["RestPort"]);
            foreach (var p in runner.AuxiliaryPorts.Values) Assert.True(Listening(p), $"port {p}");
            Assert.True(await runner.VerifyStillListeningAsync(1119));
        }
        finally
        {
            await runner.StopAsync();
            squatter.Stop();
        }
        Assert.False(Listening(1119));
    }

    [Fact]
    public async Task RealProxy_WithoutRelocation_AndTheRestPortTaken_FailsWithTheProxysOwnWords_AndFrees1119()
    {
        if (!OperatingSystem.IsLinux() || RealHermesDir is not { } dir) return;
        var rest = HermesPortPlan.ReadConfigured(Path.Combine(dir, "HermesProxy.config"))["RestPort"];
        var squatter = new TcpListener(IPAddress.Loopback, rest);
        squatter.Start();
        var runner = RealRunner(dir, relocate: false);
        try
        {
            var result = await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromSeconds(60));
            if (result.Ready)
            {
                await Task.Delay(3000);
                Assert.False(await runner.VerifyStillListeningAsync(1119));
                Assert.False(runner.IsProcessAlive == true);
            }
            else
            {
                Assert.Contains("BnetRestApiSession", result.Error);
            }
        }
        finally
        {
            await runner.StopAsync();
            squatter.Stop();
        }
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (Listening(1119) && DateTime.UtcNow < deadline) await Task.Delay(200);
        Assert.False(Listening(1119));
    }

    [Fact]
    public async Task RealProxy_WithTheBnetPortTaken_NamesTheProcessHoldingIt()
    {
        if (!OperatingSystem.IsLinux() || RealHermesDir is not { } dir) return;
        // A foreign process on 1119 (python3 -m http.server), not a listener in this test process.
        using var squatter = Process.Start(new ProcessStartInfo("/usr/bin/python3",
            ["-m", "http.server", "1119", "--bind", "127.0.0.1"]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!Listening(1119) && DateTime.UtcNow < deadline) await Task.Delay(100);
            var runner = RealRunner(dir, relocate: true);
            var result = await runner.StartAndWaitForPortAsync(1119, TimeSpan.FromSeconds(30));
            Assert.False(result.Ready);
            Assert.Contains("1119", result.Error);
            Assert.Contains($"PID {squatter.Id}", result.Error);
            Assert.Contains("python", result.Error);
            await runner.StopAsync();
        }
        finally
        {
            squatter.Kill();
            squatter.WaitForExit(5000);
        }
    }

    // ── The plan itself ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ThePlan_MovesOnlyTakenPorts_AndNeverOntoTheBnetPortOrEachOther()
    {
        var configured = new Dictionary<string, int> { ["RestPort"] = 8081, ["RealmPort"] = 8084, ["InstancePort"] = 8086 };
        var next = 40000;
        var plan = await HermesPortPlan.BuildAsync(configured, 1119,
            (p, _) => Task.FromResult(p == 8081),
            avoid => { while (avoid.Contains(next)) next++; return next++; });

        Assert.Equal(new[] { "--set", "RestPort=40000" }, plan.ExtraArgs);
        Assert.Equal(40000, plan.Ports["RestPort"]);
        Assert.Equal(8084, plan.Ports["RealmPort"]);
        Assert.Single(plan.Moves);

        var none = await HermesPortPlan.BuildAsync(configured, 1119, (_, _) => Task.FromResult(false), _ => 1);
        Assert.Empty(none.ExtraArgs);

        // A config that puts a port ON the BNet port is moved as well.
        var clash = await HermesPortPlan.BuildAsync(
            new Dictionary<string, int> { ["RestPort"] = 1119, ["RealmPort"] = 8084, ["InstancePort"] = 8086 },
            1119, (_, _) => Task.FromResult(false), _ => 41000);
        Assert.Equal(41000, clash.Ports["RestPort"]);
    }

    [Fact]
    public void ThePlan_ReadsTheProxyConfig_AndFallsBackToTheDefaults()
    {
        var rig = MakeRig();
        var read = HermesPortPlan.ReadConfigured(Path.Combine(rig.Dir, "HermesProxy.config"));
        Assert.Equal(rig.Rest, read["RestPort"]);
        var defaults = HermesPortPlan.ReadConfigured(Path.Combine(rig.Dir, "missing.config"));
        Assert.Equal(8081, defaults["RestPort"]);
        Assert.Equal(8084, defaults["RealmPort"]);
        Assert.Equal(8086, defaults["InstancePort"]);
    }

    [Theory]
    // verbatim from ~/.local/state/stonetavern-launcher/proxy-hermes.log, 2026-09-27 23:12
    [InlineData("Unhandled exception: System.Exception: Failed to start BnetRestApiSession service", true)]
    [InlineData("23:12:19 |  Network | SocketManager   | StartNetwork failed to Start AsyncAcceptor", true)]
    [InlineData("Press enter to close", true)]
    [InlineData("[out] System.Net.Sockets.SocketException (98): Address already in use", true)]
    [InlineData("23:12:19 |  Server  | BnetTcpSession  | Accepting connection from 127.0.0.1:60480.", false)]
    [InlineData("23:12:19 |  Error   | SSLSocket       | System.IO.IOException: Received an unexpected EOF or 0 bytes from the transport stream.", false)]
    public void FatalLines_AreRecognised_AndOrdinaryErrorsAreNot(string line, bool fatal) =>
        Assert.Equal(fatal, HermesPortPlan.IsFatal(line));

    [Fact]
    public void ATakenBnetPort_NamesTheProcessHoldingIt()
    {
        var msg = HermesProxyRunner.PortTakenMessage(1119, "llama-server (PID 4242)");
        Assert.Contains("llama-server (PID 4242)", msg);
        Assert.Contains("1119", msg);
        Assert.Contains("Close that program", msg);
    }
}
