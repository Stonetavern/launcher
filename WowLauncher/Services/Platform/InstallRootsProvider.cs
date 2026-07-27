namespace WowLauncher.Services.Platform;

using WowLauncher.Models;

/// <summary>
/// Supplies the platform-specific search surface for client discovery:
/// the fixed WoW.exe candidate paths tried by <see cref="ClientService.FindWowExe"/>, and the
/// filesystem roots + folder-name leaves scanned by <see cref="ClientService.DetectInstalls"/>.
/// The <em>algorithm</em> (validate a dir, read the build, dedupe) stays in ClientService; only
/// the platform-dependent locations live here.
/// </summary>
public interface IInstallRootsProvider
{
    /// <summary>Fixed WoW.exe candidate paths, in priority order, for a given configured path.</summary>
    IEnumerable<string> ExeSearchPaths(string configuredPath);

    /// <summary>Filesystem roots to scan for loose client folders (drive roots on Windows, XDG
    /// game dirs on Linux — WP2). The scanner also tries each root itself and one level of subfolders.</summary>
    IEnumerable<string> CommonInstallRoots();

    /// <summary>Folder-name fragments private-server clients usually live under.</summary>
    IReadOnlyList<string> InstallFolderNames();
}

/// <summary>
/// Windows roots/paths — byte-for-byte the lists that were inline in ClientService
/// (search paths incl. C:\Games\World of Warcraft\WoW.exe; C:/D:/E: roots; the name set).
/// </summary>
public sealed class WindowsInstallRootsProvider : IInstallRootsProvider
{
    private static readonly string[] Names =
    [
        "World of Warcraft", "WoW", "WoW Classic", "Stonetavern",
        "Stonetavern-WoW", "Vanilla", "TBC", "WotLK", "Wrath",
        "WoW-1.12.1", "WoW-2.4.3", "WoW-3.3.5",
    ];

    public IEnumerable<string> ExeSearchPaths(string configuredPath)
    {
        yield return configuredPath;
        yield return Path.Combine(AppContext.BaseDirectory, configuredPath);
        foreach (var exe in ClientVersion.ExeNames)
        {
            yield return Path.Combine(AppContext.BaseDirectory, exe);
            yield return Path.Combine(Directory.GetCurrentDirectory(), exe);
            yield return $@"C:\Games\World of Warcraft\{exe}";
        }
    }

    public IEnumerable<string> CommonInstallRoots()
    {
        foreach (var drive in new[] { "C:", "D:", "E:" })
        {
            yield return $@"{drive}\Games";
            yield return $@"{drive}\Program Files";
            yield return $@"{drive}\Program Files (x86)";
            yield return $@"{drive}\";
        }
        // Also the launcher's own dir + its parent (portable next-to-client deployment).
        yield return AppContext.BaseDirectory;
        string? parent = null;
        try { parent = Directory.GetParent(AppContext.BaseDirectory)?.FullName; } catch { /* ignore */ }
        if (!string.IsNullOrWhiteSpace(parent)) yield return parent!;
    }

    public IReadOnlyList<string> InstallFolderNames() => Names;
}

/// <summary>
/// Linux roots/paths (WP2). Discovery is read-only and best-effort: it scans the places a Linux
/// gamer keeps a hand-installed WoW client — the XDG games dir, the user's own <c>~/.wine</c>
/// drive_c, Lutris/Steam library roots, the <c>/mnt/data</c> bulk mount — plus the launcher's own
/// managed prefix's drive_c. Foreign prefixes may be <em>found</em> here, but the launcher always
/// <em>starts</em> the client with its own managed prefix (PLAN §1.3); nothing here writes. Only
/// directories that actually exist are returned, so the ClientService scan stays cheap.
/// </summary>
public sealed class LinuxInstallRootsProvider : IInstallRootsProvider
{
    // Shared with Windows — harmless on Linux, and lets a hand-copied client folder be found.
    private static readonly string[] Names =
    [
        "World of Warcraft", "WoW", "WoW Classic", "Stonetavern",
        "Stonetavern-WoW", "Vanilla", "TBC", "WotLK", "Wrath",
        "WoW-1.12.1", "WoW-2.4.3", "WoW-3.3.5",
    ];

    public IEnumerable<string> ExeSearchPaths(string configuredPath)
    {
        yield return configuredPath;
        yield return Path.Combine(AppContext.BaseDirectory, configuredPath);
        foreach (var exe in ClientVersion.ExeNames)
        {
            yield return Path.Combine(AppContext.BaseDirectory, exe);
            yield return Path.Combine(Directory.GetCurrentDirectory(), exe);
        }
    }

