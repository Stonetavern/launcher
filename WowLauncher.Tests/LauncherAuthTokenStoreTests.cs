using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Models;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Token-store roundtrip + the security invariant that matters most here: the bearer token and the
/// password NEVER reach a log. The store roundtrip runs the non-Windows (plaintext + mode 0600) path
/// on this Fedora box; the DPAPI path is Windows-only and is exercised on Windows, not here (called
/// out honestly in the return). The log-leak proof runs a full fake sign-in and asserts neither the
/// token nor the password appears in any emitted log line.
/// </summary>
public sealed class LauncherAuthTokenStoreTests
{
    private const string Token = "SECRET-bearer-9f83a-do-not-log";
    private const string Password = "hunter2-never-log-this";

    // ── Test doubles ────────────────────────────────────────────────────────────────────────────

    /// <summary>Serilog sink that keeps every rendered line (message + exception) for inspection.</summary>
    private sealed class ListSink : Serilog.Core.ILogEventSink
    {
        public readonly List<string> Lines = [];
        public void Emit(Serilog.Events.LogEvent e)
        {
            lock (Lines) Lines.Add(e.RenderMessage() + " " + (e.Exception?.ToString() ?? ""));
        }
        public string All() { lock (Lines) return string.Join("\n", Lines); }
    }

    private static (Serilog.ILogger log, ListSink sink) CapturingLogger()
    {
        var sink = new ListSink();
        var log = new Serilog.LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        return (log, sink);
    }

