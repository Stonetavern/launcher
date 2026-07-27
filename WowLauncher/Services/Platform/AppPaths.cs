namespace WowLauncher.Services.Platform;

/// <summary>
/// Central, per-OS resolver for every location the launcher WRITES to. One seam so the path policy
/// lives in exactly one place instead of being scattered across <c>AppContext.BaseDirectory</c>
/// calls (Codex A8).
///
/// <para><b>Windows</b> keeps today's behaviour <b>byte-for-byte</b>: every directory resolves to
/// <c>AppContext.BaseDirectory</c> (next to the exe), the config file is
/// <c>&lt;exe&gt;\launcher_config.json</c>, the client build extracts to
/// <c>&lt;exe&gt;\WoW-Client-&lt;build&gt;</c>. Live players see no change and there is no
/// migration.</para>
///
/// <para><b>Linux</b> follows the XDG Base Directory spec, honouring
/// <c>XDG_CONFIG_HOME</c> / <c>XDG_STATE_HOME</c> / <c>XDG_CACHE_HOME</c> / <c>XDG_DATA_HOME</c>
/// when they are set to an absolute path (the spec says relative values are invalid → ignored),
/// otherwise the documented defaults <c>~/.config</c>, <c>~/.local/state</c>, <c>~/.cache</c>,
/// <c>~/.local/share</c>. Everything lives under a <c>stonetavern-launcher/</c> sub-directory.</para>
/// </summary>
public interface IAppPaths
{
    /// <summary>Directory holding <c>launcher_config.json</c>. Linux: <c>~/.config/stonetavern-launcher</c>.</summary>
    string ConfigDir { get; }

    /// <summary>Directory holding runtime state — logs and crash logs. Linux: <c>~/.local/state/stonetavern-launcher</c>.</summary>
    string StateDir { get; }

    /// <summary>Directory for regenerable download scratch (<c>.part</c>/<c>.zip</c>). Linux: <c>~/.cache/stonetavern-launcher</c>.</summary>
    string CacheDir { get; }

    /// <summary>Directory the Serilog rolling file sink writes to (== <see cref="StateDir"/>).</summary>
    string LogDir { get; }

    /// <summary>Directory for durable data — installed clients and the managed Wine prefix. Linux: <c>~/.local/share/stonetavern-launcher</c>.</summary>
    string ShareDir { get; }

    /// <summary>Full path of <c>launcher_config.json</c>.</summary>
    string ConfigFilePath { get; }

    /// <summary>Full path of the offline news cache (<c>news-cache.json</c>). <b>Windows keeps the
    /// historical <c>%LocalAppData%\Stonetavern\news-cache.json</c> location byte-for-byte</b> (this is
    /// where <c>NewsService</c> shipped it); Linux places it under <see cref="CacheDir"/>
    /// (<c>~/.cache/stonetavern-launcher/news-cache.json</c>). Regenerable scratch, safe to delete.</summary>
    string NewsCacheFilePath { get; }

    /// <summary>Extract target for a given game build's client (under <see cref="ShareDir"/> on Linux, next to the exe on Windows).</summary>
    string ClientInstallDir(int gameBuild);

    /// <summary>Download scratch zip path for a given game build (under <see cref="CacheDir"/> on Linux, next to the exe on Windows).</summary>
    string ClientDownloadZip(int gameBuild);

    /// <summary>Best-effort create every directory this resolver hands out. Never throws.</summary>
    void EnsureDirectories();
}

/// <summary>Shared naming so Windows and XDG impls never drift on the file/folder names.</summary>
internal static class AppPathNames
{
    internal const string App = "stonetavern-launcher";
    internal const string ConfigFile = "launcher_config.json";
    internal const string NewsCacheFile = "news-cache.json";
    internal static string ClientDirName(int build) => $"WoW-Client-{build}";
    internal static string ClientZipName(int build) => $"WoW-Client-{build}.zip";
}

/// <summary>
/// Factory: hands back the resolver for the current OS. Windows → next-to-exe (byte-gleich);
/// every other host → XDG. macOS gets XDG as a stopgap until the macOS agent adds its own
/// <c>~/Library/Application Support</c> impl (AGENTS.md §macOS); the DI split mirrors WP1.
/// </summary>
public static class AppPaths
{
    public static IAppPaths ForCurrentOs() =>
        OperatingSystem.IsWindows() ? new WindowsAppPaths() : new XdgAppPaths();
}

