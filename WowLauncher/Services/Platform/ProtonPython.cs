namespace WowLauncher.Services.Platform;

using System.Diagnostics;

/// <summary>
/// Can this machine run GE-Proton's <c>proton</c> script at all?
///
/// <para><b>Why (matrix 2026-09-24).</b> <c>proton</c> is a Python script (<c>#!/usr/bin/env python3</c>),
/// and GE-Proton11-7 needs Python 3.11 or newer (<c>vulkan.py</c>: <c>from typing import Self</c>).
/// Ubuntu 22.04 ships 3.10 (so do Mint 21 and Pop!_OS 22.04): there <c>proton run</c> dies with an
/// ImportError before Wine starts. Offering GE-Proton on such a machine, or downloading it, would turn
/// a working Wine start into no start at all, so the launcher keeps the Wine path there.</para>
/// </summary>
public static class ProtonPython
{
    public static readonly Version Minimum = new(3, 11);

    /// <summary>"3.10" / "3.12.4" → true when at least <see cref="Minimum"/>. Null or garbage → false.</summary>
    public static bool IsEnough(string? versionText)
    {
        if (string.IsNullOrWhiteSpace(versionText)) return false;
        var parts = versionText.Trim().Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
            return false;
        return new Version(major, minor) >= Minimum;
    }

    private static readonly Lazy<bool> ForThisMachine = new(() => IsEnough(ReadPython3Version()));

    /// <summary>Asked once per launcher run: the answer cannot change without a package install.</summary>
    public static bool IsEnoughOnThisMachine() => OperatingSystem.IsLinux() && ForThisMachine.Value;

    private static string? ReadPython3Version()
    {
        try
        {
            var psi = new ProcessStartInfo("python3")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("import sys; print('%d.%d' % sys.version_info[:2])");
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(10_000)) { try { p.Kill(); } catch (InvalidOperationException) { } return null; }
            return p.ExitCode == 0 ? output.Trim() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null; // no python3 at all
        }
    }
}
