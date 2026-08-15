namespace WowLauncher.Services;

using WowLauncher.Models;
using WowLauncher.Services.Platform;

/// <summary>
/// Discovers and launches the WoW client executable.
/// </summary>
public interface IClientService
{
    string? FindWowExe(string? configuredPath = null);

    /// <summary>
    /// Resolve the WoW.exe for a specific gamebuild using the per-build install map
    /// (<see cref="Models.LauncherConfig.ClientInstalls"/>). Returns null when no install
    /// is registered for that era yet — the caller then drives an <see cref="Models.LauncherState.EraTransition"/>
    /// or <see cref="Models.LauncherState.NoClient"/> download.
    /// </summary>
    string? FindWowExeForBuild(int gameBuild, IReadOnlyDictionary<int, string> installs);

    /// <summary>
    /// Scan the system for already-installed WoW clients the player didn't tell us about:
    /// the Blizzard registry keys, the common install locations, and any already-known dirs.
    /// Each candidate is validated (WoW.exe + a Data dir with .MPQs) and its exact build read
    /// from the WoW.exe file version. Returns build → directory for every distinct era found.
    /// Pure discovery — no side effects; the caller merges the result into config.
    /// </summary>
    IReadOnlyDictionary<int, string> DetectInstalls(IReadOnlyDictionary<int, string> known);

    /// <summary>
    /// Read the exact client build (5875 / 8606 / 12340 …) from a WoW directory by inspecting
    /// the WoW.exe file version. Returns null when the dir isn't a valid client or the version
    /// can't be read. Used both for discovery and to tell an <em>older</em> install apart from
    /// the current one (§6.3 update detection).
    /// </summary>
    int? DetectBuild(string wowDirectory);

    /// <summary>True if a WoW.exe process is currently running — the launcher must not write client
    /// files (download/extract/repair) while the game holds locks on them (Hermes Q1).</summary>
    bool IsGameRunning();

    void SetRealmlist(string wowDirectory, string realmlistAddress);

    /// <summary>
    /// Configure the client for a locale + realm the Blizzard way: set
    /// <c>SET locale "&lt;x&gt;"</c> + <c>SET realmList</c> in WTF/Config.wtf and write
    /// Data/&lt;locale&gt;/realmlist.wtf (where TBC/WotLK actually read the realm). The
    /// matching Data/&lt;locale&gt;/ MPQs must already be present — selecting a locale just
    /// activates the already-installed language pack (text + audio).
    /// </summary>
    void ConfigureClient(string wowDirectory, string locale, string realmlistAddress);

    /// <summary>Start the WoW client at <paramref name="wowExePath"/>. Returns the platform launcher's
    /// <see cref="GameLaunchResult"/> so the caller can surface the concrete failure text (stub/Wine/
    /// Process.Start message) instead of a generic error (Codex F6a). Never throws.</summary>
    Task<GameLaunchResult> LaunchAsync(string wowExePath);
}

public sealed class ClientService : IClientService
{
    private readonly IConfigService _config;
    private readonly Serilog.ILogger _logger;
    private readonly IInstallRootsProvider _roots;
    private readonly IGameProcessDetector _detector;
    private readonly IGameLauncher _launcher;

    public ClientService(
        IConfigService config,
        Serilog.ILogger logger,
        IInstallRootsProvider roots,
        IGameProcessDetector detector,
        IGameLauncher launcher)
    {
        _config = config;
        _logger = logger;
        _roots = roots;
        _detector = detector;
        _launcher = launcher;
    }