    private sealed class TempPaths : IAppPaths, IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "mechagon-token-test-" + Guid.NewGuid().ToString("N"));
        public string ConfigDir => _root;
        public string StateDir => _root;
        public string CacheDir => _root;
        public string LogDir => _root;
        public string ShareDir => _root;
        public string ConfigFilePath => Path.Combine(_root, "launcher_config.json");
        public string NewsCacheFilePath => Path.Combine(_root, "news-cache.json");
        public string ClientInstallDir(int gameBuild) => Path.Combine(_root, $"WoW-Client-{gameBuild}");
        public string ClientDownloadZip(int gameBuild) => Path.Combine(_root, $"WoW-Client-{gameBuild}.zip");
        public void EnsureDirectories() => Directory.CreateDirectory(_root);
        public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }
    }

    private sealed class StubConfig : IConfigService
    {
        public LauncherConfig Load() => new() { RealmlistAddress = "play.stonetavern.app" };
        public void Save(LauncherConfig config) { }
        public bool LastSaveSucceeded => true;
    }

    private sealed class FixedHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        private readonly HttpStatusCode _status = status;
        private readonly string _body = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
    }

    // ── Roundtrip ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Save_ThenLoad_RoundtripsTheSession()
    {
        var (log, _) = CapturingLogger();
        using var paths = new TempPaths();
        paths.EnsureDirectories();
        var store = new FileTokenStore(paths, log);

        var session = new LauncherSession(Token, 42, "tester", 32503680000000L);
        store.Save(session);
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(Token, loaded!.Token);
        Assert.Equal(42, loaded.AccountId);
        Assert.Equal("tester", loaded.Username);
        Assert.Equal(32503680000000L, loaded.ExpiresAt);
    }

    [Fact]
    public void Clear_RemovesTheStoredSession()
    {
        var (log, _) = CapturingLogger();
        using var paths = new TempPaths();
        paths.EnsureDirectories();
        var store = new FileTokenStore(paths, log);

        store.Save(new LauncherSession(Token, 1, "tester", 0));
        store.Clear();

        Assert.Null(store.Load());
    }

    [Fact]
    public void Save_NeverWritesTheTokenIntoLogs()
    {
        var (log, sink) = CapturingLogger();
        using var paths = new TempPaths();
        paths.EnsureDirectories();
        var store = new FileTokenStore(paths, log);

        store.Save(new LauncherSession(Token, 1, "tester", 0));
        store.Load();

        Assert.DoesNotContain(Token, sink.All());
    }

    [Fact]
    public void StoredFile_IsOwnerOnly_OnPosix()
    {
        if (OperatingSystem.IsWindows()) return; // DPAPI path; POSIX modes do not apply

        using var paths = new TempPaths();
        paths.EnsureDirectories();
        var (log, _) = CapturingLogger();
        var store = new FileTokenStore(paths, log);
        store.Save(new LauncherSession(Token, 1, "tester", 0));

        var mode = File.GetUnixFileMode(Path.Combine(paths.ConfigDir, "launcher_session.dat"));
        // No group/other bits at all — owner read+write only.
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    // ── The leak proof: a full sign-in must not log the token or the password ─────────────────────

    [Fact]
    public async Task Login_StoresToken_ButNeverLogsTokenOrPassword()
    {
        var (log, sink) = CapturingLogger();
        using var paths = new TempPaths();
        paths.EnsureDirectories();
        var store = new FileTokenStore(paths, log);

        var responseJson =
            $$"""{"token":"{{Token}}","account":{"id":7,"username":"tester"},"expiresAt":32503680000000}""";
        var handler = new FixedHandler(HttpStatusCode.OK, responseJson);
        var auth = new LauncherAuthService(new HttpClient(handler), new StubConfig(), store, log);

        var outcome = await auth.LoginAsync("tester", Password);

        Assert.True(outcome.Ok);
        Assert.Equal("tester", outcome.AccountName);
        Assert.True(auth.IsLoggedIn);
        Assert.Equal(Token, auth.CurrentToken);        // token is usable in memory
        Assert.NotNull(store.Load());                  // and persisted

        var logs = sink.All();
        Assert.DoesNotContain(Token, logs);            // but never in the log
        Assert.DoesNotContain(Password, logs);         // and neither is the password
    }

    [Fact]
    public async Task Login_On401_ReturnsInvalidCredentials_WithoutLoggingPassword()
    {
        var (log, sink) = CapturingLogger();
        using var paths = new TempPaths();
        paths.EnsureDirectories();
        var store = new FileTokenStore(paths, log);
        var handler = new FixedHandler(HttpStatusCode.Unauthorized, "");
        var auth = new LauncherAuthService(new HttpClient(handler), new StubConfig(), store, log);

        var outcome = await auth.LoginAsync("tester", Password);

        Assert.False(outcome.Ok);
        Assert.Equal("Invalid username or password.", outcome.Error);
        Assert.False(auth.IsLoggedIn);
        Assert.DoesNotContain(Password, sink.All());
    }

    [Fact]
    public async Task Login_RestoresPersistedSession_OnNextConstruction()
    {
        var (log, _) = CapturingLogger();
        using var paths = new TempPaths();
        paths.EnsureDirectories();
        var store = new FileTokenStore(paths, log);

        var responseJson =
            $$"""{"token":"{{Token}}","account":{"id":7,"username":"tester"},"expiresAt":32503680000000}""";
        var auth = new LauncherAuthService(
            new HttpClient(new FixedHandler(HttpStatusCode.OK, responseJson)), new StubConfig(), store, log);
        await auth.LoginAsync("tester", Password);

        // A fresh service over the SAME store (simulating a relaunch) is already signed in.
        var reopened = new LauncherAuthService(
            new HttpClient(new FixedHandler(HttpStatusCode.OK, "")), new StubConfig(), store, log);
        Assert.True(reopened.IsLoggedIn);
        Assert.Equal(Token, reopened.CurrentToken);
    }

    [Fact]
    public void Login_DropsExpiredSession_OnConstruction()
    {
        var (log, _) = CapturingLogger();
        using var paths = new TempPaths();
        paths.EnsureDirectories();
        var store = new FileTokenStore(paths, log);
        store.Save(new LauncherSession(Token, 7, "tester", 946684800000L)); // long past (2000-01-01 in unix ms)

        var auth = new LauncherAuthService(
            new HttpClient(new FixedHandler(HttpStatusCode.OK, "")), new StubConfig(), store, log);

        Assert.False(auth.IsLoggedIn);
        Assert.Null(auth.CurrentToken);
        Assert.Null(store.Load()); // expired session was cleared
    }
}
