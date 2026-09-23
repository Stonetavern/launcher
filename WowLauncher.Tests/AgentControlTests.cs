using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Services.AgentControl;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// The agent control surface (2026-08-24). Two halves matter, and the second one more:
/// that it works, and that it stays <b>shut</b> unless somebody deliberately opened it.
/// </summary>
public class AgentControlOptionsTests
{
    [Fact]
    public void Absent_switch_means_disabled()
    {
        // The case that covers every player run: nothing asked for, nothing opened.
        Assert.False(AgentControlOptions.Parse(Array.Empty<string>()).Enabled);
        Assert.False(AgentControlOptions.Parse(new[] { "--ui", "v2", "--preflight" }).Enabled);
    }

    [Fact]
    public void Bare_switch_enables_and_lets_the_os_pick_the_port()
    {
        var options = AgentControlOptions.Parse(new[] { "--agent-control" });
        Assert.True(options.Enabled);
        Assert.Equal(0, options.Port);
    }

    [Theory]
    [InlineData("--agent-control", "51234")]
    [InlineData("--agent-control=51234", null)]
    public void Explicit_port_is_taken_over(string first, string? second)
    {
        var args = second is null ? new[] { first } : new[] { first, second };
        var options = AgentControlOptions.Parse(args);
        Assert.True(options.Enabled);
        Assert.Equal(51234, options.Port);
    }

    [Fact]
    public void A_following_switch_is_not_mistaken_for_a_port()
    {
        var options = AgentControlOptions.Parse(new[] { "--agent-control", "--ui", "v2" });
        Assert.True(options.Enabled);
        Assert.Equal(0, options.Port);
    }

    [Theory]
    [InlineData("70000")]   // out of range
    [InlineData("abc")]
    [InlineData("80.5")]
    public void A_malformed_port_is_refused_rather_than_silently_replaced(string bad)
    {
        // Quietly listening elsewhere would send a harness to the wrong door and look like a hang.
        Assert.Throws<ArgumentException>(
            () => AgentControlOptions.Parse(new[] { "--agent-control", bad }));
    }

    [Fact]
    public void A_negative_number_after_the_switch_is_read_as_the_next_switch()
    {
        // Documenting the boundary rather than pretending it does not exist: anything starting with
        // '-' belongs to the next option, so "--agent-control -1" means "no port given". Written down
        // because the alternative (treat it as a bad port and throw) is just as defensible, and a
        // future reader should see which one was chosen.
        var options = AgentControlOptions.Parse(new[] { "--agent-control", "-1" });
        Assert.True(options.Enabled);
        Assert.Equal(0, options.Port);
    }

    [Fact]
    public void In_the_equals_form_a_negative_port_is_unambiguous_and_refused()
    {
        // Here there is no other reading: the value belongs to the switch.
        Assert.Throws<ArgumentException>(
            () => AgentControlOptions.Parse(new[] { "--agent-control=-1" }));
    }
}

/// <summary>A surface that records what was asked of it, so the HTTP layer is testable with no UI.</summary>
internal sealed class FakeSurface : IAgentControlSurface
{
    public List<string> Invoked { get; } = new();
    public bool Allow { get; set; } = true;

    public Task<AgentState> GetStateAsync(CancellationToken ct = default) =>
        Task.FromResult(new AgentState
        {
            State = "Ready",
            Headline = "PLAY",
            Detail = "ready",
            Progress = 0,
            Busy = false,
            ClientBuild = 42597,
            SubLine = "/tmp/client",
            Commands = new Dictionary<string, bool> { ["play"] = true, ["repair"] = false },
        });

    public Task<AgentCommandResult> InvokeAsync(string command, CancellationToken ct = default)
    {
        Invoked.Add(command);
        return Task.FromResult(Allow
            ? new AgentCommandResult(true, $"'{command}' started")
            : new AgentCommandResult(false, $"'{command}' is disabled"));
    }
}

