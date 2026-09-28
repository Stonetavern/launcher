using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// A launcher started to take this one's place must not inherit its open files. Measured on the real
/// AppImage (2026-09-28): the handed-over launcher held the old AppImage's mount handle, so the old
/// copy stayed mounted for the whole session.
/// </summary>
public sealed class InheritedFdsTests
{
    [DllImport("libc", SetLastError = true)]
    private static extern int dup(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    private static string ChildSeesFds()
    {
        var psi = new ProcessStartInfo("/bin/sh", ["-c", "ls /proc/self/fd"]) { UseShellExecute = false, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return " " + text.Replace('\n', ' ') + " ";
    }

    [Fact]
    public void AnOpenFile_DoesNotReachTheSuccessor()
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = Path.GetTempFileName();
        using var file = new FileStream(path, FileMode.Open);
        // .NET opens its own files close-on-exec; dup() gives the kind of fd the AppImage runtime leaves.
        var fd = dup((int)file.SafeFileHandle.DangerousGetHandle());
        Assert.True(fd > 2);
        try
        {
            // Positive control: without the call a child really sees it, so the check below can fail.
            Assert.Contains($" {fd} ", ChildSeesFds());

            Assert.True(InheritedFds.KeepFromChildren() >= 1);

            Assert.DoesNotContain($" {fd} ", ChildSeesFds());
        }
        finally
        {
            close(fd);
            File.Delete(path);
        }
    }
}
