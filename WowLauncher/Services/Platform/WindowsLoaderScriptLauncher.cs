using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace WowLauncher.Services.Platform;

/// <summary>
/// Windows counterpart to <see cref="LoaderScriptLauncher"/>: legacy (1.12.1) launch through the
/// client package's own loader batch file when it ships one.
///
/// <para><b>Why this exists.</b> The tuned 1.12.1 client (Stonetavern-Classic) does not run by starting
/// <c>WoW.exe</c> directly — on Windows just as on Linux. Its documented entry point is <c>launch.bat</c>
/// (the Windows pendant to <c>launch.sh</c>), which runs the chain <c>VanillaFixes.exe WoW_tweaked.exe</c>
/// — VanillaFixes is the mandatory RDTSC timing loader (without it the 5875 client stutters on modern
/// hardware) — and additionally places the DXVK <c>d3d9.dll</c> next to the exe (Direct3D 9 on Vulkan;
/// without it the client falls back to slow native D3D9), writes the resolution from the real monitor,
/// and handles the first-run TOS. Starting <c>WoW.exe</c> ourselves silently skips all of that: the
/// client comes up looking fine but stutters, renders without DXVK and may show a cropped UI. So when the
/// install carries <c>launch.bat</c>, we run IT, not the bare exe. This closes the Windows half of the
/// same silent-degradation the Linux <see cref="LoaderScriptLauncher"/> closed.</para>
///
/// <para><b>Fallback.</b> A client WITHOUT a loader batch (an older package, a player's own install)
/// is still STARTED the old way — delegate to the inner launcher (<see cref="WindowsGameLauncher"/>,
/// the plain native <c>Process.Start(WoW.exe)</c>) — but it no longer skips the realm binding. Until
/// 2026-08-09 the missing batch short-circuited the whole method, so that install started against
/// whatever realm its files carried, silently and regardless of what the launcher displayed. The
/// decision which STARTER runs is still per-launch off what is on disk; the realm guarantee is not
/// conditional on it.</para>
///
/// <para>A <c>.bat</c> cannot be started with <c>UseShellExecute=false</c> directly, so we invoke it
/// through <c>cmd.exe /c &lt;script&gt;</c> with the client directory as the working dir — the batch
/// self-locates its own folder either way, matching how the package is proven.</para>
///
/// <para><b>The realm is written here, not delegated.</b> The <c>launch.bat</c> shipped in every client
/// package up to 2026-08-09 reads <c>REALMLIST</c> at NO point — unlike its Linux sibling
/// <c>launch.sh</c> it only runs <c>detect-display.ps1</c> and starts VanillaFixes; it writes neither
/// <c>realmlist.wtf</c> nor <c>WTF/Config.wtf</c> (verified against the packaged batch, 2026-08-09).
/// Relying on the environment alone would therefore have left a Windows player on whatever realm the
/// files already carried, while the launcher displayed another one. So this launcher writes both files
/// itself via <see cref="RealmBinding.WriteClientRealm"/> before starting the batch, and additionally
/// passes <c>REALMLIST</c> so a later, fixed batch writes the same value (idempotent).
/// The order is deliberate: an address that cannot be validated refuses the launch BEFORE anything is
/// written, and a write that fails or does not read back refuses it too — never a silent start on the
/// package's default realm.</para>
/// </summary>
public sealed class WindowsLoaderScriptLauncher : IGameLauncher
{
    /// <summary>The loader batch name the tuned Windows client ships (see class remarks).</summary>
    internal const string LoaderName = "launch.bat";

    private readonly IGameLauncher _inner;
    private readonly Serilog.ILogger _log;
    private readonly Func<string, IReadOnlyDictionary<string, string>, GameLaunchResult>? _runScript;
    private readonly Func<string?>? _realmAddress;

    /// <param name="inner">The launcher used when there is no loader batch (the existing native start).</param>
    /// <param name="realmAddress">The realm this launch goes to. Null preserves standalone-client
    /// behaviour; otherwise an invalid value refuses the loader so its package default cannot win.</param>
    /// <param name="runScript">Test seam: given the resolved script path and its environment, start it
    /// and return the result. Null = start <c>cmd.exe /c &lt;script&gt;</c> for real.</param>
    public WindowsLoaderScriptLauncher(
        IGameLauncher inner,
        Serilog.ILogger log,
        Func<string?>? realmAddress = null,
        Func<string, IReadOnlyDictionary<string, string>, GameLaunchResult>? runScript = null)
    {
        _inner = inner;
        _log = log;
        _realmAddress = realmAddress;
        _runScript = runScript;
    }

