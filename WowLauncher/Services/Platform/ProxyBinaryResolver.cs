using System.Runtime.InteropServices;

namespace WowLauncher.Services.Platform;

/// <summary>What the resolver decided: which file to start, and what that particular file needs.</summary>
/// <param name="Path">Full path to the proxy binary. Points at the FIRST candidate when none exists, so
/// the caller's error message names the file the package is supposed to contain.</param>
/// <param name="RequiresOpenSsl">True only for the legacy native <c>HermesProxy</c>, which links against
/// a system OpenSSL 3. The self-contained JimsProxy builds do not — measured 2026-08-24 on BOTH macOS
/// binaries of build 5.2.0: <c>AppleCrypto</c> 487 hits each, <c>Native.OpenSsl</c> zero in each,
/// against 335 in the linux-x64 binary of the same build as the positive control.</param>
public sealed record ProxyBinary(string Path, bool RequiresOpenSsl);

/// <summary>
/// Finds the realm proxy inside a 1.14.2 client bundle.
///
/// <para><b>Why this exists.</b> The proxy binary changed its name and its place in the bundle when the
/// packages moved from HermesProxy to its JimsProxy fork, and the three platforms did not move together:
/// Windows ships <c>Hermes/JimsProxy.exe</c>, Linux <c>Hermes/linux/JimsProxy</c>, macOS two
/// per-architecture binaries under <c>Hermes/bin/</c>. A launcher that hard-codes one name refuses to
/// start on every bundle that spells it differently — which is exactly what players reported on macOS
/// after the v1.4.0 rollout ("Hermes/HermesProxy is missing", report ST-8PXS-EGCY, 2026-08-24), while the
/// package's own start script found the very same proxy without trouble.</para>
///
/// <para><b>Only PUBLISHED layouts are candidates.</b> The list is deliberately not "anything that looks
/// like a proxy": every entry corresponds to a layout we actually shipped. A wider net would grow the
/// set of files the launcher is willing to execute, and nothing here hashes the file before starting it
/// (Codex review 2026-08-24, finding 3).</para>
///
/// <para><b>Architecture is not a free choice on macOS.</b> Rosetta translates x86_64 on Apple Silicon,
/// never the other way round, so an Intel Mac must not fall back to the arm64 binary — it would fail to
/// exec. Apple Silicon may fall back to x86_64: this whole package runs the game itself through
/// Rosetta+GPTK, so a machine without Rosetta cannot play at all and the fallback costs nothing.</para>
/// </summary>
public static class ProxyBinaryResolver
{
    private const string LegacyMacName = "HermesProxy";

    /// <summary>
    /// 🔴 <b>Under Wine, JimsProxy is not an option — it crashes the client on world entry.</b>
    ///
    /// <para>Measured in-world twice, independently: 2026-06-04 (<c>bugmaster/STATUS.md</c>) and
    /// 2026-08-24 on the owner's Mac. The client dies with <c>ERROR #132</c> / ACCESS_VIOLATION at
    /// instruction <c>0x141242199</c> — the same fingerprint, down to the call stack, that the crash
    /// bisection of 2026-05-31 pinned on JimsProxy's object-update framing for build 42597
    /// (<c>client-1158-re/08-jimsproxy-1142-CRASH-BISECTION.md</c>: twelve hypotheses excluded, never
    /// solved; no commit has touched that framing since). Natively on Windows JimsProxy runs faultlessly,
    /// so the ordering below is per-platform and not a blanket verdict on the proxy.</para>
    ///
    /// <para>The June measurement already produced the rule "Linux does not run JimsProxy, it runs
    /// HermesProxy v3.10". The rule was lost when the August packages were rebuilt: both the shipped Linux
    /// package (v1.4.1) and the macOS one (v1.4.0) carry JimsProxy, and macOS — which runs the game under
    /// GPTK's Wine — was never covered by the rule at all, although it always fell under it.</para>
    ///
    /// <para><b>What this costs.</b> HermesProxy is the older fork: everything JimsProxy has fixed since
    /// (pet scaling, aura ghosts, combat lockouts, the shop mounts) is missing for Wine players. That is
    /// the deliberate trade — playable with old blemishes beats a crash on entering the world. Windows
    /// keeps JimsProxy and keeps those fixes. Reverting this is one line, once the framing bug is solved.</para>
    ///
    /// <para><b>🔴 Superseded 2026-09-19 for Linux only (still true for macOS below).</b> JimsProxy
    /// v5.2.1-beta.4 (#530) fixed the framing bug — a single bit in <c>SMSG_FEATURE_SYSTEM_STATUS</c>,
    /// not <c>RaceClassExpansionLevels</c>, which stays absent in that build's feature block — and the
    /// owner reached the world with it under GE-Proton11-7 (<c>/mnt/data/wow/beta-test-linux/diag-results/
    /// LEDGER.txt</c>, Lauf U). Linux therefore prefers JimsProxy again; this note and the crash fingerprint
    /// stay on record because the fix is version-specific, not a retraction of the measurement, and because
    /// macOS (still on GPTK Wine, not yet remeasured against beta.4) keeps the old order.</para>
    /// </summary>
    private const string WineCrashNote =
        "JimsProxy crashes Wine clients on world entry (#132, build 42597 object-update framing) - " +
        "measured in-world 2026-06-04 and 2026-08-24 - " +
        "superseded 2026-09-19 for Linux: JimsProxy v5.2.1-beta.4 (#530) fixes the framing bug and " +
        "reaches the world under GE-Proton11-7 (Ledger run U); macOS unchanged, not yet remeasured";