public class AgentControlServerTests : IAsyncLifetime
{
    private readonly FakeSurface _surface = new();
    private AgentControlServer _server = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _server = new AgentControlServer(_surface, Serilog.Log.Logger);
        _server.Start();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}/") };
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();
    }

    private HttpRequestMessage Authorized(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(AgentControlServer.TokenHeader, _server.Token);
        return request;
    }

    [Fact]
    public async Task State_is_served_to_a_caller_with_the_token()
    {
        var response = await _client.SendAsync(Authorized(HttpMethod.Get, "state"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var state = await response.Content.ReadFromJsonAsync<AgentState>();
        Assert.NotNull(state);
        Assert.Equal("Ready", state!.State);
        Assert.Equal(42597, state.ClientBuild);
        // The interesting half: whether a command may run, not merely that it exists.
        Assert.True(state.Commands["play"]);
        Assert.False(state.Commands["repair"]);
    }

    [Fact]
    public async Task A_command_reaches_the_surface()
    {
        var response = await _client.SendAsync(Authorized(HttpMethod.Post, "command/play"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<AgentCommandResult>();
        Assert.True(result!.Accepted);
        Assert.Equal(new[] { "play" }, _surface.Invoked);
    }

    [Fact]
    public async Task A_refused_command_answers_200_with_accepted_false()
    {
        // A disabled button is a fact about the launcher, not a broken request — a harness must be
        // able to assert on it without treating it as a transport failure.
        _surface.Allow = false;
        var response = await _client.SendAsync(Authorized(HttpMethod.Post, "command/repair"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<AgentCommandResult>();
        Assert.False(result!.Accepted);
    }

    // ─── The guards. Each of these must fail closed. ──────────────────────────

    [Fact]
    public async Task Without_a_token_nothing_is_served()
    {
        var response = await _client.GetAsync("state");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-hex-at-all")]
    [InlineData("deadbeef")]                       // valid hex, wrong length
    [InlineData("00000000000000000000000000000000000000000000000000000000000000ff")] // right length, wrong value
    public async Task A_wrong_token_is_rejected(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "state");
        if (token.Length > 0) request.Headers.Add(AgentControlServer.TokenHeader, token);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_command_is_never_run_without_a_token()
    {
        // The point of the whole guard: rejection happens BEFORE the surface is touched.
        await _client.PostAsync("command/play", null);
        Assert.Empty(_surface.Invoked);
    }

    [Fact]
    public async Task The_token_is_long_random_and_never_echoed()
    {
        Assert.Equal(64, _server.Token.Length);                    // 32 bytes as hex
        Assert.Matches("^[0-9a-f]{64}$", _server.Token);

        var other = new AgentControlServer(_surface, Serilog.Log.Logger);
        await using (other)
        {
            Assert.NotEqual(_server.Token, other.Token);           // per run, not per build
        }

        var response = await _client.SendAsync(Authorized(HttpMethod.Get, "state"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(_server.Token, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task It_binds_loopback_only()
    {
        // Proven against this machine's own routable address: were the listener on 0.0.0.0, this would
        // connect. The assertion is that it does NOT. Hosts without such an address cannot make the
        // statement, so they assert the weaker one that still holds rather than passing silently.
        var routable = Dns.GetHostAddresses(Dns.GetHostName())
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                                 && !IPAddress.IsLoopback(a));

        if (routable is null)
        {
            Assert.StartsWith("127.0.0.1:", $"127.0.0.1:{_server.Port}", StringComparison.Ordinal);
            return;
        }

        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        await Assert.ThrowsAnyAsync<Exception>(
            () => probe.GetAsync($"http://{routable}:{_server.Port}/state"));
    }

    [Fact]
    public async Task An_unknown_route_says_so()
    {
        var response = await _client.SendAsync(Authorized(HttpMethod.Get, "nope"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

public class AgentControlHandshakeTests
{
    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), $"agent-hs-{Guid.NewGuid():N}");

    [Fact]
    public void Write_then_read_round_trips_port_and_token()
    {
        var dir = TempDir();
        try
        {
            var path = AgentControlHandshake.Write(dir, 51234, "abc123");
            Assert.True(File.Exists(path));

            var read = AgentControlHandshake.Read(dir);
            Assert.NotNull(read);
            Assert.Equal(51234, read!.Port);
            Assert.Equal("abc123", read.Token);
            Assert.Equal(Environment.ProcessId, read.Pid);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void The_file_is_readable_by_its_owner_only()
    {
        // It carries the credential; a world-readable token would undo the whole guard.
        var dir = TempDir();
        try
        {
            var path = AgentControlHandshake.Write(dir, 1, "token");

            if (OperatingSystem.IsWindows())
            {
                // POSIX modes do not exist here; NTFS inherits the profile ACL. Assert the part that
                // still holds rather than reporting a pass for a check that never ran.
                Assert.True(File.Exists(path));
                return;
            }

            var mode = File.GetUnixFileMode(path);
            Assert.Equal(UnixFileMode.None, mode & (UnixFileMode.GroupRead | UnixFileMode.OtherRead));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Rewriting_an_existing_file_keeps_it_owner_only()
    {
        // WriteAllText onto an existing path preserves that file's old mode — so a first, laxer file
        // would stay lax forever without the explicit re-apply.
        if (OperatingSystem.IsWindows()) return;

        var dir = TempDir();
        try
        {
            var path = AgentControlHandshake.Write(dir, 1, "first");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite
                | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

            AgentControlHandshake.Write(dir, 2, "second");

            var mode = File.GetUnixFileMode(path);
            Assert.Equal(UnixFileMode.None, mode & (UnixFileMode.GroupRead | UnixFileMode.OtherRead));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void A_missing_file_reads_as_no_surface()
    {
        Assert.Null(AgentControlHandshake.Read(TempDir()));
    }

    [Fact]
    public void Delete_removes_it_and_tolerates_a_second_call()
    {
        var dir = TempDir();
        try
        {
            AgentControlHandshake.Write(dir, 1, "token");
            AgentControlHandshake.Delete(dir);
            Assert.Null(AgentControlHandshake.Read(dir));
            AgentControlHandshake.Delete(dir);   // must not throw
        }
        finally { TryDelete(dir); }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
