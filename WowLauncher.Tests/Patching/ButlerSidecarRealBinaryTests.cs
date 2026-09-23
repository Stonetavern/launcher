using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using WowLauncher.Services.Patching;
using Xunit;

namespace WowLauncher.Tests.Patching;

/// <summary>
/// Runs the REAL, hash-pinned butler binary (copied next to the test assembly by
/// <c>WowLauncher.Tests.csproj</c> regardless of the platform this happens to build for) through
/// <see cref="ButlerSidecar"/> — a genuine external-process integration proof, not a mock. Every test
/// here is a real Skip (not a silent pass) when the binary is not present for this OS/arch, per the
/// Auftrag ("butler-Tests nur, wenn das Binary da ist, sonst Skip mit Grund").
/// </summary>
public sealed class ButlerSidecarRealBinaryTests
{
    /// <summary>The directory <c>WowLauncher.Tests.csproj</c> copies <c>tools/butler/&lt;rid&gt;</c>
    /// into, one level up from the normal publish layout — <see cref="ButlerSidecar"/> expects
    /// <c>&lt;base&gt;/tools/butler/&lt;rid&gt;/...</c>, so this points it at
    /// <c>bin/.../butler-fixture</c> as that base.</summary>
    private static string FixtureBaseDir => Path.Combine(AppContext.BaseDirectory, "butler-fixture");

    private static bool BinaryPresent =>
        OperatingSystem.IsLinux() && File.Exists(Path.Combine(FixtureBaseDir, "tools", "butler", "linux-x64", "butler"));

    private static ButlerSidecar NewSidecar() =>
        new(FixtureBaseDir, new Serilog.LoggerConfiguration().CreateLogger());

    [SkippableFact]
    public void IsAvailable_is_true_only_when_the_pinned_hash_matches()
    {
        Skip.IfNot(BinaryPresent, "butler binary fixture not present for this OS/arch — see WowLauncher.Tests.csproj");
        Assert.True(NewSidecar().IsAvailable);
    }

