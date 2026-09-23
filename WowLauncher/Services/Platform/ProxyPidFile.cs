namespace WowLauncher.Services.Platform;

using System.Diagnostics;

/// <summary>
/// The pidfile both proxy runners use to clean up after a launcher that died without stopping its
/// proxy — and the one piece of information it was missing.
///
/// <para><b>What went wrong without it.</b> The file held a single number: the proxy's PID. Before
/// starting a proxy, a runner read it, found the process alive, saw a matching process name, called
/// it "stale from a previous session" and killed it. That is right after a crash — and wrong while
/// a second launcher window is open, which is an ordinary thing to happen: the launcher hides itself
/// in the tray when the game starts, so a player who wants it back often starts it again instead of
/// finding the tray icon. The second instance then reads the same pidfile (one fixed path under
/// ShareDir, shared by every instance), kills the proxy the FIRST instance started, and the player
/// who is in the world right now loses their connection. There is no single-instance lock anywhere
/// in the launcher to prevent the second window (measured: no Mutex in the tree).</para>
///
/// <para><b>What tells the two cases apart.</b> Not the proxy — a proxy left behind by a crash and a
/// proxy serving a live session look identical, both alive, both listening. The difference is the
/// LAUNCHER that started it. So the pidfile now records both PIDs, and a proxy is only stale when the
/// launcher that owns it is gone. Old single-number files are still read (a player mid-upgrade must
/// not get a stuck proxy) and are treated the way they always were.</para>
/// </summary>
internal static class ProxyPidFile
{
    /// <summary>What a pidfile says: the proxy, and the launcher that started it (null for the old
    /// single-number format).</summary>
    internal readonly record struct Entry(int ProxyPid, int? OwnerPid);

    public static void Write(string path, int proxyPid, Serilog.ILogger logger)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, $"{proxyPid} {Environment.ProcessId}");
        }
        catch (Exception ex)
        {
            logger.Debug(ex, "Could not write proxy pidfile {Path}", path);
        }
    }

    public static Entry? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var parts = File.ReadAllText(path).Trim()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !int.TryParse(parts[0], out var proxyPid)) return null;
            int? owner = parts.Length > 1 && int.TryParse(parts[1], out var o) ? o : null;
            return new Entry(proxyPid, owner);
        }
        catch
        {
            return null;
        }
    }

    public static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    /// <summary>True when the launcher that started this proxy is still running — meaning the proxy
    /// belongs to a live session and must not be touched.
    ///
    /// <para>Our own process never counts: a relaunch of the same instance is not another session.
    /// The process name is checked too, because the OS recycles PIDs and a stranger that inherited
    /// the number must not be able to protect an orphaned proxy forever.</para></summary>
    public static bool OwnerStillRunning(Entry entry)
    {
        if (entry.OwnerPid is not { } ownerPid) return false;      // old format: no owner recorded
        if (ownerPid == Environment.ProcessId) return false;        // ourselves, from an earlier run

        try
        {
            using var owner = Process.GetProcessById(ownerPid);
            using var self = Process.GetCurrentProcess();
            return string.Equals(owner.ProcessName, self.ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;   // gone, or not inspectable — treat the proxy as orphaned, as before
        }
    }
}
