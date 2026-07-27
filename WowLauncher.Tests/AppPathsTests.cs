using System;
using System.IO;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// WP3 path-abstraction proofs. Two things must hold: the Windows branch reproduces today's
/// next-to-exe behaviour byte-for-byte (no migration for live players), and the Linux branch maps
/// onto the XDG Base Directory spec, honouring the XDG_*_HOME overrides.
///
/// The XDG tests mutate process-global environment variables, so this class is marked
/// non-parallel and every test restores what it changed.
/// </summary>
[Collection("env-mutating")]
public sealed class AppPathsTests
{
    private const string App = "stonetavern-launcher";

    [Fact]
    public void Windows_MapsEverythingNextToTheExe_ByteGleich()
    {
        var p = new WindowsAppPaths();
        var baseDir = AppContext.BaseDirectory;

        // Every root is the exe directory — exactly the pre-WP3 behaviour.
        Assert.Equal(baseDir, p.ConfigDir);
        Assert.Equal(baseDir, p.StateDir);
        Assert.Equal(baseDir, p.CacheDir);
        Assert.Equal(baseDir, p.LogDir);
        Assert.Equal(baseDir, p.ShareDir);

        // The concrete file/dir names match what ConfigService / PlayViewModel wrote before.
        Assert.Equal(Path.Combine(baseDir, "launcher_config.json"), p.ConfigFilePath);
        Assert.Equal(Path.Combine(baseDir, "WoW-Client-5875"), p.ClientInstallDir(5875));
        Assert.Equal(Path.Combine(baseDir, "WoW-Client-5875.zip"), p.ClientDownloadZip(5875));

        // F2 byte-gleich: the news cache shipped under %LocalAppData%\Stonetavern (NOT next to the
        // exe) — the Windows resolver must keep that exact location so live players' cache survives.
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Stonetavern", "news-cache.json"),
            p.NewsCacheFilePath);
    }

    [Fact]
    public void Linux_DefaultsFollowXdgSpec_WhenNoOverrides()
    {
        WithEnv(new()
        {
            ["XDG_CONFIG_HOME"] = null,
            ["XDG_STATE_HOME"] = null,
            ["XDG_CACHE_HOME"] = null,
            ["XDG_DATA_HOME"] = null
        }, () =>
{
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var p = new XdgAppPaths();

    Assert.Equal(Path.Combine(home, ".config", App), p.ConfigDir);
    Assert.Equal(Path.Combine(home, ".local", "state", App), p.StateDir);
    Assert.Equal(Path.Combine(home, ".cache", App), p.CacheDir);
    Assert.Equal(Path.Combine(home, ".local", "share", App), p.ShareDir);

    // LogDir is StateDir; installs live under ShareDir; downloads under CacheDir.
    Assert.Equal(p.StateDir, p.LogDir);
    Assert.Equal(Path.Combine(home, ".config", App, "launcher_config.json"), p.ConfigFilePath);
    Assert.Equal(Path.Combine(p.ShareDir, "WoW-Client-5875"), p.ClientInstallDir(5875));
    Assert.Equal(Path.Combine(p.CacheDir, "WoW-Client-5875.zip"), p.ClientDownloadZip(5875));
    // F2: on Linux the news cache lives under the XDG cache dir (regenerable scratch).
    Assert.Equal(Path.Combine(home, ".cache", App, "news-cache.json"), p.NewsCacheFilePath);
});
    }

    [Fact]
    public void Linux_HonoursXdgOverrides_WhenAbsolute()
    {
        var root = Path.Combine(Path.GetTempPath(), "mechagon-xdg-test-" + Guid.NewGuid().ToString("N"));
        var cfg = Path.Combine(root, "cfg");
        var state = Path.Combine(root, "state");
        var cache = Path.Combine(root, "cache");
        var data = Path.Combine(root, "data");

        WithEnv(new()
        {
            ["XDG_CONFIG_HOME"] = cfg,
            ["XDG_STATE_HOME"] = state,
            ["XDG_CACHE_HOME"] = cache,
            ["XDG_DATA_HOME"] = data
        }, () =>
{
    var p = new XdgAppPaths();

    Assert.Equal(Path.Combine(cfg, App), p.ConfigDir);
    Assert.Equal(Path.Combine(state, App), p.StateDir);
    Assert.Equal(Path.Combine(cache, App), p.CacheDir);
    Assert.Equal(Path.Combine(data, App), p.ShareDir);
    Assert.Equal(Path.Combine(state, App), p.LogDir);
    Assert.Equal(Path.Combine(data, App, "WoW-Client-5875"), p.ClientInstallDir(5875));
    Assert.Equal(Path.Combine(cache, App, "WoW-Client-5875.zip"), p.ClientDownloadZip(5875));
});
    }

    [Fact]
    public void Linux_IgnoresRelativeXdgValue_PerSpec()
    {
        // The XDG spec: a relative value in XDG_*_HOME is invalid and must be ignored (→ default).
        WithEnv(new() { ["XDG_CACHE_HOME"] = "relative/not/absolute" }, () =>
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var p = new XdgAppPaths();
            Assert.Equal(Path.Combine(home, ".cache", App), p.CacheDir);
        });
    }

    /// <summary>Set the given env vars (null = unset), run the body, then restore every original value.</summary>
    private static void WithEnv(System.Collections.Generic.Dictionary<string, string?> vars, Action body)
    {
        var saved = new System.Collections.Generic.Dictionary<string, string?>();
        foreach (var key in vars.Keys)
            saved[key] = Environment.GetEnvironmentVariable(key);
        try
        {
            foreach (var (key, value) in vars)
                Environment.SetEnvironmentVariable(key, value);
            body();
        }
        finally
        {
            foreach (var (key, value) in saved)
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}