    [SkippableFact]
    public void IsAvailable_is_false_when_the_binary_is_tampered_with()
    {
        Skip.IfNot(BinaryPresent, "butler binary fixture not present for this OS/arch");

        var tamperedBase = PatchingFakes.NewTempDir();
        var dir = Path.Combine(tamperedBase, "tools", "butler", "linux-x64");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "butler"), "not-really-butler"u8.ToArray());

        var sidecar = new ButlerSidecar(tamperedBase, new Serilog.LoggerConfiguration().CreateLogger());
        Assert.False(sidecar.IsAvailable);
    }

    /// <summary>Teil A (ARCHITEKTUR-v2-patcher.md §5): the pin now covers the two 7z sidecars too, not
    /// just the executable — butler cannot open a <c>.pwr</c> without them, so a tampered/mismatched
    /// sidecar library must close the Delta route exactly like a tampered executable does. Copies the
    /// REAL fixture (so the exe and the untouched sidecar still verify) and corrupts only one
    /// library, proving the gate checks every pinned file, not just <c>butler</c> itself.</summary>
    [SkippableFact]
    public void IsAvailable_is_false_when_a_7z_sidecar_is_tampered_with()
    {
        Skip.IfNot(BinaryPresent, "butler binary fixture not present for this OS/arch");

        var tamperedBase = PatchingFakes.NewTempDir();
        var dir = Path.Combine(tamperedBase, "tools", "butler", "linux-x64");
        Directory.CreateDirectory(dir);
        foreach (var name in new[] { "butler", "7z.so", "libc7zip.so" })
            File.Copy(Path.Combine(FixtureBaseDir, "tools", "butler", "linux-x64", name), Path.Combine(dir, name));

        // Corrupt only the untouched-in-the-other-test sidecar — butler.exe itself and libc7zip.so
        // both still match their pins.
        File.WriteAllBytes(Path.Combine(dir, "7z.so"), "not the real 7z.so"u8.ToArray());

        var sidecar = new ButlerSidecar(tamperedBase, new Serilog.LoggerConfiguration().CreateLogger());
        Assert.False(sidecar.IsAvailable);
    }

    /// <summary>
    /// The S0 lesson end to end with the REAL binary: build a genuine delta with <c>butler diff</c>
    /// (test setup, not part of the production surface), apply it via <see cref="ButlerSidecar.ApplyAsync"/>
    /// onto the CORRECT base, and prove <see cref="ButlerSidecar.VerifyAsync"/> reports it clean.
    /// </summary>
    [SkippableFact]
    public async Task Apply_then_verify_succeeds_against_the_correct_base()
    {
        Skip.IfNot(BinaryPresent, "butler binary fixture not present for this OS/arch");

        var (oldDir, newDir, pwrPath, sigPath) = BuildRealDelta();
        var stagingDir = Path.Combine(PatchingFakes.NewTempDir(), "staging");
        var targetDir = PatchingFakes.NewTempDir();
        CopyDir(oldDir, targetDir);

        var sidecar = NewSidecar();
        var apply = await sidecar.ApplyAsync(pwrPath, stagingDir, targetDir, progress: null, CancellationToken.None);
        Assert.True(apply.Ok, apply.Detail);

        var verify = await sidecar.VerifyAsync(sigPath, targetDir, CancellationToken.None);
        Assert.True(verify.Ok, verify.Detail);

        Assert.Equal(
            await File.ReadAllTextAsync(Path.Combine(newDir, "changed.txt")),
            await File.ReadAllTextAsync(Path.Combine(targetDir, "changed.txt")));
    }

    /// <summary>The exact S0 finding, with the real binary: apply the delta onto a TREE THAT IS NOT
    /// THE DECLARED BASE. Real butler still exits 0 ("Patched ... cleanly") — verify is what catches
    /// it. If this ever stops being true (a butler upgrade that starts refusing bad bases outright),
    /// this test fails LOUDLY rather than the engine silently losing its safety net.</summary>
    [SkippableFact]
    public async Task Apply_exits_ok_against_the_wrong_base_but_verify_catches_it()
    {
        Skip.IfNot(BinaryPresent, "butler binary fixture not present for this OS/arch");

        var (oldDir, _, pwrPath, sigPath) = BuildRealDelta();
        var wrongBase = PatchingFakes.NewTempDir();
        CopyDir(oldDir, wrongBase);
        // Sabotage the base AFTER the delta was built from the real `oldDir` — same shape as
        // A1/v_wrong2 in the S0 measurement (a handful of bytes changed inside an existing file).
        var sabotaged = Path.Combine(wrongBase, "unchanged.txt");
        var bytes = await File.ReadAllBytesAsync(sabotaged);
        bytes[0] ^= 0xFF;
        await File.WriteAllBytesAsync(sabotaged, bytes);

        var stagingDir = Path.Combine(PatchingFakes.NewTempDir(), "staging");
        var sidecar = NewSidecar();
        var apply = await sidecar.ApplyAsync(pwrPath, stagingDir, wrongBase, progress: null, CancellationToken.None);
        Assert.True(apply.Ok, "S0 measured butler apply exiting 0 against the wrong base — if this assert fails, butler's own behaviour changed and ButlerSidecar's doc comment needs updating");

        var verify = await sidecar.VerifyAsync(sigPath, wrongBase, CancellationToken.None);
        Assert.False(verify.Ok); // THIS is the check that must never be skipped
    }

    /// <summary>Builds oldDir/newDir/patch.pwr/patch.pwr.sig with the real butler binary — test
    /// fixture generation, not production code (the production surface only ever APPLIES/VERIFIES a
    /// delta the operator's release tool built, never diffs one itself).</summary>
    private static (string oldDir, string newDir, string pwrPath, string sigPath) BuildRealDelta()
    {
        var oldDir = PatchingFakes.NewTempDir();
        var newDir = PatchingFakes.NewTempDir();
        File.WriteAllText(Path.Combine(oldDir, "unchanged.txt"), "same in both versions, padded a bit to be a real file...");
        File.WriteAllText(Path.Combine(newDir, "unchanged.txt"), "same in both versions, padded a bit to be a real file...");
        File.WriteAllText(Path.Combine(oldDir, "changed.txt"), "old contents");
        File.WriteAllText(Path.Combine(newDir, "changed.txt"), "new contents, this is the one file the delta must carry");

        var patchDir = PatchingFakes.NewTempDir();
        var pwrPath = Path.Combine(patchDir, "test.pwr");
        var butlerExe = Path.Combine(FixtureBaseDir, "tools", "butler", "linux-x64", "butler");

        var psi = new ProcessStartInfo(butlerExe)
        {
            // Redirected AND drained (BeginOutputReadLine/BeginErrorReadLine below) — an unread
            // redirected pipe deadlocks the child the moment its output fills the OS pipe buffer,
            // which WaitForExit alone cannot see coming (measured while building this fixture:
            // `--verbose` chatter from `butler diff` is small here, but leaving the pipes undrained
            // is a latent hang for any input this test is later given).
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.ArgumentList.Add("diff");
        psi.ArgumentList.Add(oldDir);
        psi.ArgumentList.Add(newDir);
        psi.ArgumentList.Add(pwrPath);
        using var proc = Process.Start(psi)!;
        proc.OutputDataReceived += (_, _) => { };
        proc.ErrorDataReceived += (_, _) => { };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        var exited = proc.WaitForExit(60_000);
        Assert.True(exited, "butler diff did not finish within 60s while building the test fixture");
        Assert.Equal(0, proc.ExitCode);

        return (oldDir, newDir, pwrPath, pwrPath + ".sig");
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
    }
}
