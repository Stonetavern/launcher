namespace WowLauncher.Services.Platform;

/// <summary>
/// Is the ICU library the .NET proxies need for globalization installed?
///
/// <para><b>Why (matrix 2026-09-24).</b> JimsProxy and HermesProxy are self-contained .NET builds. On a
/// Linux without <c>libicu</c> (a minimal Debian, a trimmed install) they abort at startup with
/// "Couldn't find a valid ICU package", the port never opens and the player gets no login. When ICU is
/// missing the launcher starts the proxy with <c>DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1</c>: the proxy
/// only formats numbers and parses its own CSV data, which is culture-independent anyway.</para>
/// </summary>
public static class IcuProbe
{
    public const string InvariantVariable = "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT";

    /// <summary>Where distributions put <c>libicuuc.so.*</c>: Fedora/openSUSE <c>/usr/lib64</c>, Arch
    /// <c>/usr/lib</c>, Debian/Ubuntu the multiarch folder.</summary>
    public static IReadOnlyList<string> DefaultDirs { get; } =
    [
        "/usr/lib64", "/usr/lib", "/lib64", "/lib",
        "/usr/lib/x86_64-linux-gnu", "/lib/x86_64-linux-gnu", "/usr/local/lib",
    ];

    /// <summary>True when any directory holds a <c>libicuuc.so</c> file. Unreadable directories count
    /// as "not here".</summary>
    public static bool Present(IEnumerable<string> dirs)
    {
        foreach (var dir in dirs)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "libicuuc.so*").Any())
                    return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return false;
    }

    /// <summary>Linux only: the extra environment a proxy start needs, or null when none. Honors an
    /// explicit setting of the variable by the player.</summary>
    public static KeyValuePair<string, string>? ProxyEnvironmentFor(
        IEnumerable<string> dirs, Func<string, string?> env)
    {
        if (!string.IsNullOrEmpty(env(InvariantVariable))) return null;
        var searched = dirs.Concat((env("LD_LIBRARY_PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries));
        return Present(searched) ? null : new KeyValuePair<string, string>(InvariantVariable, "1");
    }
}
