namespace WowLauncher.Services.Platform;

using System.Runtime.InteropServices;

/// <summary>
/// Linux: keep this process's open files out of a launcher it starts to take its place.
///
/// <para><b>Measured 2026-09-28 (E2E, real AppImage).</b> After the Stonetavern-folder handoff the new
/// launcher's AppImage runtime held fd 1023, a directory handle on the OLD AppImage's mount, plus two
/// dmabuf graphics buffers of the old window. The AppImage runtime opens that directory without
/// close-on-exec, the old launcher inherited it, and .NET's <c>Process.Start</c> hands every such fd on.
/// So the old copy's mount process lived as long as the new launcher did. Nothing crashed; it was a leak
/// that the player would see as a second "stonetavern-launcher" in the task list.</para>
///
/// <para>Marking every fd above stderr close-on-exec right before starting the successor is the
/// standard answer (Python's subprocess does the same by default). It changes nothing in THIS process:
/// the fds stay open here, they just do not travel. Only for successor launchers, not for the game.</para>
///
/// <para>Linux only: <c>fcntl</c> is variadic, and on Apple Silicon variadic arguments go on the stack,
/// so a fixed P/Invoke signature would pass garbage there. macOS never copies the launcher anyway.</para>
/// </summary>
public static class InheritedFds
{
    private const int F_GETFD = 1;
    private const int F_SETFD = 2;
    private const int FD_CLOEXEC = 1;

    [DllImport("libc", SetLastError = true, EntryPoint = "fcntl")]
    private static extern int Fcntl(int fd, int cmd, int arg);

    /// <summary>Mark every fd above 2 close-on-exec. Returns how many were changed; 0 off Linux or on
    /// any failure (a leak is better than not starting the successor at all).</summary>
    public static int KeepFromChildren()
    {
        if (!OperatingSystem.IsLinux()) return 0;
        try
        {
            var changed = 0;
            // Listed first: the listing itself opens an fd, which comes and goes while we walk.
            foreach (var entry in Directory.GetFileSystemEntries("/proc/self/fd"))
            {
                if (!int.TryParse(Path.GetFileName(entry), out var fd) || fd <= 2) continue;
                var flags = Fcntl(fd, F_GETFD, 0);
                if (flags < 0 || (flags & FD_CLOEXEC) != 0) continue;
                if (Fcntl(fd, F_SETFD, flags | FD_CLOEXEC) == 0) changed++;
            }
            return changed;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