    /// <summary>macOS candidates for the running architecture. HermesProxy FIRST: the Mac runs the game
    /// under GPTK's Wine, so <see cref="WineCrashNote"/> applies. The universal binary covers both
    /// architectures; the JimsProxy builds stay as a last resort for a bundle that has nothing else.</summary>
    public static IReadOnlyList<string> MacCandidates(Architecture architecture) =>
        architecture == Architecture.Arm64
            ? [LegacyMacName, "bin/JimsProxy-arm64", "bin/JimsProxy-x86_64"]
            : [LegacyMacName, "bin/JimsProxy-x86_64"];

    /// <summary>Linux, relative to <c>Hermes/</c>. JimsProxy FIRST since 2026-09-19 — see the
    /// "Superseded" note on <see cref="WineCrashNote"/>: v5.2.1-beta.4 no longer carries the Wine
    /// framing crash, measured in-world under GE-Proton. The new shared-tree layout (KONZEPT §13) puts
    /// it beside the Windows/macOS binaries at <c>Hermes/bin/JimsProxy-linux-x64</c>, so the caller must
    /// start it with <c>Hermes/</c> — not <c>Hermes/bin/</c> — as its data directory (config and CSV
    /// live there, same reasoning as <see cref="MacCandidates"/>'s <c>bin/</c> entries). The two
    /// <c>linux/</c> candidates are the old per-OS tree kept as a fallback for existing installs that
    /// have not repackaged yet; their data directory is <c>Hermes/linux/</c>, beside the binary.</summary>
    public static IReadOnlyList<string> LinuxCandidates { get; } =
        ["bin/JimsProxy-linux-x64", "linux/JimsProxy", "linux/HermesProxy"];

    /// <summary>Windows: one PE beside its config in <c>Hermes/</c>.</summary>
    public static IReadOnlyList<string> WindowsCandidates { get; } = ["JimsProxy.exe", "HermesProxy.exe"];

    /// <summary>The first candidate that exists under <paramref name="proxyDir"/>, or the first candidate
    /// unresolved when none does. <paramref name="fileExists"/> is injectable so the choice can be tested
    /// without laying down 80 MB binaries.</summary>
    public static ProxyBinary Resolve(
        string proxyDir,
        IReadOnlyList<string> candidates,
        Func<string, bool>? fileExists = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proxyDir);
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
            throw new ArgumentException("At least one candidate name is required.", nameof(candidates));

        var exists = fileExists ?? File.Exists;
        foreach (var candidate in candidates)
        {
            var full = Combine(proxyDir, candidate);
            if (exists(full)) return new ProxyBinary(full, NeedsOpenSsl(candidate));
        }

        return new ProxyBinary(Combine(proxyDir, candidates[0]), NeedsOpenSsl(candidates[0]));
    }

    /// <summary>The candidate relative to the bundle root, in the spelling a player sees in an error
    /// message (<c>Hermes/bin/JimsProxy-arm64</c>), independent of the platform separator.</summary>
    public static string DisplayName(string bundleRoot, string resolvedProxyExe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedProxyExe);

        var root = Path.GetFullPath(bundleRoot).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(resolvedProxyExe);
        var relative = full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? full[(root.Length + 1)..]
            : full;

        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>The legacy HermesProxy binary is the only one that links against a system OpenSSL 3.
    /// Matched by FILE NAME, not by the whole candidate string: Linux's fallback candidate is
    /// <c>linux/HermesProxy</c> (a path, not a bare name) and must still be flagged, exactly as the
    /// bare macOS <c>HermesProxy</c> is - a Linux-shaped install of the legacy binary needs the same
    /// system OpenSSL 3 the Mac one does.</summary>
    private static bool NeedsOpenSsl(string candidate)
    {
        var name = candidate[(candidate.LastIndexOf('/') + 1)..];
        return string.Equals(name, LegacyMacName, StringComparison.Ordinal);
    }

    /// <summary>Where the Linux proxy's own data (<c>HermesProxy.config</c>, <c>CSV/</c>) lives, given
    /// the binary <see cref="Resolve"/> picked. The new shared-tree layout (<see cref="LinuxCandidates"/>)
    /// puts the binary in <c>Hermes/bin/</c> while its data stays one level up in <c>Hermes/</c> - the
    /// same split <see cref="MacCandidates"/> already has for its <c>bin/</c> entries; the old per-OS
    /// layout keeps data beside the binary in <c>Hermes/linux/</c>. Worked out from the resolved PATH
    /// itself, not from "which candidate index matched" threaded separately through the caller, so it
    /// stays correct however <paramref name="resolvedProxyExe"/> was found.</summary>
    public static string LinuxDataDir(string hermesDir, string resolvedProxyExe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hermesDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedProxyExe);

        var fullHermes = Path.GetFullPath(hermesDir);
        var parent = Path.GetDirectoryName(Path.GetFullPath(resolvedProxyExe));
        var binDir = Path.Combine(fullHermes, "bin");
        return string.Equals(parent, binDir, StringComparison.Ordinal) ? fullHermes : (parent ?? fullHermes);
    }

    private static string Combine(string directory, string relativeCandidate)
    {
        var segments = relativeCandidate.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return Path.Combine([directory, .. segments]);
    }
}
