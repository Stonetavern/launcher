using System;

namespace WowLauncher.Models;

/// <summary>
/// A WoW client build the launcher knows how to install, detect and point at a realm.
///
/// <para>This is the launcher's <em>vocabulary</em>, not a server progression: it says nothing about
/// which realm runs what. A realm names the client it needs (<see cref="RealmEntry.ClientKey"/>) and
/// the launcher resolves the install for that build. That separation is what makes the launcher usable
/// for any server, not only Stonetavern.</para>
///
/// <para><b>Era vs version.</b> Players think in eras (Vanilla, Burning Crusade, Wrath), but an era can
/// have more than one client: Vanilla is both <c>1.12.1</c> (build 5875, the classic private server
/// build) and <c>1.14.2</c> (build 42597, the Classic Era client; 44833 is 1.14.3). Both are Vanilla and both must say
/// exactly which one they are, or a player points a 1.14 client at a 1.12 realm and gets a wall of
/// nothing. Hence <see cref="Era"/> for grouping and <see cref="PreciseLabel"/> for the truth.</para>
/// </summary>
public sealed record ClientVersion(
    string Key,        // stable id used in config, e.g. "1.14.2"
    string EraId,      // "vanilla" | "tbc" | "wotlk" - groups the picker
    string Era,        // "Vanilla"
    string Version,    // "1.14.2"
    int Build,         // 42597
    string Note,       // "" or a qualifier that distinguishes siblings, e.g. "Classic Era"
    string ExeName,    // the executable to look for - NOT always WoW.exe
                       // IsAvailable means "there is a client the launcher can actually ship/detect/start today".
                       // Burning Crusade and Wrath are vocabulary the model already carries for later - EraId/Era exist
                       // so the rest of the app (progression, realm labels) can talk about them - but they are not an
                       // offer. Flip this the day a real 2.4.3 / 3.3.5a build is wired up end to end, not before.
    bool IsAvailable = true,
    // NeedsModernRuntime separates the two RUNTIME shapes, which is a different question from era or
    // availability: 1.12/2.4/3.3 are 32-bit D3D9 PEs that run on plain system Wine, while 1.14.2 is a
    // 64-bit client that renders through D3D12 (vkd3d) and needs a Wine build carrying it. Every
    // per-build runtime decision on Linux reads THIS flag - the launcher router, the readiness probe
    // (32-bit syswow64 check vs D3D12/Vulkan check) and the start path (Arctium + Config.wtf). Before
    // this existed the router matched a hardcoded exe name, which silently makes the wrong choice the
    // day a second modern build ships.
    bool NeedsModernRuntime = false)
{
    /// <summary>Compact machine truth: <c>1.14.2 (42597)</c>.</summary>
    public string ShortLabel => $"{Version} ({Build})";

    /// <summary>What the UI shows when it must be unambiguous: <c>1.14.2 (42597) · Classic Era</c>.</summary>
    public string PreciseLabel => Note.Length == 0 ? ShortLabel : $"{ShortLabel}  ·  {Note}";

    /// <summary>Era plus version, for a one-line description: <c>Vanilla 1.14.2</c>.</summary>
    public string DisplayName => $"{Era} {Version}";

    public static readonly IReadOnlyList<ClientVersion> All =
    [
        new("1.12.1", "vanilla", "Vanilla",                "1.12.1", 5875,  "",            "WoW.exe"),
        // Build read off the shipped client, not from memory: 1.14.2.42597 (1.14.3 is the 44833 one).
        // The Classic Era client is also NOT called WoW.exe, which is why ExeName exists at all - a
        // wrong name here means the launcher silently never finds the install.
        new("1.14.2", "vanilla", "Vanilla",                "1.14.2", 42597, "Classic Era", "WowClassic.exe",
            NeedsModernRuntime: true),
        new("2.4.3",  "tbc",     "Burning Crusade",        "2.4.3",  8606,  "",            "WoW.exe", IsAvailable: false),
        new("3.3.5a", "wotlk",   "Wrath of the Lich King", "3.3.5a", 12340, "",            "WoW.exe", IsAvailable: false),
    ];

    public static ClientVersion Default => All[0];

    /// <summary>True if this build has NO working path on the given OS and must not be offered AT ALL —
    /// a HARD exclusion, distinct from <see cref="IsAvailable"/> (which greys a "coming soon" build but
    /// still lists it). macOS runs ONLY the 64-bit modern client (owner scope 2026-07-23: no 1.12 on the
    /// Mac — the 32-bit D3D9 builds have no path under the free GPTK-Wine the Mac uses), so every
    /// non-modern build is excluded there. Windows and Linux exclude nothing. Takes the OS flag as a
    /// parameter so the rule is unit-testable off the current host.</summary>
    public bool IsExcludedOn(bool isMacOs) => isMacOs && !NeedsModernRuntime;

    /// <summary>The exclusion rule against the current OS. See <see cref="IsExcludedOn"/>.</summary>
    public bool IsExcludedOnCurrentOs => IsExcludedOn(OperatingSystem.IsMacOS());

    public static ClientVersion ByKey(string? key) =>
        All.FirstOrDefault(c => c.Key == key) ?? Default;

    /// <summary>Look a client up by the build number read out of an installed executable.</summary>
    public static ClientVersion? ByBuild(int build) => All.FirstOrDefault(c => c.Build == build);

    /// <summary>Every build the launcher recognises.</summary>
    public static IReadOnlyList<int> KnownBuilds => All.Select(c => c.Build).ToList();

    /// <summary>Every executable name a WoW install might use, deduped. Used when scanning a folder
    /// whose build is not known yet.</summary>
    public static IReadOnlyList<string> ExeNames =>
        All.Select(c => c.ExeName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The build an exe name identifies UNAMBIGUOUSLY, or null. <c>WowClassic.exe</c> maps
    /// to exactly one build (42597) and settles the question outright; <c>WoW.exe</c> is shared by
    /// three builds (5875/8606/12340) and stays ambiguous by name alone — callers must keep falling
    /// back to a version read / Data-MPQ heuristic for that case. Exists so an unreadable exe version
    /// can never demote an unambiguously-named client (e.g. WowClassic.exe) to the wrong build via a
    /// heuristic that was never meant to cover it (Codex Finding 2).</summary>
    public static int? UniqueBuildForExeName(string exeName)
    {
        var matches = All.Where(c => string.Equals(c.ExeName, exeName, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0].Build : null;
    }

    /// <summary>True if <paramref name="exeName"/> is EVER shipped as <paramref name="build"/> — e.g.
    /// <c>WoW.exe</c> can be 5875/8606/12340 but never 42597, and <c>WowClassic.exe</c> can only ever
    /// be 42597. Used by the stale-registration fallback in <see cref="WowLauncher.Services.ClientService.FindWowExeForBuild"/>:
    /// it may only accept a differently-named exe than the one <paramref name="build"/> ships as when
    /// that name is at least CONSISTENT with the requested build, never one that names a build the
    /// requested build could never be (Orchestrator Blocker 2 — starting the wrong client for a realm).</summary>
    public static bool ExeNameCanBeBuild(string exeName, int build) =>
        All.Any(c => string.Equals(c.ExeName, exeName, StringComparison.OrdinalIgnoreCase) && c.Build == build);

    /// <summary>True if an executable of this name belongs to a build that needs the modern runtime
    /// (64-bit, D3D12/vkd3d). Answers the Linux routing question from the ONE fact that decides it -
    /// <see cref="NeedsModernRuntime"/> - rather than from a hardcoded build number. A name shared by
    /// several builds (<c>WoW.exe</c>) is modern only if one of those builds is, which today is never;
    /// the day that changes the router must be handed the resolved build, not the name.</summary>
    public static bool ExeNameNeedsModernRuntime(string exeName) =>
        All.Any(c => string.Equals(c.ExeName, exeName, StringComparison.OrdinalIgnoreCase)
                     && c.NeedsModernRuntime);

    /// <summary>The versions of one era, in release order. More than one means the UI has to say
    /// which is which rather than just "Vanilla".</summary>
    public static IReadOnlyList<ClientVersion> OfEra(string eraId) =>
        All.Where(c => c.EraId == eraId).ToList();

    /// <summary>An era is offered in the picker if at least one of its builds is available. The single
    /// source for that question - nothing else re-derives it from a separate list.</summary>
    public static bool EraIsAvailable(string eraId) => OfEra(eraId).Any(c => c.IsAvailable);

    /// <summary>Era ids in progression order, each with its versions. Drives the grouped picker.</summary>
    public static IReadOnlyList<(string EraId, string Era, IReadOnlyList<ClientVersion> Versions)> ByEra =>
    [
        ("vanilla", "Vanilla",                OfEra("vanilla")),
        ("tbc",     "Burning Crusade",        OfEra("tbc")),
        ("wotlk",   "Wrath of the Lich King", OfEra("wotlk")),
    ];
}
