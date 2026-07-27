namespace WowLauncher.Services.Platform;

using System.Text.Json;

/// <summary>Result of the host Vulkan check: whether a driver was found, the highest API version it
/// reports, and where the answer came from (for the log, so a wrong verdict is diagnosable).</summary>
public sealed record VulkanCapability(bool DriverFound, Version? ApiVersion, string Source)
{
    public static VulkanCapability None(string source) => new(false, null, source);

    /// <summary>The modern client renders through D3D12, which vkd3d maps onto Vulkan 1.3 features.
    /// Below that the client starts and then dies when it enters the world, which is exactly the
    /// crash this check exists to replace with a sentence.</summary>
    public bool MeetsModernClientRequirement =>
        DriverFound && ApiVersion is not null && ApiVersion >= new Version(1, 3);
}

/// <summary>
/// Answers "does this machine have a Vulkan driver, and is it new enough" WITHOUT starting the game
/// and WITHOUT depending on <c>vulkaninfo</c> being installed (it usually is not).
///
/// <para>Method: read the Vulkan loader's own ICD manifests. Every installed driver drops a small
/// JSON file (<c>{"ICD": {"library_path": ..., "api_version": "1.4.303"}}</c>) into a well-known
/// directory; that is how the loader itself finds drivers, so reading the same files answers the same
/// question the loader would answer. The search path follows the loader's documented order, including
/// the <c>VK_ICD_FILENAMES</c>/<c>VK_DRIVER_FILES</c> override and XDG data directories, so a
/// non-standard install (Nix, a container, a manually placed driver) is not reported as "no Vulkan".</para>
///
/// <para><b>Absence is reported as absence, not as failure.</b> Finding zero manifests is a real
/// signal - it is how a machine with no GPU driver looks - but the caller states it as "no Vulkan
/// driver was found", never as "your GPU is unsupported", because this check reads configuration and
/// not the hardware.</para>
/// </summary>
public static class VulkanProbe
{
    /// <summary>Directories the Vulkan loader searches for ICD manifests, most specific first.</summary>
    public static IReadOnlyList<string> DefaultSearchDirs()
    {
        var dirs = new List<string>();

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdgDataHome))
            dirs.Add(Path.Combine(xdgDataHome, "vulkan", "icd.d"));
        else if (!string.IsNullOrEmpty(home))
            dirs.Add(Path.Combine(home, ".local", "share", "vulkan", "icd.d"));

        dirs.Add("/etc/vulkan/icd.d");

        var xdgDataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        if (!string.IsNullOrWhiteSpace(xdgDataDirs))
            dirs.AddRange(xdgDataDirs
                .Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Select(d => Path.Combine(d, "vulkan", "icd.d")));

        dirs.Add("/usr/local/share/vulkan/icd.d");
        dirs.Add("/usr/share/vulkan/icd.d");

        return dirs.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Probe the current machine. Honours <c>VK_DRIVER_FILES</c>/<c>VK_ICD_FILENAMES</c>
    /// first, exactly as the loader does - a player who points those at a specific driver has already
    /// answered the question.</summary>
    public static VulkanCapability ForCurrentMachine()
    {
        var explicitFiles =
            Environment.GetEnvironmentVariable("VK_DRIVER_FILES")
            ?? Environment.GetEnvironmentVariable("VK_ICD_FILENAMES");

        if (!string.IsNullOrWhiteSpace(explicitFiles))
        {
            var files = explicitFiles.Split(':', StringSplitOptions.RemoveEmptyEntries);
            var fromEnv = Highest(files, "VK_DRIVER_FILES");
            if (fromEnv.DriverFound)
                return fromEnv;
        }

        return FromDirs(DefaultSearchDirs());
    }

    /// <summary>Probe a given set of manifest directories. Injectable so the behaviour is testable
    /// against fabricated manifests instead of whatever driver the dev box happens to have.</summary>
    public static VulkanCapability FromDirs(IReadOnlyList<string> searchDirs)
    {
        var manifests = new List<string>();
        foreach (var dir in searchDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                    manifests.AddRange(Directory.EnumerateFiles(dir, "*.json"));
            }
            catch (Exception)
            {
                // An unreadable directory is not evidence either way; keep looking in the others.
            }
        }

        return manifests.Count == 0
            ? VulkanCapability.None("no ICD manifest found")
            : Highest(manifests, $"{manifests.Count} ICD manifest(s)");
    }

    private static VulkanCapability Highest(IEnumerable<string> manifestPaths, string source)
    {
        Version? best = null;
        var any = false;

        foreach (var path in manifestPaths)
        {
            var version = ReadApiVersion(path);
            if (version is null) continue;
            any = true;
            if (best is null || version > best) best = version;
        }

        return any ? new VulkanCapability(true, best, source) : VulkanCapability.None(source);
    }

    /// <summary>The <c>ICD.api_version</c> of one manifest, or null if the file is absent, malformed,
    /// or not an ICD manifest. A manifest we cannot parse is skipped rather than treated as a driver:
    /// counting it would turn a broken file into a false "Vulkan is fine".</summary>
    internal static Version? ReadApiVersion(string manifestPath)
    {
        try
        {
            using var stream = File.OpenRead(manifestPath);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("ICD", out var icd)) return null;
            if (!icd.TryGetProperty("api_version", out var apiVersion)) return null;
            var text = apiVersion.GetString();
            return Version.TryParse(text, out var parsed) ? parsed : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
