namespace WowLauncher.Services.Platform;

using WowLauncher.Models;

/// <summary>What starts a client on Linux, in the order the launch itself decides it.</summary>
public enum LinuxRuntimeSource
{
    /// <summary>1.12.1 from the Stonetavern package: its START.sh fetches and runs a pinned Proton.</summary>
    PackageProton,
    /// <summary>1.14.2 on a GE-Proton found on this machine (Steam's compatibility tools).</summary>
    GeProton,
    /// <summary>1.14.2 on the pinned GE-Proton the launcher fetches before the first start.</summary>
    GeProtonPinned,
    /// <summary>The Wine from the runtime menu: a 1.12.1 without START.sh, or 1.14.2 when Python is
    /// too old for the proton script.</summary>
    Wine,
}

/// <summary>The answer for one client. <see cref="Wine"/> is set only for <see cref="LinuxRuntimeSource.Wine"/>.</summary>
public sealed record LinuxRuntimeInUse(LinuxRuntimeSource Source, string? GeProtonName, LinuxRuntimeDecision? Wine, bool PythonTooOld);

/// <summary>
/// One answer to "what really starts this client", shared by the runtime line in Settings and the
/// "Runner:" line of the start report.
///
/// <para>🔴 Both used to read the Wine menu alone. Settings said "In use: /usr/bin/wine" and the start
/// report "Runner: /usr/bin/wine" while both clients started on Proton (owner 2026-09-28). Settings
/// was corrected first and the report kept the old answer, found only on the rendered page. One
/// function for both, so the two cannot drift apart again.</para>
/// </summary>
public static class LinuxRuntimeInUseResolver
{
    public static LinuxRuntimeInUse For(
        ClientVersion client, string? clientDir, string? configuredRuntime, string? customPath,
        Func<string?> findGeProton, Func<bool> protonPythonOk, Func<string, bool> hasStartScript)
    {
        if (!client.NeedsModernRuntime)
        {
            // Not installed yet counts as the package: that is what the download brings.
            if (string.IsNullOrWhiteSpace(clientDir) || hasStartScript(clientDir))
                return new(LinuxRuntimeSource.PackageProton, null, null, false);
            return new(LinuxRuntimeSource.Wine, null,
                LinuxRuntimeSelection.ForCurrentUser(configuredRuntime, customPath, autoPrefersWineGe: false), false);
        }

        if (!protonPythonOk())
            return new(LinuxRuntimeSource.Wine, null,
                LinuxRuntimeSelection.ForCurrentUser(configuredRuntime, customPath, autoPrefersWineGe: true), true);
        if (findGeProton() is { } proton)
            return new(LinuxRuntimeSource.GeProton, Path.GetFileName(Path.GetDirectoryName(proton)) ?? proton, null, false);
        return new(LinuxRuntimeSource.GeProtonPinned, null, null, false);
    }

    /// <summary>The start report's "Runner:" line. English on purpose: support reads it.</summary>
    public static string ReportLine(LinuxRuntimeInUse r) => r.Source switch
    {
        LinuxRuntimeSource.PackageProton => "Proton from the client package (START.sh)",
        LinuxRuntimeSource.GeProton => "GE-Proton " + r.GeProtonName,
        LinuxRuntimeSource.GeProtonPinned => "GE-Proton, pinned build fetched before the first start",
        _ => WineLine(r.Wine) + (r.PythonTooOld ? " (Python too old for Proton)" : ""),
    };

    private static string WineLine(LinuxRuntimeDecision? d)
    {
        if (d is null || !d.Found) return "none found";
        return d.FellBack ? d.Path + " (fallback, the chosen one is not usable)" : d.Path;
    }
}
