namespace WowLauncher.Models;

using System.Text.Json.Serialization;

/// <summary>
/// Server manifest as served by downloads.stonetavern.app/manifest.json.
/// </summary>
public sealed class ServerManifest
{
    [JsonPropertyName("product")]
    public string Product { get; set; } = "stonetavern-classic";

    [JsonPropertyName("current_version")]
    public string CurrentVersion { get; set; } = "1.0.0";

    [JsonPropertyName("build_date")]
    public string BuildDate { get; set; } = "";

    [JsonPropertyName("base")]
    public ManifestFile? Base { get; set; }

    [JsonPropertyName("patches")]
    public List<ManifestPatch> Patches { get; set; } = [];

    [JsonPropertyName("launcher")]
    public ManifestFile? Launcher { get; set; }

    /// <summary>
    /// Optional Linux launcher build (WP4). Same shape as <see cref="Launcher"/> (version/url/sha256/size)
    /// but a SEPARATE top-level field — never nested under <c>launcher</c> — so the existing Windows
    /// deserialisation is untouched. Absent = the server hasn't published a Linux build yet: the Linux
    /// launcher then shows no update hint and raises no error. The Windows path IGNORES this field.
    /// V1 is Check + Notify only (no auto-apply until artefact signing, PLAN §1.5 / WP7).
    /// </summary>
    [JsonPropertyName("launcher_linux")]
    public ManifestFile? LauncherLinux { get; set; }

    /// <summary>
    /// Slug of the globally active progression phase (see <see cref="Progression"/>).
    /// The launcher resolves which client build the player needs from this.
    /// Empty/null = server hasn't announced a phase yet → launcher falls back to Vanilla.
    /// </summary>
    [JsonPropertyName("active_phase")]
    public string ActivePhase { get; set; } = "";

    /// <summary>
    /// Per-phase realm + client-download coordinates. The launcher only needs the
    /// entry whose <see cref="PhaseManifest.Phase"/> matches <see cref="ActivePhase"/>,
    /// but the server may list all phases so the launcher can pre-stage upcoming clients.
    /// </summary>
    [JsonPropertyName("phases")]
    public List<PhaseManifest> Phases { get; set; } = [];
}

/// <summary>
/// Server-supplied realm + client coordinates for a single <see cref="ProgressionPhase"/>.
/// The client <em>identity</em> (version, gamebuild) is launcher-side canon
/// (<see cref="Progression"/>); the server only supplies the volatile bits:
/// where the realm lives and where to fetch the matching client build.
/// </summary>
public sealed class PhaseManifest
{
    /// <summary>Matches <see cref="ProgressionPhase.Slug"/>.</summary>
    [JsonPropertyName("phase")]
    public string Phase { get; set; } = "";

    /// <summary>realmlist.wtf address for this realm, e.g. "vanilla.play.stonetavern.app".</summary>
    [JsonPropertyName("realmlist")]
    public string Realmlist { get; set; } = "";

    /// <summary>The downloadable client build for this phase (null = player brings their own).
    /// This is the phase's CANONICAL build - vanilla means 5875 - and it is the only build this field
    /// can ever describe. A realm that also speaks a second client (1.14.2 on a vanilla realm) needs
    /// <see cref="Clients"/>.</summary>
    [JsonPropertyName("client")]
    public ManifestFile? Client { get; set; }

    /// <summary>
    /// Per-build download coordinates, for a phase that offers more than one client. Each entry names
    /// the build it is for (<see cref="ManifestFile.Build"/>); order does not matter.
    ///
    /// <para><b>Additive.</b> Absent in every manifest published before this field existed, and absent
    /// is not a special case: <see cref="ClientForBuild"/> then falls back to <see cref="Client"/> for
    /// the canonical build and to null for anything else, which is exactly the behaviour that shipped
    /// before. So an old manifest keeps working unchanged, and a launcher older than this field simply
    /// ignores it.</para>
    ///
    /// <para><b>Why not just add a second phase.</b> A phase is server progression - which content is
    /// live - and both Vanilla clients play the SAME phase. Modelling 1.14.2 as its own phase would
    /// claim a progression step that does not exist and would move every realm onto it.</para>
    /// </summary>
    [JsonPropertyName("clients")]
    public List<ManifestFile> Clients { get; set; } = [];