    public string? FindWowExe(string? configuredPath = null)
    {
        configuredPath ??= _config.Load().WowExecutablePath;

        // When the caller hands us a directory (e.g. the freshly extracted client dir),
        // resolve WoW.exe inside it. Some client ZIPs wrap everything in a single top-level
        // folder (e.g. "Stonetavern-WoW-1.12.1/WoW.exe"), so also look one level down.
        if (Directory.Exists(configuredPath))
        {
            var resolved = ResolveExeInDir(configuredPath);
            if (resolved is not null)
            {
                _logger.Information("Found WoW client at {Path}", resolved);
                return resolved;
            }
        }

        // Platform-specific candidate list (Windows incl. C:\Games\…; Linux neutral-only until WP2).
        foreach (var path in _roots.ExeSearchPaths(configuredPath))
        {
            if (File.Exists(path))
            {
                var full = Path.GetFullPath(path);
                _logger.Information("Found WoW client at {Path}", full);
                return full;
            }
        }

        _logger.Warning("WoW.exe not found in any search path");
        return null;
    }

    /// <summary>The client executable directly in <paramref name="dir"/>, else inside a single
    /// wrapping sub-folder (the layout some client ZIPs use). Returns the full exe path or null.
    ///
    /// <para>Tries every name the launcher knows, not just <c>WoW.exe</c>: the 1.14 Classic Era client
    /// ships as <c>WowClassic.exe</c>, and hard-coding one name meant a whole client generation could
    /// never be found - a silent miss, not an error.</para></summary>
    private static string? ResolveExeInDir(string dir)
    {
        foreach (var exe in ClientVersion.ExeNames)
        {
            var direct = Path.Combine(dir, exe);
            if (File.Exists(direct)) return Path.GetFullPath(direct);
        }
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            foreach (var exe in ClientVersion.ExeNames)
            {
                var nested = Path.Combine(sub, exe);
                if (File.Exists(nested)) return Path.GetFullPath(nested);
            }
            // Two levels down: the modern 1.14.2 bundle is a multi-folder tree (root holds Hermes/,
            // Launcher/ and "World of Warcraft/") whose exe lives at
            // "World of Warcraft/_classic_era_/WowClassic.exe" — the single-wrapper case above never
            // reaches it, so a freshly extracted 1.14.2 client was reported "not found" and the whole
            // download failed after the multi-GB transfer (2026-07-23). Bounded to directories, not a
            // full file walk of a multi-GB client, so it stays cheap.
            foreach (var sub2 in Directory.EnumerateDirectories(sub))
            {
                foreach (var exe in ClientVersion.ExeNames)
                {
                    var deep = Path.Combine(sub2, exe);
                    if (File.Exists(deep)) return Path.GetFullPath(deep);
                }
            }
        }
        return null;
    }

    public string? FindWowExeForBuild(int gameBuild, IReadOnlyDictionary<int, string> installs)
    {
        // A registered install for this era wins — that's the player's own per-build WoW dir.
        if (installs.TryGetValue(gameBuild, out var dir) && !string.IsNullOrWhiteSpace(dir))
        {
            // Prefer the executable this build is actually shipped as, then fall back to the others -
            // a player may have registered the directory before the launcher knew the build. BUT the
            // fallback must never hand back an exe that unambiguously belongs to a DIFFERENT build
            // (Orchestrator Blocker 2): WoW.exe is never build 42597, WowClassic.exe is never
            // 5875/8606/12340. Starting the wrong client for a realm is exactly the silent failure
            // this whole package exists to close.
            var names = ClientVersion.ByBuild(gameBuild) is { } known
                ? new[] { known.ExeName }.Concat(ClientVersion.ExeNames).Distinct(StringComparer.OrdinalIgnoreCase)
                : ClientVersion.ExeNames.AsEnumerable();

            foreach (var name in names)
            {
                // Reject a name that is never shipped AS gameBuild — WoW.exe is never 42597,
                // WowClassic.exe is never 5875/8606/12340. Names shared across several builds (WoW.exe)
                // stay acceptable for any of THOSE builds; only a build outside that name's own set is
                // rejected (a name unique to exactly one build is simply that set with one element).
                if (!ClientVersion.ExeNameCanBeBuild(name, gameBuild))
                    continue;

                var exe = Path.Combine(dir, name);
                if (!File.Exists(exe)) continue;
                var full = Path.GetFullPath(exe);
                _logger.Information("Found build {Build} client at {Path}", gameBuild, full);
                return full;
            }
            _logger.Warning(
                "Registered install for build {Build} has no matching exe (only names that belong to a " +
                "different build, or none at all) — {Dir}", gameBuild, dir);
        }

        _logger.Information("No registered client for build {Build}", gameBuild);
        return null;
    }

    // ─── Auto-detection of existing installs ──────────────────────────────
    // Single source of truth: whatever ClientVersion declares, incl. 1.14.2 (42597).
    private static IReadOnlyList<int> KnownBuilds => ClientVersion.KnownBuilds;

    public int? DetectBuild(string wowDirectory)
    {
        if (string.IsNullOrWhiteSpace(wowDirectory)) return null;

        // A dir may contain more than one known exe (e.g. a leftover WoW.exe next to a freshly
        // installed WowClassic.exe). An exe name that names exactly one build is the stronger pick —
        // it settles the question outright further down — so it must win over an ambiguous name
        // (WoW.exe, shared by three builds), not just whichever ClientVersion.ExeNames lists first
        // (Codex Round 2: FirstOrDefault used to let list order pick WoW.exe arbitrarily).
        var candidates = ClientVersion.ExeNames.Where(n => File.Exists(Path.Combine(wowDirectory, n))).ToList();
        if (candidates.Count == 0) return null;
        var exeName = candidates.FirstOrDefault(n => ClientVersion.UniqueBuildForExeName(n) is not null)
            ?? candidates[0];
        var exe = Path.Combine(wowDirectory, exeName);

        try
        {
            // 1.12.1.5875 / 2.4.3.8606 / 3.3.5.12340 / 1.14.2.42597 — the file version carries the
            // build when the resource is present, and it is the STRONGER evidence: a real, differently
            // versioned client wearing the same exe name (e.g. a future WowClassic.exe build the
            // launcher does not know yet) must not be mislabelled via the name below.
            var fv = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
            var build = fv.FileBuildPart; // the 4th field (…​.<build>)
            if (build != 0)
            {
                // Version resource present and non-zero: authoritative. Known build → that's the
                // answer. Unknown build → an ACTIVE signal this is a different client than the exe
                // name suggests (Orchestrator Blocker 1) — the name must NOT override this; null.
                return KnownBuilds.Contains(build) ? build : null;
            }
            // Version resource present but zero (common for private-server exes) — as unreliable as
            // an unreadable one, fall through to the name/MPQ resolution below.
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "DetectBuild: version read failed for {Dir}", wowDirectory);
        }

        // Version was unreadable or zero. An exe name that names exactly one build (WowClassic.exe →
        // 42597) settles the question outright — the Data-MPQ heuristic below was written for
        // Vanilla/TBC/WotLK sharing WoW.exe and must never override an unambiguous name (Codex
        // Finding 2: it used to mislabel a 1.14.2 install with an unreadable exe version as 5875 via
        // the MPQ fallback). Only an ambiguous name (WoW.exe) still needs the MPQ heuristic.
        if (ClientVersion.UniqueBuildForExeName(exeName) is int uniqueBuild)
            return uniqueBuild;
        return InferBuildFromData(wowDirectory);
    }

    /// <summary>Heuristic build guess from the Data/ layout when the exe version is unusable.</summary>
    private static int? InferBuildFromData(string wowDirectory)
    {
        var data = Path.Combine(wowDirectory, "Data");
        if (!Directory.Exists(data)) return null;
        // WotLK ships lichking.MPQ / expansion.MPQ; TBC ships expansion.MPQ (no lichking);
        // Vanilla has neither. Locale dirs carry the same pattern but root MPQs suffice.
        // Last-resort heuristic (exe version unreadable) — TBC/WotLK also ship common/patch.MPQ,
        // so the order matters and a missing expansion artifact can misclassify. Best-effort only;
        // a real install registered via FileVersionInfo never reaches here.
        bool Has(string name) => File.Exists(Path.Combine(data, name));
        if (Has("lichking.MPQ")) return 12340;
        if (Has("expansion.MPQ"))
        {
            if (!Has("common-2.MPQ"))
                Serilog.Log.Warning("InferBuild: expansion.MPQ without common-2.MPQ in {Dir} — could be a WotLK install missing lichking.MPQ; guessing TBC (8606)", data);
            return 8606;
        }
        if (Has("common.MPQ") || Has("dbc.MPQ") || Has("patch.MPQ")) return 5875;
        return null;
    }

    /// <summary>A directory is a usable client iff it has any known client exe (see
    /// <see cref="ClientVersion.ExeNames"/> — not every build is WoW.exe, e.g. 1.14.2 ships
    /// WowClassic.exe) and a Data dir with at least one .MPQ.</summary>
    private static bool IsValidWowDir(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        if (!ClientVersion.ExeNames.Any(exe => File.Exists(Path.Combine(dir, exe)))) return false;
        var data = Path.Combine(dir, "Data");
        return Directory.Exists(data) &&
               Directory.EnumerateFiles(data, "*.MPQ", SearchOption.TopDirectoryOnly).Any();
    }

    public IReadOnlyDictionary<int, string> DetectInstalls(IReadOnlyDictionary<int, string> known)
    {
        var found = new Dictionary<int, string>();

        void Consider(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            try { dir = Path.GetFullPath(dir); } catch { return; }
            if (found.Values.Any(d => string.Equals(d, dir, StringComparison.OrdinalIgnoreCase))) return;
            if (!IsValidWowDir(dir)) return;
            var build = DetectBuild(dir);
            if (build is int b && !found.ContainsKey(b))
            {
                found[b] = dir;
                _logger.Information("Detected {Build} client at {Dir}", b, dir);
            }
        }

        // 1) Already-known installs from prior runs (cheap, authoritative).
        foreach (var dir in known.Values) Consider(dir);

        // 2) Common filesystem locations — private-server clients are loose folders, so this is
        //    the primary signal. Scan a curated set of roots + one level of named subfolders.
        foreach (var dir in CommonInstallDirs()) Consider(dir);

        // 3) Blizzard registry (retail installs). Windows-only; guarded so it no-ops elsewhere.
        if (OperatingSystem.IsWindows())
            foreach (var dir in RegistryInstallDirs()) Consider(dir);

        return found;
    }

    private IEnumerable<string> CommonInstallDirs()
    {
        // Roots + folder-name fragments come from the platform provider (Windows: drive roots +
        // C:\Games\…; Linux: neutral only until WP2 fills XDG/Wine/Steam roots).
        var roots = _roots.CommonInstallRoots();
        var names = _roots.InstallFolderNames();

        foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            // The root itself might be a client dir (e.g. launcher placed next to WoW.exe).
            yield return root;
            string[] subs;
            try { subs = Directory.Exists(root) ? Directory.GetDirectories(root) : []; }
            catch { subs = []; }
            // Named matches first (cheap), then any sub that simply contains a known client exe
            // (ClientVersion.ExeNames — a WowClassic.exe-only folder used to be invisible here).
            foreach (var sub in subs)
            {
                var leaf = Path.GetFileName(sub);
                if (names.Any(n => leaf.Contains(n, StringComparison.OrdinalIgnoreCase)))
                    yield return sub;
                else if (ClientVersion.ExeNames.Any(exe => File.Exists(Path.Combine(sub, exe))))
                    yield return sub;
            }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private IEnumerable<string> RegistryInstallDirs()
    {
        // Blizzard persists the install path under these keys; the value is InstallPath (retail)
        // or GamePath (older clients). WOW6432Node first — this 64-bit launcher reads the 32-bit
        // registry view explicitly to find 32-bit retail installs — then the plain HKLM key, then
        // the per-user HKCU install. (Hermes Q1: never use Uninstall keys.)
        (Microsoft.Win32.RegistryKey root, string sub)[] keys =
        [
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft"),
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Blizzard Entertainment\World of Warcraft"),
            (Microsoft.Win32.Registry.CurrentUser,  @"SOFTWARE\Blizzard Entertainment\World of Warcraft"),
        ];
        foreach (var (root, sub) in keys)
        {
            string? path = null;
            try
            {
                using var rk = root.OpenSubKey(sub);
                path = (rk?.GetValue("InstallPath") ?? rk?.GetValue("GamePath")) as string;
            }
            catch (Exception ex) { _logger.Debug(ex, "Registry read failed: {Key}", sub); }
            if (!string.IsNullOrWhiteSpace(path)) yield return path!;
        }
    }

    public bool IsGameRunning()
    {
        // Windows scan is path-agnostic (catches any WoW.exe, incl. player-started — Codex A1);
        // the Linux /proc detector wants the absolute path, so hand it a best-effort one.
        return _detector.IsGameRunning(ResolveKnownWowExe());
    }

    /// <summary>Cheapest absolute client exe path we can name from registered installs, or null.
    /// Only the Linux detector consumes it; the Windows detector ignores the argument. Tries every
    /// known exe name (<see cref="ClientVersion.ExeNames"/>) — a 1.14.2 install ships WowClassic.exe,
    /// not WoW.exe, and a wrong name here means the "is the game running?" guard silently never
    /// fires for that build, letting the launcher overwrite files the running game has open.</summary>
    private string? ResolveKnownWowExe()
    {
        try
        {
            foreach (var dir in _config.Load().ClientInstalls.Values)
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                foreach (var exeName in ClientVersion.ExeNames)
                {
                    var exe = Path.Combine(dir, exeName);
                    if (File.Exists(exe)) return Path.GetFullPath(exe);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "ResolveKnownWowExe failed");
        }
        return null;
    }

    /// <summary>
    /// The characters a realmlist address may contain: hostname/IPv4/IPv6 material plus an optional
    /// <c>:port</c>. Deliberately a whitelist, not a blacklist of bad characters.
    /// </summary>
    private static bool IsAddressChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':' or '[' or ']';

    /// <summary>
    /// True when <paramref name="address"/> is safe to write into a .wtf file: non-empty, no control
    /// characters, no whitespace, nothing but hostname/IP material. A line break here would append
    /// arbitrary extra directives to realmlist.wtf and Config.wtf, which the client then executes as
    /// configuration. Validated at the settings form AND here, because the value also arrives from the
    /// server manifest and from a hand-edited launcher_config.json.
    /// </summary>
    public static bool IsValidRealmlistAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        if (address.Length > 253) return false;              // longest legal DNS name
        foreach (var c in address)
            if (!IsAddressChar(c)) return false;
        return true;
    }

    /// <summary>
    /// The address to write, or an empty string when there is nothing safe to write. Surrounding
    /// whitespace is tolerated (a manifest field often carries it); anything else that fails validation
    /// is REFUSED rather than repaired. Stripping the bad characters instead would leave a mangled
    /// hostname in the file and the player would see a client that connects to nothing with no
    /// explanation. Keeping the previous realmlist and logging the cause is the honest outcome.
    /// </summary>
    private string SafeAddress(string? realmlistAddress)
    {
        var trimmed = realmlistAddress?.Trim() ?? "";
        if (IsValidRealmlistAddress(trimmed)) return trimmed;

        _logger.Error("Refusing the realmlist address: it is empty or contains characters that would " +
                      "become extra directives in a .wtf file (length {Len})", trimmed.Length);
        return "";
    }

    public void SetRealmlist(string wowDirectory, string realmlistAddress)
    {
        var realmlistPath = Path.Combine(wowDirectory, "realmlist.wtf");
        var address = SafeAddress(realmlistAddress);
        if (address.Length == 0)
        {
            _logger.Error("Refusing to write realmlist.wtf: the address is empty after validation");
            return;
        }

        try
        {
            // Backup existing realmlist
            if (File.Exists(realmlistPath))
                File.Copy(realmlistPath, realmlistPath + ".bak", overwrite: true);

            File.WriteAllText(realmlistPath, $"set realmlist {address}\n");
            _logger.Information("Realmlist set: {Addr} → {Path}", address, realmlistPath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to write realmlist.wtf");
        }
    }

    public void ConfigureClient(string wowDirectory, string locale, string realmlistAddress)
    {
        realmlistAddress = SafeAddress(realmlistAddress);
        if (realmlistAddress.Length == 0)
        {
            _logger.Error("Refusing to configure the client: the realmlist address is empty after validation");
            return;
        }
        // The locale names a directory and lands in Config.wtf; anything but plain letters is not a
        // locale and must not reach either.
        if (string.IsNullOrEmpty(locale) || !locale.All(char.IsAsciiLetter))
        {
            _logger.Warning("Locale {Locale} is not a plain locale code — falling back to enUS", locale);
            locale = "enUS";
        }

        try
        {
            // 1. WTF/Config.wtf — SET locale (language pack) + SET realmList (belt & suspenders).
            var configPath = Path.Combine(wowDirectory, "WTF", "Config.wtf");
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            SetWtfVar(configPath, "locale", locale);
            SetWtfVar(configPath, "realmList", realmlistAddress);

            // 2. Data/<locale>/realmlist.wtf — where TBC/WotLK read the realm first.
            var localeDir = Path.Combine(wowDirectory, "Data", locale);
            if (Directory.Exists(localeDir))
            {
                var rl = Path.Combine(localeDir, "realmlist.wtf");
                if (File.Exists(rl)) File.Copy(rl, rl + ".bak", overwrite: true);
                File.WriteAllText(rl, $"set realmlist {realmlistAddress}\n");
            }
            else
            {
                _logger.Warning("Locale dir missing — language pack not installed: {Dir}", localeDir);
            }

            // 3. Root realmlist.wtf too (Vanilla 1.12 reads it; harmless for TBC/WotLK).
            SetRealmlist(wowDirectory, realmlistAddress);
            _logger.Information("Client configured: locale={Locale} realm={Addr}", locale, realmlistAddress);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to configure client (locale={Locale})", locale);
        }
    }

    /// <summary>Set or replace a single <c>SET key "value"</c> line in a .wtf file, preserving the rest.
    /// The implementation lives in <see cref="WtfFile"/> because the 1.12.1 language switch writes into
    /// the same file and must use the same rule.</summary>
    private static void SetWtfVar(string path, string key, string value) =>
        WtfFile.SetVar(path, key, value);

    public async Task<GameLaunchResult> LaunchAsync(string wowExePath)
    {
        // Platform start lives behind IGameLauncher (Windows: native ProcessStart, byte-for-byte the
        // former inline path; Linux: WineGameLauncher).
        //
        // Codex F4a: pre-WP1 the WHOLE body (incl. Path.GetDirectoryName) sat inside one try/catch
        // that logged Fatal and returned a failure — an invalid path never threw out of LaunchAsync.
        // Guard GetDirectoryName so that catch-all semantics are restored (the launcher call itself
        // never throws — both platform impls catch internally and return a failed result).
        string dir;
        try
        {
            dir = Path.GetDirectoryName(wowExePath) ?? ".";
        }
        catch (Exception ex)
        {
            _logger.Fatal(ex, "Failed to launch WoW.exe");
            return GameLaunchResult.Failed(ex.Message);
        }

        return await _launcher.LaunchAsync(wowExePath, dir).ConfigureAwait(false);
    }
}
