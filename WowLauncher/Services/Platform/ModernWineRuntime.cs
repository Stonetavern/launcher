namespace WowLauncher.Services.Platform;

/// <summary>The Wine build the modern client will be started with, and where it came from.</summary>
/// <param name="Path">Absolute path to the wine binary.</param>
/// <param name="IsWineGe">True for a Lutris wine-ge runner, false for the system Wine.</param>
public sealed record ModernWineChoice(string Path, bool IsWineGe);

/// <summary>
/// Which Wine the modern (1.14.2) client is started with: the newest Lutris <c>wine-ge</c> runner when
/// the player has one, otherwise the system Wine.
///
/// <para><b>Why the system Wine is in here at all (measured 2026-08-03).</b> Until now the launcher
/// REFUSED to start the modern client without wine-ge, on the reasoning that wine-ge is the build the
/// client is proven on. The reasoning was right and the conclusion was wrong: the bundle's own
/// <c>Play Stonetavern.sh</c> has always fallen back to <c>command -v wine</c>, and that is the script
/// a player used successfully on 2026-07-26 while the launcher beside it refused. Re-measured here on
/// Fedora with wine 11.0 (Staging), no wine-ge involved: the proxy bound 127.0.0.1:1119, Arctium
/// patched the client, and <c>WowClassic.exe</c> came up. So a refusal costs a player a working game
/// for a runner they do not need.</para>
///
/// <para>wine-ge stays FIRST because it ships vkd3d/DXVK in the runner, which is the configuration with
/// the most evidence behind it. The system Wine is second, not equal. Nothing at all is still a
/// refusal — starting the client without a Wine is not a thing.</para>
/// </summary>
public static class ModernWineRuntime
{
    /// <summary>The wine binary name looked for on PATH — the same one the bundle's script resolves with
    /// <c>command -v wine</c>. Deliberately not <c>wine64</c>: on a modern multilib Wine, <c>wine</c> is
    /// the entry point and <c>wine64</c> is a legacy alias that some distributions no longer ship.</summary>
    private const string SystemWineName = "wine";

    /// <summary>Pure core: pick a runtime from the two candidate sources, wine-ge first. Both are
    /// seams so this is provable without depending on what happens to be installed on the dev box.</summary>
    public static ModernWineChoice? Resolve(Func<string?> wineGe, Func<string?> systemWine)
    {
        var ge = wineGe();
        if (!string.IsNullOrEmpty(ge)) return new ModernWineChoice(ge, IsWineGe: true);

        var system = systemWine();
        return string.IsNullOrEmpty(system) ? null : new ModernWineChoice(system, IsWineGe: false);
    }

    /// <summary>The runtime for this machine, or null when there is no Wine at all. Null off Linux:
    /// the other platforms resolve their own Wine (macOS through GPTK) and must not pick one up here.</summary>
    public static ModernWineChoice? ResolveForCurrentUser() =>
        OperatingSystem.IsLinux()
            ? Resolve(WineGeLocator.FindLatestForCurrentUser, () => FindSystemWine(Environment.GetEnvironmentVariable("PATH")))
            : null;

    /// <summary>The first executable <c>wine</c> on <paramref name="pathVariable"/>, or null. Mirrors
    /// what a shell's <c>command -v</c> answers, including the "found but not executable" case, which
    /// must NOT count as found — handing an unexecutable file to a process start would turn a missing
    /// Wine into an unexplained launch failure.</summary>
    internal static string? FindSystemWine(string? pathVariable, Func<string, bool>? isExecutable = null)
    {
        if (string.IsNullOrEmpty(pathVariable)) return null;
        isExecutable ??= DefaultIsExecutable;

        foreach (var dir in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try { candidate = Path.Combine(dir, SystemWineName); }
            catch (ArgumentException) { continue; }   // a malformed PATH entry is skipped, not fatal
            if (isExecutable(candidate)) return candidate;
        }
        return null;
    }

    private static bool DefaultIsExecutable(string path)
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