    /// <summary>Durchgereicht wie beim Linux-Gegenstueck: diese Huelle aendert den Weg zum Start,
    /// nicht seine Voraussetzungen. Eine Huelle, die die Vorgabe stehen laesst, meldet „bereit" fuer
    /// einen inneren Launcher, den niemand gefragt hat.</summary>
    public Task<string?> CheckReadyAsync(string exeName) => _inner.CheckReadyAsync(exeName);

    public async Task<GameLaunchResult> LaunchAsync(string exePath, string workingDirectory)
    {
        var script = FindLoaderScript(exePath, workingDirectory, File.Exists);

        // The realm is settled BEFORE the fork between loader batch and native start (Codex review
        // 2026-08-09, finding 2). Until then the "no launch.bat" branch returned straight into the
        // inner launcher — no RealmAddress.Parse, no WriteClientRealm — so a player using the supported
        // "Locate installed WoW" on a 1.12.1 install without a loader batch started on whatever realm
        // the files happened to carry. ConfigureClient does not close that: it logs and returns on an
        // invalid address and swallows write failures, so the ONLY guarantee is the one taken here.
        if (!TryResolveRealmEnvironment(out var environment, out var address, out var realmError))
            return GameLaunchResult.Failed(realmError!);

        // Only now, with a validated address, may anything be written into the client.
        if (address is not null)
        {
            var clientDir = LoaderScriptPaths.ClientDirectory(script, exePath, workingDirectory);
            if (!RealmBinding.WriteClientRealm(clientDir, address, out var writeError))
            {
                _log.Error("Not starting the client: {Error}", writeError);
                return GameLaunchResult.Failed(writeError);
            }
        }

        if (script is null)
            return await _inner.LaunchAsync(exePath, workingDirectory).ConfigureAwait(false);

        _log.Information("Launching the tuned client through its loader batch {Script}", script);
        try
        {
            return _runScript is not null ? _runScript(script, environment) : StartCmd(script, environment);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Loader batch {Script} failed to start", script);
            return GameLaunchResult.Failed($"Could not start the client loader: {ex.Message}");
        }
    }

    /// <summary>
    /// The loader batch for this install, or null when there is none. Looks in the working directory and
    /// next to the resolved exe (the client dir either way). Pure so a test can drive it without disk.
    /// </summary>
    internal static string? FindLoaderScript(string? exePath, string? workingDirectory, Func<string, bool> fileExists)
        => LoaderScriptPaths.FindLoader(LoaderName, exePath, workingDirectory, fileExists);

    /// <summary>Resolve the exact environment the batch needs to agree with the selected realm.
    /// Once a resolver exists, invalid input is a refusal: an empty environment would cause the batch
    /// to use its packaged default realm.</summary>
    internal bool TryResolveRealmEnvironment(
        out IReadOnlyDictionary<string, string> environment,
        out WowLauncher.Services.RealmAddress? realm,
        out string? error)
    {
        if (_realmAddress is null)
        {
            environment = EmptyEnvironment;
            realm = null;
            error = null;
            return true;
        }

        var raw = _realmAddress.Invoke();
        var address = WowLauncher.Services.RealmAddress.Parse(raw);
        if (address is null)
        {
            _log.Error(
                "Not starting the client: {Address} is not a usable realmlist address.", raw);
            environment = EmptyEnvironment;
            realm = null;
            error = "The selected realm address is invalid, so the launcher did not start the client " +
                    "(it would otherwise connect to the package's default realm).";
            return false;
        }

        _log.Information("Loader batch realm: {Var}={Address}", RealmBinding.RealmlistEnvVar, address.Value);
        environment = RealmBinding.LoaderEnvironment(address);
        realm = address;
        error = null;
        return true;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private GameLaunchResult StartCmd(string script, IReadOnlyDictionary<string, string> environment)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(script) ?? Environment.CurrentDirectory,
        };
        foreach (var (key, value) in environment) psi.Environment[key] = value;
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(script);
        var proc = Process.Start(psi);
        if (proc is null)
            return GameLaunchResult.Failed("The client loader did not start.");
        _log.Information("Client loader started (pid {Pid})", proc.Id);
        return GameLaunchResult.Ok(proc.Id);
    }
}
