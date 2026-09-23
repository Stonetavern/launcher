namespace WowLauncher.Services.Platform;

/// <summary>
/// Puts the proxy's data where the proxy actually looks for it, on the one platform where the shipped
/// package puts it somewhere else.
///
/// <para><b>The measurement this is built on (2026-08-24, Apple Silicon).</b> JimsProxy does not inherit
/// the working directory it is given — it sets its own, to the directory of its binary, and says so:</para>
/// <code>
/// Switching working directory
///   Old: .../WoW-Client-42597/Hermes
///   New: .../WoW-Client-42597/Hermes/bin
/// Fail to load config file 'HermesProxy.config'
/// Config loading failed
/// </code>
/// <para>The macOS package puts the binary in <c>Hermes/bin/</c> and leaves <c>HermesProxy.config</c>,
/// <c>CSV/</c> and <c>AccountData/</c> in <c>Hermes/</c>. Windows (<c>Hermes/JimsProxy.exe</c> beside
/// <c>Hermes/CSV/</c>) and Linux (<c>Hermes/linux/</c> for both) are not affected — only macOS separates
/// them. The proxy therefore dies before it binds port 1119, and the player never leaves the launcher.</para>
///
/// <para><b>Why this lives in the launcher and not only in the package.</b> Fixing the package is right and
/// still open, but it only helps someone who downloads 8.3 GB again. This repair costs one relative symlink
/// per missing entry and heals every Mac that already has the client on disk. It is deliberately
/// conservative: it only ever ADDS a link that is missing, never replaces or deletes anything a player has,
/// and a failure to link is reported and then ignored — the proxy start that follows is the real verdict.</para>
///
/// <para><b>Symlinks, not copies.</b> The launcher writes the realm address into
/// <c>Hermes/HermesProxy.config</c>. A copy in <c>bin/</c> would go stale the moment that happens, and the
/// proxy would silently use the old address — a worse failure than the one being fixed, because it looks
/// like it works.</para>
/// </summary>
public static class MacProxyDataLayout
{
    /// <summary>What JimsProxy reads from its own directory. The config is the one it names in its error
    /// message; the two directories are what it loads game data and account state from.</summary>
    internal static readonly string[] RequiredEntries = ["HermesProxy.config", "CSV", "AccountData"];

    /// <summary>
    /// Make sure everything the proxy reads from its own directory is reachable there.
    /// </summary>
    /// <param name="proxyExePath">The resolved proxy binary (e.g. <c>Hermes/bin/JimsProxy-arm64</c>).</param>
    /// <param name="proxyDataDir">Where the package put the data (e.g. <c>Hermes/</c>).</param>
    /// <returns>The entries that had to be linked. Empty means nothing needed doing — which is the normal,
    /// healthy case on Windows, on Linux, and on a fixed macOS package.</returns>
    public static IReadOnlyList<string> EnsureDataBesideBinary(
        string proxyExePath, string proxyDataDir, Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var linked = new List<string>();
        string binDir, dataDir;
        try
        {
            binDir = Path.GetDirectoryName(Path.GetFullPath(proxyExePath)) ?? "";
            dataDir = Path.GetFullPath(proxyDataDir);
        }
        catch (Exception ex)
        {
            logger.Debug(ex, "Could not resolve the proxy paths — leaving the layout untouched");
            return linked;
        }

        // The binary already sits with its data (Windows, Linux, a fixed package): nothing to do. Comparing
        // full paths, so a trailing separator or a relative segment cannot make this look like a mismatch.
        if (string.IsNullOrEmpty(binDir) ||
            string.Equals(binDir.TrimEnd(Path.DirectorySeparatorChar),
                          dataDir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
            return linked;

        foreach (var entry in RequiredEntries)
        {
            var target = Path.Combine(dataDir, entry);
            var link = Path.Combine(binDir, entry);
            try
            {
                // Anything already there — a real file, a directory, or a link from an earlier run — stays.
                // Idempotent by construction, and a player's own arrangement is never overwritten.
                if (File.Exists(link) || Directory.Exists(link)) continue;
                if (!File.Exists(target) && !Directory.Exists(target)) continue; // the package has no such entry

                // Relative, so the client directory survives being moved or renamed.
                var relative = Path.Combine("..", entry);
                if (Directory.Exists(target)) Directory.CreateSymbolicLink(link, relative);
                else File.CreateSymbolicLink(link, relative);
                linked.Add(entry);
            }
            catch (Exception ex)
            {
                // Read-only volume, no symlink permission, a race with another launcher: report it and move
                // on. The proxy start that follows decides whether this actually mattered.
                logger.Warning(ex, "Could not link {Entry} next to the proxy binary in {Dir}", entry, binDir);
            }
        }

        if (linked.Count > 0)
            logger.Information(
                "Proxy data layout repaired in {Dir}: linked {Entries} (the macOS package keeps them one " +
                "directory up, but the proxy reads them from beside its binary)",
                binDir, string.Join(", ", linked));

        return linked;
    }
}
