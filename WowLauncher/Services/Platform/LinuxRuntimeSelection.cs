namespace WowLauncher.Services.Platform;

/// <summary>What the player asked to be started with. Stored in the config as the string value.</summary>
public enum LinuxRuntimeKind
{
    /// <summary>Let the launcher decide, per client: 1.12.1 on the system Wine, 1.14.2 on a wine-ge
    /// runner when there is one. That is exactly what every build before this did, without saying so —
    /// so nobody who leaves this alone sees a changed start.</summary>
    Auto,

    /// <summary>The <c>wine</c> on PATH, whatever the distribution ships.</summary>
    SystemWine,

    /// <summary>The newest Lutris <c>wine-ge</c> runner. Ships vkd3d/DXVK inside the runner, which is
    /// the configuration the modern client has the most evidence behind.</summary>
    WineGe,

    /// <summary>A binary the player names. This is also the way to a Proton build: point it at that
    /// build's <c>wine</c>. See the class remarks for why Proton is not its own entry.</summary>
    Custom,
}

/// <summary>
/// The runtime the launcher will actually use, and — the part that matters — <b>why</b>.
/// </summary>
/// <param name="Kind">What is really being used, after any fallback.</param>
/// <param name="Path">Absolute path or bare name of the binary. Empty when nothing was found.</param>
/// <param name="Requested">What the player asked for.</param>
/// <param name="Reason">Machine-readable reason, resolved to a sentence by the UI layer.</param>
public sealed record LinuxRuntimeDecision(
    LinuxRuntimeKind Kind,
    string Path,
    LinuxRuntimeKind Requested,
    LinuxRuntimeReason Reason)
{
    /// <summary>True when the player asked for one thing and gets another. The player is TOLD about
    /// this — a silent fallback is how someone ends up debugging a setting that is not in effect.</summary>
    public bool FellBack => Kind != Requested && Requested != LinuxRuntimeKind.Auto;

    /// <summary>Nothing to start with. Not an error here; the caller decides what to say.</summary>
    public bool Found => Path.Length > 0;
}

/// <summary>Why the decision came out the way it did. One value per sentence the player can be shown.</summary>
public enum LinuxRuntimeReason
{
    /// <summary>The requested runtime was there and is being used.</summary>
    AsRequested,
    /// <summary>Automatic pick: a Lutris wine-ge runner was found.</summary>
    AutoPickedWineGe,
    /// <summary>Automatic pick: no wine-ge, so the system Wine.</summary>
    AutoPickedSystemWine,
    /// <summary>Asked for wine-ge, none installed, using the system Wine instead.</summary>
    WineGeMissing,
    /// <summary>Asked for a custom binary that is not there or not executable.</summary>
    CustomMissing,
    /// <summary>There is no Wine on this machine at all.</summary>
    NothingFound,
}

/// <summary>
/// Which Wine the launcher starts the game with on Linux, from the player's setting rather than from
/// a rule nobody can see.
///
/// <para><b>Why this exists (owner, 2026-08-04 and again 2026-08-05).</b> The launcher already had all
/// the machinery — <see cref="WineGeLocator"/>, <see cref="ModernWineRuntime"/>, a system-Wine
/// fallback — and made the choice silently. A player whose game starts wrong could neither see what
/// was used nor change it. The setting is half of the fix; the other half is that the launcher now
/// SAYS what it is using, including when it fell back to something other than what was asked for.</para>
///
/// <para><b>Why Proton is not its own entry.</b> There is a <c>UmuGameLauncher</c>, and it is
/// deliberately not in the modern client's path: it starts the client exe on its own, and the modern
/// client only reaches this realm through the proxy the modern launcher owns — so it produced a login
/// screen in front of a realm that was never contacted. Offering a Proton button that ends there would
/// be a control that lies. A Proton build's own <c>wine</c> binary can still be selected through
/// <see cref="LinuxRuntimeKind.Custom"/>, which is honest about what it does: it hands that binary the
/// same job the system Wine would get.</para>
///
/// <para>Pure by construction: the two lookups and the executable test are seams, so the whole
/// decision table is provable without depending on what happens to be installed on the dev box.</para>
/// </summary>
public static class LinuxRuntimeSelection
{
    /// <summary>Config value ⇄ enum. Unknown or empty reads as <see cref="LinuxRuntimeKind.Auto"/>,
    /// because a config written by a newer launcher must not brick an older one.</summary>
    public static LinuxRuntimeKind Parse(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "system" or "system-wine" => LinuxRuntimeKind.SystemWine,
        "wine-ge" or "winege" => LinuxRuntimeKind.WineGe,
        "custom" => LinuxRuntimeKind.Custom,
        _ => LinuxRuntimeKind.Auto,
    };

    /// <summary>The stored spelling. Kept stable: it lands in a config file players keep.</summary>
    public static string ToConfigValue(LinuxRuntimeKind kind) => kind switch
    {
        LinuxRuntimeKind.SystemWine => "system",
        LinuxRuntimeKind.WineGe => "wine-ge",
        LinuxRuntimeKind.Custom => "custom",
        _ => "auto",
    };

