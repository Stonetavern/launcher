namespace WowLauncher.Services;

using System.IO;

/// <summary>
/// The one place that turns a wire path from a file manifest or a ZIP entry into a local path.
///
/// <para><b>Why one place.</b> Two halves of the repair mechanism used to disagree about what a path
/// is: <see cref="ContentRoot.Resolve"/> mapped BOTH <c>\</c> and <c>/</c> onto the platform
/// separator before voting on a root, while <c>ClientVerifyService</c> mapped only <c>/</c>. On
/// Windows that difference is invisible; on macOS and Linux the root vote counted an entry as
/// present that the verifier then reported as missing. Whichever rule is right, it has to be the
/// same rule on both sides — so it lives here and both call it.</para>
///
/// <para><b>And the rule is platform-dependent, not a matter of taste.</b> On Windows <c>\</c> cannot
/// appear IN a file name, so treating it as a separator is always correct. On Unix it is an ordinary
/// character: a file really can be called <c>dir\file</c>, and reinterpreting the manifest entry
/// <c>dir\file</c> as <c>dir/file</c> would let a verifier declare a file present that is not there.
/// That is the one direction an integrity check may never fail in, so on Unix the path is taken
/// literally and the schema violation is reported instead.</para>
///
/// <para><c>deploy/MANIFEST-SCHEMA.md</c> §files[].path requires <c>/</c> "even from a Windows
/// generator run", so a backslash is always a generator bug. It is logged rather than rejected: a
/// launcher that refuses a whole manifest locks every player out of updating, which is a worse
/// failure than one entry reading as missing (arbitration 2026-09-14).</para>
/// </summary>
public static class ManifestPath
{
    /// <summary>The wire path as a local relative path.</summary>
    public static string ToLocal(string wirePath)
    {
        if (string.IsNullOrEmpty(wirePath)) return wirePath;
        var local = wirePath.Replace('/', Path.DirectorySeparatorChar);
        // Windows only: there, and only there, a backslash is unambiguously a separator.
        if (OperatingSystem.IsWindows()) local = local.Replace('\\', Path.DirectorySeparatorChar);
        return local;
    }

    /// <summary>True when this wire path violates the "/ only" rule of MANIFEST-SCHEMA.md.</summary>
    public static bool ViolatesSeparatorRule(string wirePath) =>
        !string.IsNullOrEmpty(wirePath) && wirePath.Contains('\\', StringComparison.Ordinal);
}
