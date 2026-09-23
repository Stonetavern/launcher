namespace WowLauncher.Services.Platform;

using System.Runtime.Versioning;
using System.Security.Principal;

/// <summary>
/// The OS-touching side of the 1.8.11 preflight: thin adapters that turn a real machine into an
/// <see cref="InstallEnvironmentFacts"/>. Deliberately dumb — nothing here decides anything, it only
/// reports. All the actual rules live in <see cref="InstallEnvironmentPreflight"/>, which is what gets
/// unit tested; these probes are exercised for real only by running the launcher itself (elevation and
/// the macOS quarantine flag are not simulated in CI — see the release report for what that leaves
/// unmeasured).
/// </summary>
public static class InstallEnvironmentProbes
{
    private static readonly string[] EnvironmentKeys =
    [
        "ProgramFiles", "ProgramFiles(x86)", "windir", "SystemRoot",
        "OneDrive", "OneDriveConsumer", "OneDriveCommercial",
        "USERPROFILE", "APPIMAGE",
    ];

    public static InstallHostOs CurrentOs() =>
        OperatingSystem.IsWindows() ? InstallHostOs.Windows
        : OperatingSystem.IsMacOS() ? InstallHostOs.MacOs
        : OperatingSystem.IsLinux() ? InstallHostOs.Linux
        : InstallHostOs.Other;

    /// <summary>True when this process runs elevated. Always false off Windows — elevation is a
    /// Windows-only concept for the rules that read it.</summary>
    public static bool IsElevated() => OperatingSystem.IsWindows() && IsElevatedWindows();

    [SupportedOSPlatform("windows")]
    private static bool IsElevatedWindows()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            // A probe that cannot answer is not evidence of elevation — ELEVATED is a Warn, not a
            // Block, so a false negative here costs a missed hint, never a wrongly refused download.
            return false;
        }
    }

    /// <summary>The handful of environment variables <see cref="InstallEnvironmentPreflight"/> reads,
    /// collected once into a case-insensitive dictionary (env var names are case-insensitive on Windows,
    /// and folding them the same way on every OS keeps the rules simple).</summary>
    public static IReadOnlyDictionary<string, string> CollectEnvironmentVars()
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in EnvironmentKeys)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
                vars[key] = value;
        }
        return vars;
    }

    /// <summary>macOS quarantine flag on <paramref name="bundlePath"/>: <c>xattr -p com.apple.quarantine
    /// &lt;path&gt;</c> exits 0 when the attribute is present. Never throws; any probe failure (missing
    /// xattr binary, timeout, anything) reads as "not quarantined" — QUARANTINED is Warn-only, so a
    /// false negative costs a missed hint, not a wrong refusal.</summary>
    public static bool HasQuarantineAttribute(string bundlePath)
    {
        if (!OperatingSystem.IsMacOS() || string.IsNullOrWhiteSpace(bundlePath)) return false;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/xattr")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add("com.apple.quarantine");
            psi.ArgumentList.Add(bundlePath);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return false;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(true); } catch { /* best effort */ }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Walk up from <paramref name="startDir"/> to the nearest ancestor whose name ends in
    /// <c>.app</c> — the bundle root <c>xattr</c> should be asked about, since
    /// <c>AppContext.BaseDirectory</c> on macOS resolves deep inside
    /// <c>Stonetavern.app/Contents/MacOS</c>. Returns <paramref name="startDir"/> unchanged when no
    /// <c>.app</c> ancestor is found (e.g. in tests, or a dev run outside a bundle).</summary>
    public static string ResolveMacBundleRoot(string startDir)
    {
        try
        {
            var dir = new DirectoryInfo(startDir);
            while (dir is not null)
            {
                if (dir.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        catch
        {
            // fall through to the unresolved start dir
        }
        return startDir;
    }

    /// <summary>Create-then-delete a probe file in <paramref name="dir"/> (creating the directory first
    /// if it does not exist). Never throws; a failed probe simply reports "not writable".</summary>
    public static bool ProbeWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".wl-preflight-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, [0]);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