    public IEnumerable<string> CommonInstallRoots()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in EnumerateCandidateRoots())
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string full;
            try { full = Path.GetFullPath(root); } catch { continue; }
            if (!seen.Add(full)) continue;
            // Only hand back roots that exist — a scan over a non-existent path is wasted work.
            bool exists;
            try { exists = Directory.Exists(full); } catch { exists = false; }
            if (exists) yield return full;
        }
    }

    private static IEnumerable<string?> EnumerateCandidateRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? H(params string[] parts) =>
            string.IsNullOrWhiteSpace(home) ? null : Path.Combine(new[] { home }.Concat(parts).ToArray());

        // XDG games dir (also the Lutris default game location).
        yield return H("Games");
        // The user's OWN default wine prefix — discovery only; we never launch into it.
        yield return H(".wine", "drive_c");
        // Lutris data root (per-game wine prefixes live under here).
        yield return H(".local", "share", "lutris");
        // Steam library commons (native package + flatpak layout).
        yield return H(".local", "share", "Steam", "steamapps", "common");
        yield return H(".steam", "steam", "steamapps", "common");
        yield return H(".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam", "steamapps", "common");
        // Bulk data mount used on this fleet for WoW clients.
        yield return "/mnt/data";
        // The launcher's OWN managed prefix drive_c (a client copied inside it is discoverable too).
        yield return Path.Combine(WineOptions.DefaultPrefixPath(), "drive_c");

        // F4: wine keeps a SEPARATE prefix per game (lutris/manual), so a client typically sits at
        // <container>/<game>/drive_c/… — two levels below the container, which ClientService's
        // root+one-sublevel scan cannot reach. Expand the middle layer here into concrete candidate
        // roots: each existing <container>/*/drive_c (glob depth 1 + drive_c = max depth 2, read-only).
        // ~/Games is lutris' default prefix location; ~/.local/share/lutris its data root.
        foreach (var r in ExpandPrefixDriveCs(H("Games"))) yield return r;
        foreach (var r in ExpandPrefixDriveCs(H(".local", "share", "lutris"))) yield return r;

        // Portable next-to-launcher deployment.
        yield return AppContext.BaseDirectory;
        string? parent = null;
        try { parent = Directory.GetParent(AppContext.BaseDirectory)?.FullName; } catch { /* ignore */ }
        yield return parent;
    }

    /// <summary>Given a container that holds one wine prefix per game, hand back each existing
    /// <c>&lt;container&gt;/*/drive_c</c> as a concrete client-search root. Bounded (one glob level then
    /// drive_c — max depth 2), existing directories only, strictly read-only.</summary>
    private static IEnumerable<string> ExpandPrefixDriveCs(string? container)
    {
        if (string.IsNullOrWhiteSpace(container) || !SafeDirExists(container))
            yield break;

        string[] subs;
        try { subs = Directory.GetDirectories(container); }
        catch { yield break; }

        foreach (var sub in subs)
        {
            var driveC = Path.Combine(sub, "drive_c");
            if (SafeDirExists(driveC))
                yield return driveC;
        }
    }

    private static bool SafeDirExists(string path)
    {
        try { return Directory.Exists(path); }
        catch { return false; }
    }

    public IReadOnlyList<string> InstallFolderNames() => Names;
}

/// <summary>
/// Neutral roots/paths for platforms without a dedicated implementation yet (macOS and any other
/// host, Codex F6b). Same minimal, harmless surface as Linux (launcher dir + parent + shared names)
/// but without claiming to be the Linux provider. macOS gets its own discovery per AGENTS.md.
/// </summary>
public sealed class NeutralInstallRootsProvider : IInstallRootsProvider
{
    private static readonly string[] Names =
    [
        "World of Warcraft", "WoW", "WoW Classic", "Stonetavern",
        "Stonetavern-WoW", "Vanilla", "TBC", "WotLK", "Wrath",
        "WoW-1.12.1", "WoW-2.4.3", "WoW-3.3.5",
    ];

    public IEnumerable<string> ExeSearchPaths(string configuredPath)
    {
        yield return configuredPath;
        yield return Path.Combine(AppContext.BaseDirectory, configuredPath);
        foreach (var exe in ClientVersion.ExeNames)
        {
            yield return Path.Combine(AppContext.BaseDirectory, exe);
            yield return Path.Combine(Directory.GetCurrentDirectory(), exe);
        }
    }

    public IEnumerable<string> CommonInstallRoots()
    {
        yield return AppContext.BaseDirectory;
        string? parent = null;
        try { parent = Directory.GetParent(AppContext.BaseDirectory)?.FullName; } catch { /* ignore */ }
        if (!string.IsNullOrWhiteSpace(parent)) yield return parent!;
    }

    public IReadOnlyList<string> InstallFolderNames() => Names;
}
