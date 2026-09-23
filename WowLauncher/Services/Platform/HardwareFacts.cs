using System;
using System.IO;
using System.Runtime.Intrinsics.X86;

namespace WowLauncher.Services.Platform;

/// <summary>
/// Two facts the Enhanced 1.12.1 package depends on and nobody could see in a report: the CPU has
/// AVX2 (wow_turbo is built for it; without it the client dies once the world loads), and the
/// 32-bit Visual C++ 2015-2022 runtime is installed (nampower.dll links against it; without it
/// the loader cannot inject the module). Logged at every launch so a "crashes after login" ticket
/// carries the answer in its log instead of a guess.
/// </summary>
public static class HardwareFacts
{
    public static bool Avx2Supported => Avx2.IsSupported;

    /// <summary>The x86 VC++ runtime is present when its two DLLs sit in SysWOW64 (where the
    /// redistributable installs them) or next to the client (an app-local copy works as well).</summary>
    public static bool VcRuntimeX86Present(string? clientDirectory, Func<string, bool>? fileExists = null)
    {
        if (!OperatingSystem.IsWindows()) return true;
        var exists = fileExists ?? File.Exists;
        var wow64 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64");
        return Present(wow64, exists) || (clientDirectory is not null && Present(clientDirectory, exists));
    }

    private static bool Present(string dir, Func<string, bool> exists) =>
        exists(Path.Combine(dir, "vcruntime140.dll")) && exists(Path.Combine(dir, "msvcp140.dll"));

    /// <summary>Which of the package's modules the install actually chain-loads, from its dlls.txt.</summary>
    public static bool ListsModule(string clientDirectory, string dllName)
    {
        var list = Path.Combine(clientDirectory, "dlls.txt");
        if (!File.Exists(list)) return false;
        foreach (var raw in File.ReadLines(list))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (string.Equals(Path.GetFileName(line), dllName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
