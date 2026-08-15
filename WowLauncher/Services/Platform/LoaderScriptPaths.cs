using System;
using System.Collections.Generic;
using System.IO;

namespace WowLauncher.Services.Platform;

/// <summary>
/// The path arithmetic both loader-script launchers share: where the package's loader lives
/// (<c>launch.sh</c> on Linux, <c>launch.bat</c> on Windows) and which folder the realm files belong in.
///
/// <para><b>Why it is one place.</b> Windows and Linux answer the SAME two questions, and they must
/// answer them identically — the folder the realm is written into has to be the folder the client reads,
/// on both. Two copies of this arithmetic is how the two platforms drift apart silently: one gets a fix,
/// the other keeps the old answer and nothing goes red. Pulled out on 2026-08-09 when the Linux path was
/// given the same fail-closed realm binding Windows had just received (Codex review, finding 2).</para>
/// </summary>
internal static class LoaderScriptPaths
{
    /// <summary>
    /// The loader for this install, or null when there is none. Looks in the working directory and next
    /// to the resolved exe (the client dir either way). Pure so a test can drive it without disk.
    /// </summary>
    internal static string? FindLoader(
        string loaderName, string? exePath, string? workingDirectory, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        foreach (var dir in CandidateDirs(exePath, workingDirectory))
        {
            var candidate = Path.Combine(dir, loaderName);
            if (fileExists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>The client folder the realm files belong in: next to the loader when there is one,
    /// otherwise next to the exe that is about to be started (the working directory is the last
    /// resort). Both branches must land in the SAME folder the client will read.</summary>
    internal static string ClientDirectory(string? script, string? exePath, string workingDirectory)
    {
        if (!string.IsNullOrEmpty(script))
            return Path.GetDirectoryName(script) ?? workingDirectory;
        if (!string.IsNullOrEmpty(exePath))
            return Path.GetDirectoryName(exePath) ?? workingDirectory;
        return workingDirectory;
    }

    private static IEnumerable<string> CandidateDirs(string? exePath, string? workingDirectory)
    {
        if (!string.IsNullOrEmpty(workingDirectory)) yield return workingDirectory;
        var exeDir = string.IsNullOrEmpty(exePath) ? null : Path.GetDirectoryName(exePath);
        if (!string.IsNullOrEmpty(exeDir) && !string.Equals(exeDir, workingDirectory, StringComparison.Ordinal))
            yield return exeDir;
    }
}
