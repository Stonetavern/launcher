namespace WowLauncher.Services.Platform;

/// <summary>
/// The 1.12.1 client caches everything the realm tells it (item, quest, NPC and page text) in
/// <c>WDB/*.wdb</c> and never asks again. After the realm changes a row, the client keeps showing the
/// old text until the cache is gone; the communities around this client clear it on every start for
/// exactly that reason. The cache refills on its own while playing, the way it did in 2006.
/// </summary>
public static class WdbCache
{
    /// <summary>Delete every <c>*.wdb</c> under <c>WDB/</c> in <paramref name="clientDir"/>. Returns how many
    /// were deleted. Never throws: a file the client still holds is left for the next start.</summary>
    public static int Clear(string clientDir, Serilog.ILogger log)
    {
        var deleted = 0;
        try
        {
            var wdb = Path.Combine(clientDir, "WDB");
            if (!Directory.Exists(wdb)) return 0;
            foreach (var file in Directory.EnumerateFiles(wdb, "*.wdb", SearchOption.AllDirectories))
            {
                try { File.Delete(file); deleted++; }
                catch (Exception ex) { log.Debug(ex, "Could not delete {Path}", file); }
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not clear the WDB cache in {Dir}", clientDir);
        }
        return deleted;
    }
}
