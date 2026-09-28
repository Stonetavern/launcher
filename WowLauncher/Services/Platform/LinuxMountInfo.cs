namespace WowLauncher.Services.Platform;

/// <summary>
/// Linux: is a folder on a mount that forbids running programs (<c>noexec</c>)? The Stonetavern folder
/// holds the launcher and both clients, so a <c>noexec</c> target installs fine and then never starts.
/// Checked before the first byte moves (TODO C8).
///
/// <para>Read from <c>/proc/self/mountinfo</c> (proc(5)): the most specific mount point that contains
/// the folder on a path boundary wins, later lines shadow earlier ones at the same point, and the octal
/// escapes in paths (<c>\040</c> for a space) are decoded. Only a confirmed <c>noexec</c> counts. The
/// file system type alone (vfat, exfat, ntfs) is not a verdict: whether those allow running programs
/// depends on the driver and the mount options, and the options are what this reads (Codex Terra
/// 2026-09-28).</para>
/// </summary>
public static class LinuxMountInfo
{
    public sealed record Mount(string MountPoint, IReadOnlyList<string> Options, string FsType);

    /// <summary>True only when the mount holding <paramref name="path"/> is known to be <c>noexec</c>.
    /// False when it is not, and also when it cannot be told (not Linux, unreadable table).</summary>
    public static bool IsNoExec(string path)
    {
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            var mounts = Parse(File.ReadAllLines("/proc/self/mountinfo"));
            return MountFor(mounts, RealPath(path))?.Options.Contains("noexec") == true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static IReadOnlyList<Mount> Parse(IEnumerable<string> lines)
    {
        var result = new List<Mount>();
        foreach (var line in lines)
        {
            var fields = line.Split(' ');
            var dash = Array.IndexOf(fields, "-");
            if (fields.Length < 7 || dash < 6 || dash + 1 >= fields.Length) continue;
            result.Add(new Mount(Unescape(fields[4]), fields[5].Split(','), fields[dash + 1]));
        }
        return result;
    }

    /// <summary>The mount a path lives on: the longest mount point that is the path or a parent of it,
    /// the last one listed when several share that point (a later mount hides an earlier one).</summary>
    public static Mount? MountFor(IReadOnlyList<Mount> mounts, string path)
    {
        Mount? best = null;
        foreach (var m in mounts)
        {
            if (!Contains(m.MountPoint, path)) continue;
            if (best is null || m.MountPoint.Length >= best.MountPoint.Length) best = m;
        }
        return best;
    }

    private static bool Contains(string mountPoint, string path)
    {
        if (mountPoint == "/") return path.StartsWith('/');
        return path == mountPoint || path.StartsWith(mountPoint + "/", StringComparison.Ordinal);
    }

    /// <summary>proc(5): space, tab, newline and backslash appear as three-digit octal escapes.</summary>
    internal static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && IsOctal(s, i + 1) && IsOctal(s, i + 2) && IsOctal(s, i + 3))
            {
                sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 3), 8));
                i += 3;
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    private static bool IsOctal(string s, int i) => i < s.Length && s[i] is >= '0' and <= '7';

    /// <summary>The folder with every symlink resolved, for the part that exists; a part that does not
    /// exist yet is appended as written (the Stonetavern folder is usually created by the setup).</summary>
    internal static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var missing = new Stack<string>();
        var existing = full;
        while (!Directory.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (parent is null) break;
            missing.Push(Path.GetFileName(existing));
            existing = parent;
        }
        var resolved = Resolve(existing);
        while (missing.Count > 0) resolved = Path.Combine(resolved, missing.Pop());
        return resolved;
    }

    private static string Resolve(string existing)
    {
        // Walk from the root and follow each link, so a link in the middle is resolved too.
        var parts = existing.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "/";
        foreach (var part in parts)
        {
            var next = Path.Combine(current, part);
            var target = new DirectoryInfo(next).ResolveLinkTarget(returnFinalTarget: true);
            current = target is not null ? Path.GetFullPath(target.FullName) : next;
        }
        return current;
    }
}
