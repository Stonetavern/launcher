namespace WowLauncher.Services;

using WowLauncher.Services.Platform;

/// <summary>
/// The OS-touching seam <see cref="UpdateService"/> calls through for the release 1.8.11
/// self-update-swap preflight (TRANSLOCATED / PATH_NOT_WRITABLE). Optional collaborator, same shape as
/// <see cref="IUpdateAttemptLedger"/>/<see cref="IUpdateHealth"/> on the very same constructor: null
/// means "do not brake", i.e. the exact behaviour that shipped before this existed, so every test that
/// does not care about this gate keeps compiling and passing unchanged. The real app always wires
/// <see cref="InstallEnvironmentGate"/> (see <c>DependencyInjection</c>); a unit test that constructs
/// <see cref="UpdateService"/> directly with fakes for download/swap must not also perform a real
/// filesystem write probe or a real macOS bundle walk just to exercise the swap logic.
/// </summary>
public interface IInstallEnvironmentGate
{
    /// <summary>Findings for the two rules the swap cares about — never the full rule set
    /// (<see cref="InstallEnvironmentPreflight.EvaluateSelfUpdateSwap"/>, not <c>Evaluate</c>).</summary>
    IReadOnlyList<PreflightFinding> EvaluateSelfUpdateSwap(string launcherBaseDir, string targetDir);
}

/// <inheritdoc cref="IInstallEnvironmentGate"/>
public sealed class InstallEnvironmentGate : IInstallEnvironmentGate
{
    public IReadOnlyList<PreflightFinding> EvaluateSelfUpdateSwap(string launcherBaseDir, string targetDir)
    {
        var facts = new InstallEnvironmentFacts(
            InstallEnvironmentProbes.CurrentOs(),
            launcherBaseDir,
            targetDir,
            IsElevated: false,
            InstallEnvironmentProbes.CollectEnvironmentVars(),
            InstallEnvironmentProbes.HasQuarantineAttribute(
                InstallEnvironmentProbes.ResolveMacBundleRoot(launcherBaseDir)),
            InstallEnvironmentProbes.ProbeWritable(targetDir),
            LongestRelativePathInManifest: 0,
            GameProcessRunning: false);

        return InstallEnvironmentPreflight.EvaluateSelfUpdateSwap(facts);
    }
}