/// <summary>
/// Windows resolver — everything next to the exe (<c>AppContext.BaseDirectory</c>). This reproduces
/// the shipped behaviour exactly: <c>ConfigService</c> used <c>AppContext.BaseDirectory</c>, the
/// Serilog sink wrote <c>launcher.log</c> next to the exe, crash logs and client zips/extract dirs
/// all lived there too. No XDG, no migration.
/// </summary>
public sealed class WindowsAppPaths : IAppPaths
{
    private static string Base => AppContext.BaseDirectory;

    public string ConfigDir => Base;
    public string StateDir => Base;
    public string CacheDir => Base;
    public string LogDir => Base;
    public string ShareDir => Base;
    public string ConfigFilePath => Path.Combine(Base, AppPathNames.ConfigFile);

    // Byte-gleich: NewsService shipped the disk cache under %LocalAppData%\Stonetavern (NOT next to
    // the exe like the other write paths), so the Windows resolver maps it there exactly — live
    // players keep their existing cache, no migration.
    public string NewsCacheFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Stonetavern", AppPathNames.NewsCacheFile);

    public string ClientInstallDir(int gameBuild) => Path.Combine(Base, AppPathNames.ClientDirName(gameBuild));
    public string ClientDownloadZip(int gameBuild) => Path.Combine(Base, AppPathNames.ClientZipName(gameBuild));

    public void EnsureDirectories() { /* the exe dir already exists — nothing to create */ }
}

/// <summary>
/// XDG Base Directory resolver (Linux and, as a stopgap, any non-Windows host). Each root honours
/// its <c>XDG_*_HOME</c> override when it is an absolute path, else falls back to the spec default
/// relative to <c>$HOME</c>. Clients + the managed Wine prefix live under
/// <see cref="ShareDir"/> (<c>~/.local/share/stonetavern-launcher</c>); download scratch under
/// <see cref="CacheDir"/> (<c>~/.cache/stonetavern-launcher</c>).
/// </summary>
public sealed class XdgAppPaths : IAppPaths
{
    private readonly string _config;
    private readonly string _state;
    private readonly string _cache;
    private readonly string _share;

    public XdgAppPaths()
    {
        var home = HomeDir();
        _config = Path.Combine(Resolve("XDG_CONFIG_HOME", Path.Combine(home, ".config")), AppPathNames.App);
        _state = Path.Combine(Resolve("XDG_STATE_HOME", Path.Combine(home, ".local", "state")), AppPathNames.App);
        _cache = Path.Combine(Resolve("XDG_CACHE_HOME", Path.Combine(home, ".cache")), AppPathNames.App);
        _share = Path.Combine(Resolve("XDG_DATA_HOME", Path.Combine(home, ".local", "share")), AppPathNames.App);
    }

    public string ConfigDir => _config;
    public string StateDir => _state;
    public string CacheDir => _cache;
    public string LogDir => _state;
    public string ShareDir => _share;
    public string ConfigFilePath => Path.Combine(_config, AppPathNames.ConfigFile);
    public string NewsCacheFilePath => Path.Combine(_cache, AppPathNames.NewsCacheFile);
    public string ClientInstallDir(int gameBuild) => Path.Combine(_share, AppPathNames.ClientDirName(gameBuild));
    public string ClientDownloadZip(int gameBuild) => Path.Combine(_cache, AppPathNames.ClientZipName(gameBuild));

    public void EnsureDirectories()
    {
        foreach (var dir in new[] { _config, _state, _cache, _share })
        {
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) { Serilog.Log.Debug(ex, "Could not create app directory {Dir}", dir); }
        }
    }

    private static string HomeDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home)) return home;
        // Fall back to $HOME if the runtime could not resolve the profile (should not happen on Linux).
        return Environment.GetEnvironmentVariable("HOME") ?? ".";
    }

    /// <summary>XDG spec: an <c>XDG_*_HOME</c> value must be an absolute path to be honoured;
    /// anything else (unset, empty, relative) falls back to the default.</summary>
    private static string Resolve(string envVar, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(envVar);
        return !string.IsNullOrEmpty(value) && Path.IsPathRooted(value) ? value : fallback;
    }
}