    /// <summary>
    /// Decide, from the preference and what the machine has. Never throws, never probes anything
    /// expensive: the functional Wine probe stays where it was, in
    /// <see cref="WineGameLauncher"/>, and runs on whatever this picked.
    /// </summary>
    /// <param name="autoPrefersWineGe">What <see cref="LinuxRuntimeKind.Auto"/> means for THIS client,
    /// and it is not the same for both. 1.14.2 wants wine-ge first: the runner ships vkd3d/DXVK and is
    /// the configuration the modern client is proven on. 1.12.1 wants the system Wine, because it is a
    /// 32-bit binary and a wine-ge runner is frequently an x86_64-only build with no 32-bit support at
    /// all.
    ///
    /// <para>🔴 This parameter exists because leaving it out was a live regression, caught on
    /// 2026-08-05 by the very status line this feature adds: with one shared Auto, the settings page
    /// read "In use: .../wine-ge-8-26-x86_64/bin/wine" for the 1.12.1 client, which every build before
    /// had started on plain <c>wine</c>. A setting whose default silently changes how the game starts
    /// is worse than no setting.</para></param>
    public static LinuxRuntimeDecision Resolve(
        LinuxRuntimeKind requested,
        string? customPath,
        Func<string?> wineGe,
        Func<string?> systemWine,
        Func<string, bool> isExecutable,
        bool autoPrefersWineGe)
    {
        switch (requested)
        {
            case LinuxRuntimeKind.Custom:
            {
                var path = (customPath ?? "").Trim();
                if (path.Length > 0 && isExecutable(path))
                    return new(LinuxRuntimeKind.Custom, path, requested, LinuxRuntimeReason.AsRequested);
                // A named binary that is not there is the one case where falling back quietly would be
                // worst: the player picked THAT build for a reason. Fall back anyway - a game that
                // starts beats a refusal - but the reason says which, and the UI shows it.
                var replacement = Auto(wineGe, systemWine, autoPrefersWineGe);
                return replacement.Found
                    ? replacement with { Requested = requested, Reason = LinuxRuntimeReason.CustomMissing }
                    : new(LinuxRuntimeKind.Custom, "", requested, LinuxRuntimeReason.NothingFound);
            }

            case LinuxRuntimeKind.WineGe:
            {
                var ge = wineGe();
                if (!string.IsNullOrEmpty(ge))
                    return new(LinuxRuntimeKind.WineGe, ge, requested, LinuxRuntimeReason.AsRequested);
                var sys = systemWine();
                return string.IsNullOrEmpty(sys)
                    ? new(LinuxRuntimeKind.WineGe, "", requested, LinuxRuntimeReason.NothingFound)
                    : new(LinuxRuntimeKind.SystemWine, sys, requested, LinuxRuntimeReason.WineGeMissing);
            }

            case LinuxRuntimeKind.SystemWine:
            {
                var sys = systemWine();
                if (!string.IsNullOrEmpty(sys))
                    return new(LinuxRuntimeKind.SystemWine, sys, requested, LinuxRuntimeReason.AsRequested);
                // Asked for the system Wine and there is none. wine-ge is not a substitute a player
                // asked for, but it IS a working Wine, and refusing here would strand somebody who has
                // Lutris and no distribution package.
                var ge = wineGe();
                return string.IsNullOrEmpty(ge)
                    ? new(LinuxRuntimeKind.SystemWine, "", requested, LinuxRuntimeReason.NothingFound)
                    : new(LinuxRuntimeKind.WineGe, ge, requested, LinuxRuntimeReason.AutoPickedWineGe);
            }

            default:
                return Auto(wineGe, systemWine, autoPrefersWineGe);
        }
    }

    private static LinuxRuntimeDecision Auto(
        Func<string?> wineGe, Func<string?> systemWine, bool prefersWineGe)
    {
        var first = prefersWineGe ? wineGe() : systemWine();
        if (!string.IsNullOrEmpty(first))
            return prefersWineGe
                ? new(LinuxRuntimeKind.WineGe, first, LinuxRuntimeKind.Auto, LinuxRuntimeReason.AutoPickedWineGe)
                : new(LinuxRuntimeKind.SystemWine, first, LinuxRuntimeKind.Auto, LinuxRuntimeReason.AutoPickedSystemWine);

        var second = prefersWineGe ? systemWine() : wineGe();
        if (string.IsNullOrEmpty(second))
            return new(LinuxRuntimeKind.Auto, "", LinuxRuntimeKind.Auto, LinuxRuntimeReason.NothingFound);

        return prefersWineGe
            ? new(LinuxRuntimeKind.SystemWine, second, LinuxRuntimeKind.Auto, LinuxRuntimeReason.AutoPickedSystemWine)
            : new(LinuxRuntimeKind.WineGe, second, LinuxRuntimeKind.Auto, LinuxRuntimeReason.AutoPickedWineGe);
    }

    /// <summary>The decision for this machine and this config. Off Linux it reports nothing found:
    /// Windows starts the client natively and macOS resolves its own Wine through GPTK, and a setting
    /// that appears to apply on those platforms would be a third lie in a class of bug this file
    /// exists to remove.</summary>
    public static LinuxRuntimeDecision ForCurrentUser(
        string? configured, string? customPath, bool autoPrefersWineGe = false) =>
        OperatingSystem.IsLinux()
            ? Resolve(
                Parse(configured), customPath,
                WineGeLocator.FindLatestForCurrentUser,
                () => ModernWineRuntime.FindSystemWine(Environment.GetEnvironmentVariable("PATH")),
                IsExecutableFile,
                autoPrefersWineGe)
            : new(LinuxRuntimeKind.Auto, "", LinuxRuntimeKind.Auto, LinuxRuntimeReason.NothingFound);

    /// <summary>Found AND executable. A file that exists but cannot be run must not count as found —
    /// handing it to a process start turns a wrong setting into an unexplained launch failure.</summary>
    internal static bool IsExecutableFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return true;
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch
        {
            return false;
        }
    }
}
