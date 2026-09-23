namespace WowLauncher.Services;

using System.Reflection;

/// <summary>
/// Where THIS launcher build fetches its own next version from — and the one rule about it: the
/// player's configuration does not get a vote.
///
/// <para><b>What went wrong without it.</b> <see cref="ManifestSignatureGate"/> read
/// <c>LauncherConfig.ManifestUrl</c>, which is the manifest of the realm the player currently has
/// selected. <see cref="ManifestService.FetchLauncherManifestAsync"/> had already learned better and
/// used a constant, with the reason written next to it, but the signature gate — the seam that
/// decides whether a launcher update happens at all — still asked the config. A player who added
/// their own realm therefore had a manifest URL that is not ours (or none at all, which is legal in
/// simple mode), the gate fetched THAT document, its signature did not verify against our release
/// key, and the gate returned null. Null means "do nothing". So the launcher stopped updating
/// itself, forever, and said so only in a log file nobody reads. That is the worst shape a bug can
/// have: it disables the mechanism that would have shipped its own fix.</para>
///
/// <para><b>Why it is a class and not a constant.</b> A second, identical download origin has to
/// exist for testing end to end — the same manifest, the same signature check, the same swap, just
/// not in front of real players. That origin is chosen when the build is PACKAGED
/// (<c>-p:StonetavernChannel=beta</c>), not when it runs, so a beta launcher and a stable launcher
/// are two different artefacts and can never be confused for one another at a player's machine.
/// <see cref="EnvironmentOverride"/> exists on top of that for a developer pointing a local build at
/// a local server; it is announced in the log every time it takes effect, because a launcher that
/// updates itself from somewhere unexpected must never do so quietly.</para>
/// </summary>
public static class LauncherChannel
{
    /// <summary>The live origin. Every player who did not deliberately install a beta build uses it.</summary>
    public const string StableBaseUrl = "https://downloads.stonetavern.app";

    /// <summary>The 1:1 copy of the live origin, serving the same file names with the same signatures,
    /// used to rehearse a release before it reaches anyone.</summary>
    public const string BetaBaseUrl = "https://beta-downloads.stonetavern.app";

    /// <summary>Set this to a base URL to point a locally built launcher at a local server. Ignored
    /// unless it parses as an absolute http(s) address.</summary>
    public const string EnvironmentOverride = "STONETAVERN_DOWNLOAD_BASE";

    // One lazy value for both facts, so IsBeta can never be read before it has been decided —
    // a separate bool assigned inside Resolve() would answer "stable" to whoever asked first.
    private static readonly Lazy<(string Url, bool Beta)> _resolved = new(Resolve);

    /// <summary>Base address of this build's download origin, without a trailing slash.</summary>
    public static string BaseUrl => _resolved.Value.Url;

    /// <summary>The manifest this build updates ITSELF from. Not the realm's manifest.</summary>
    public static string ManifestUrl => BaseUrl + "/manifest.json";

    /// <summary>"stable" or "beta" — shown in the UI so a tester can see which build they are on.</summary>
    public static string Name => IsBeta ? "beta" : "stable";

    /// <summary>True when this artefact was packaged against the rehearsal origin.</summary>
    public static bool IsBeta => _resolved.Value.Beta;

    private static (string Url, bool Beta) Resolve()
    {
        var packaged = typeof(LauncherChannel).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, "StonetavernChannel", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        var beta = string.Equals(packaged?.Trim(), "beta", StringComparison.OrdinalIgnoreCase);
        var packagedUrl = beta ? BetaBaseUrl : StableBaseUrl;
        var resolved = packagedUrl;

        var env = Environment.GetEnvironmentVariable(EnvironmentOverride);
        if (!string.IsNullOrWhiteSpace(env) && ManifestService.IsValidManifestUrl(env))
        {
            resolved = env.Trim().TrimEnd('/');
            Serilog.Log.Warning(
                "{Var} is set — this launcher updates itself from {Url} instead of {Default}",
                EnvironmentOverride, resolved, packagedUrl);
        }
        else if (!string.IsNullOrWhiteSpace(env))
        {
            Serilog.Log.Error("{Var}={Value} is not an absolute http(s) address — ignored, staying on {Url}",
                EnvironmentOverride, env, resolved);
        }

        return (resolved.TrimEnd('/'), beta);
    }
}