    /// <summary>
    /// Download coordinates for exactly <paramref name="build"/>, or null when the server publishes
    /// none for it (the player brings their own client for that build).
    ///
    /// <para>The fallback to <see cref="Client"/> is deliberately narrow: it applies ONLY when the
    /// requested build IS <paramref name="canonicalBuild"/>. Handing the phase's 5875 zip to a 42597
    /// request would download and extract the wrong client over a working install - the exact silent
    /// failure the earlier hard guard existed to prevent, which this method replaces rather than
    /// loosens.</para>
    /// </summary>
    public ManifestFile? ClientForBuild(int build, int canonicalBuild)
    {
        // An entry naming THIS os wins over an os-neutral one, so a server can publish a generic
        // package and override it per platform without removing either.
        var candidates = Clients.Where(c => c.Build == build && c.MatchesCurrentOs()).ToList();
        var exact = candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Os))
                    ?? candidates.FirstOrDefault();
        if (exact is not null) return exact;

        // A build that IS published but only for other platforms must come back as "nothing here",
        // never fall through to the phase's canonical package - that fallback exists for a manifest
        // that predates per-build entries, not as a consolation prize for the wrong OS.
        if (Clients.Any(c => c.Build == build)) return null;

        return build == canonicalBuild ? Client : null;
    }
}

public sealed class ManifestFile
{
    /// <summary>
    /// The client build these coordinates are for, e.g. 5875 or 42597. Only meaningful inside
    /// <see cref="PhaseManifest.Clients"/>; null everywhere else (<c>base</c>, <c>launcher</c>,
    /// <c>launcher_linux</c> and the singular <c>client</c> have no build of their own).
    ///
    /// <para>Null is never treated as "matches any build" - <see cref="PhaseManifest.ClientForBuild"/>
    /// compares for equality, so an entry without a build simply never matches. An entry that failed to
    /// say which client it is for must not be handed to whichever client happens to ask.</para>
    /// </summary>
    [JsonPropertyName("build")]
    public int? Build { get; set; }

    /// <summary>
    /// The operating system this package is for: <c>"windows"</c> or <c>"linux"</c>. Null (the normal
    /// case) means the package works everywhere.
    ///
    /// <para>Needed because the modern 1.14.2 client ships as TWO packages that are not
    /// interchangeable: the Windows one carries the Windows proxy, the Linux one carries a native
    /// ELF proxy under <c>Hermes/linux</c>. A Linux player handed the Windows package gets an install
    /// the launcher then refuses to start, having downloaded several gigabytes first. The 1.12.1
    /// client has no such split - it is one Windows package that runs under Wine - which is why this
    /// is optional rather than required.</para>
    /// </summary>
    [JsonPropertyName("os")]
    public string? Os { get; set; }

    /// <summary>True if this package is usable on the machine the launcher is running on.</summary>
    public bool MatchesCurrentOs()
    {
        if (string.IsNullOrWhiteSpace(Os)) return true;   // no restriction stated
        if (Os.Equals("windows", StringComparison.OrdinalIgnoreCase)) return OperatingSystem.IsWindows();
        if (Os.Equals("linux", StringComparison.OrdinalIgnoreCase)) return OperatingSystem.IsLinux();
        if (Os.Equals("macos", StringComparison.OrdinalIgnoreCase)) return OperatingSystem.IsMacOS();
        // An OS this launcher does not know is NOT a match. Guessing "probably fine" here would hand a
        // player a package built for something else.
        return false;
    }

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    /// <summary>
    /// OPTIONAL per-file manifest (Phase 1 repair-without-redownload, <c>deploy/MANIFEST-SCHEMA.md</c>
    /// §files_url). Absent/null = exactly today's behaviour: Repair always re-downloads and
    /// re-extracts the whole ZIP named by <see cref="Url"/>/<see cref="Sha256"/> above. Present =
    /// <see cref="Services.IClientVerifyService"/> can check the existing install file-by-file first
    /// and only fall back to the full ZIP when something actually is missing or corrupt.
    /// </summary>
    [JsonPropertyName("files_url")]
    public string? FilesUrl { get; set; }
}

public sealed class ManifestPatch
{
    [JsonPropertyName("from")]
    public string From { get; set; } = "";

    [JsonPropertyName("to")]
    public string To { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("changelog")]
    public List<string> Changelog { get; set; } = [];
}

[JsonSerializable(typeof(ServerManifest))]
internal partial class ServerManifestContext : JsonSerializerContext { }
