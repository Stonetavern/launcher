using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Services;
using WowLauncher.Services.Platform;
using WowLauncher.Startup;
using WowLauncher.ViewModels;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// "Remember username" on both sign-in forms: the startup login shell and the sidebar card. Off by
/// default; only a name the server accepted is remembered; unticking forgets it on the next sign-in.
/// The name is kept sealed on disk, never in the config. All names here are made up.
/// </summary>
public sealed class RememberUsernameTests
{
    private const string Name = "tester";

    private sealed class MemoryStub : IUsernameMemory
    {
        public string? Stored;
        public string? Load() => Stored;
        public void Save(string username) => Stored = username;
        public void Clear() => Stored = null;
    }

    private sealed class TempPaths : IAppPaths, IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "remember-username-test-" + Guid.NewGuid().ToString("N"));
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

    private static Serilog.ILogger Silent() => new Serilog.LoggerConfiguration().CreateLogger();

    // ── The file ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void File_RoundtripsTheName_AndNeverHoldsItInCleartext()
    {
        using var paths = new TempPaths();
        var memory = new FileUsernameMemory(paths, Silent());

        Assert.Null(memory.Load());
        memory.Save(Name);

        Assert.Equal(Name, memory.Load());
        var raw = File.ReadAllBytes(Path.Combine(paths.ConfigDir, "remembered_username.dat"));
        Assert.DoesNotContain(Name, Encoding.UTF8.GetString(raw), StringComparison.Ordinal);

        memory.Clear();
        Assert.Null(memory.Load());
    }

    [Fact]
    public void File_ThatIsNotOurs_IsForgotten()
    {
        using var paths = new TempPaths();
        paths.EnsureDirectories();
        var path = Path.Combine(paths.ConfigDir, "remembered_username.dat");
        File.WriteAllText(path, Name);   // plaintext, not sealed

        Assert.Null(new FileUsernameMemory(paths, Silent()).Load());
        Assert.False(File.Exists(path));
    }

    // ── The startup login shell ──────────────────────────────────────────────────────────────────

    private static async Task<LoginShellViewModel> ShellVm(string mode, IUsernameMemory memory)
    {
        static InitStep Step(string key, bool gates) =>
            new(key, $"status:{key}", gates, _ => Task.FromResult<string?>(null));
        var pipeline = new InitPipeline(
        [
            Step(LauncherInitSteps.ConfigKey, true),
            Step(LauncherInitSteps.UpdateKey, true),
            Step(LauncherInitSteps.RealmKey, true),
            Step(LauncherInitSteps.CdnKey, false),
            Step(LauncherInitSteps.InstallsKey, false),
        ]);
        var facts = new InitFacts { Realms = [new RealmProbe("stonetavern", "Stonetavern", "play.stonetavern.app", true, 7)] };
        var vm = new LoginShellViewModel(pipeline, facts, new FakeAuthGateway(mode, TimeSpan.Zero, k => k),
                                         post: a => a(), remembered: memory);
        vm.BeginPhase1();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!vm.IsGateOpen)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("gate did not open within 5 s");
            await Task.Delay(5);
        }
        return vm;
    }

    [Fact]
    public async Task Shell_StartsEmptyAndUnticked_WhenNothingIsRemembered()
    {
        var vm = await ShellVm(FakeAuthGateway.SuccessMode, new MemoryStub());

        Assert.Equal("", vm.Username);
        Assert.False(vm.RememberUsername);
    }

    [Fact]
    public async Task Shell_PrefillsAndTicks_WhenANameIsRemembered()
    {
        var vm = await ShellVm(FakeAuthGateway.SuccessMode, new MemoryStub { Stored = Name });

        Assert.Equal(Name, vm.Username);
        Assert.True(vm.RememberUsername);
    }

    [Fact]
    public async Task Shell_Success_WithTheBoxTicked_RemembersTheTrimmedName()
    {
        var memory = new MemoryStub();
        var vm = await ShellVm(FakeAuthGateway.SuccessMode, memory);
        vm.Username = "  " + Name + " "; vm.Password = "pw"; vm.RememberUsername = true;

        await vm.SignInCommand.ExecuteAsync(null);

        Assert.Equal(Name, memory.Stored);
    }

    [Fact]
    public async Task Shell_Success_WithTheBoxUnticked_ForgetsTheName()
    {
        var memory = new MemoryStub { Stored = Name };
        var vm = await ShellVm(FakeAuthGateway.SuccessMode, memory);
        vm.Password = "pw"; vm.RememberUsername = false;

        await vm.SignInCommand.ExecuteAsync(null);

        Assert.Null(memory.Stored);
    }

    [Fact]
    public async Task Shell_Rejected_RemembersNothing()
    {
        var memory = new MemoryStub();
        var vm = await ShellVm(FakeAuthGateway.RejectMode, memory);
        vm.Username = Name; vm.Password = "wrong"; vm.RememberUsername = true;

        await vm.SignInCommand.ExecuteAsync(null);

        Assert.Null(memory.Stored);
    }

    // ── The sidebar card ─────────────────────────────────────────────────────────────────────────

    private sealed class ScriptedAuth(bool ok) : ILauncherAuthService
    {
        public bool IsLoggedIn => false;
        public string? CurrentToken => null;
        public string? CurrentAccount => null;
        public Task<LoginOutcome> LoginAsync(string u, string p, CancellationToken ct = default) =>
            Task.FromResult(ok ? LoginOutcome.Success(u) : LoginOutcome.Failure("no"));
        public Task<LoginOutcome> RegisterAsync(RegisterRequest request, CancellationToken ct = default) =>
            Task.FromResult(LoginOutcome.Failure("unused"));
        public void Logout() { }
    }

    [Fact]
    public async Task Card_Success_WithTheBoxTicked_RemembersAndKeepsTheName()
    {
        var memory = new MemoryStub();
        var vm = new LoginViewModel(new ScriptedAuth(ok: true), remembered: memory);
        Assert.False(vm.RememberUsername);
        vm.Username = Name; vm.Password = "pw"; vm.RememberUsername = true;

        await vm.SignInCommand.ExecuteAsync(null);

        Assert.Equal(Name, memory.Stored);
        Assert.Equal(Name, vm.Username);
    }

    [Fact]
    public async Task Card_Success_WithTheBoxUnticked_ForgetsAndClearsTheName()
    {
        var memory = new MemoryStub { Stored = Name };
        var vm = new LoginViewModel(new ScriptedAuth(ok: true), remembered: memory);
        Assert.Equal(Name, vm.Username);
        vm.Password = "pw"; vm.RememberUsername = false;

        await vm.SignInCommand.ExecuteAsync(null);

        Assert.Null(memory.Stored);
        Assert.Equal("", vm.Username);
    }

    [Fact]
    public async Task Card_Rejected_RemembersNothing()
    {
        var memory = new MemoryStub();
        var vm = new LoginViewModel(new ScriptedAuth(ok: false), remembered: memory);
        vm.Username = Name; vm.Password = "wrong"; vm.RememberUsername = true;

        await vm.SignInCommand.ExecuteAsync(null);

        Assert.Null(memory.Stored);
    }

    /// <summary>The card is built while the login shell may still be signing in, so a name the shell
    /// remembers arrives after construction. Sign-out reloads it.</summary>
    [Fact]
    public void Card_PicksUpANameRememberedAfterItWasBuilt()
    {
        var memory = new MemoryStub();
        var vm = new LoginViewModel(new ScriptedAuth(ok: true), remembered: memory);
        memory.Stored = Name;

        vm.LoadRememberedUsername();

        Assert.Equal(Name, vm.Username);
        Assert.True(vm.RememberUsername);
    }
}
