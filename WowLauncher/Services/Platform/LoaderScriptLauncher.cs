using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace WowLauncher.Services.Platform;

/// <summary>
/// Legacy (1.12.1) launch through the client package's own loader script when it ships one.
///
/// <para><b>Why this exists.</b> The tuned 1.12.1 client (Stonetavern-Classic) does not run by starting
/// <c>WoW.exe</c> directly. Its documented, tested entry point is <c>launch.sh</c> (Linux), which runs
/// the chain <c>wine VanillaFixes.exe WoW_tweaked.exe</c> — VanillaFixes is the mandatory RDTSC timing
/// loader (without it the 5875 client stutters on modern hardware) — and additionally places the DXVK
/// <c>d3d9.dll</c> next to the exe with a <c>d3d9=n,b</c> override (Direct3D 9 on Vulkan; without it the
/// client falls back to slow wined3d), writes the resolution from the real monitor (a too-small value
/// crops the client UI), and handles the first-run TOS. Starting <c>wine WoW.exe</c> ourselves silently
/// skips all of that: the client comes up looking fine but stutters, renders on wined3d and may show a
/// cropped UI. So when the install carries <c>launch.sh</c>, we run IT, not the bare exe.</para>
///
/// <para><b>Fallback.</b> A client WITHOUT a loader script (an older package, a player's own install)
/// is still STARTED the old way — delegate to the inner launcher (<c>wine WoW.exe</c>) — but it no longer
/// skips the realm binding. Until 2026-08-09 the missing script short-circuited the whole method, so that
/// install started against whatever realm its files carried, silently and regardless of what the launcher
/// displayed. The decision which STARTER runs is still per-launch off what is on disk; the realm
/// guarantee is not conditional on it.</para>
///
/// <para>The script self-locates (<c>CLIENT_DIR</c> from <c>BASH_SOURCE</c>) and <c>cd</c>s into its own
/// directory, so we only need to hand <c>bash</c> the script path. It manages its own WINEPREFIX default,
/// matching the environment the package is proven against.</para>
///
/// <para><b>The realm is written here as well, not only handed over.</b> <c>launch.sh</c> DOES read
/// <see cref="RealmBinding.RealmlistEnvVar"/> and writes the same two files from it, so for a client that
/// ships the script the environment alone would do. It says nothing about a client that does not ship
/// one: there the launcher is the only writer. So the binding runs before the fork —
/// <see cref="RealmBinding.WriteClientRealm"/> plus the environment — and the two writes are byte-identical,
/// hence idempotent when the script runs afterwards. The order is deliberate: an address that cannot be
/// validated refuses the launch BEFORE anything is written, and a write that fails or does not read back
/// refuses it too — never a silent start on the realm the files happened to carry.</para>
/// </summary>
public sealed class LoaderScriptLauncher : IGameLauncher
{
    /// <summary>The loader script name the tuned Linux client ships (see class remarks).</summary>
    internal const string LoaderName = "launch.sh";

    private readonly IGameLauncher _inner;
    private readonly Serilog.ILogger _log;
    private readonly Func<string, IReadOnlyDictionary<string, string>, GameLaunchResult>? _runScript;
    private readonly Func<string?>? _realmAddress;

    /// <param name="inner">The launcher used when there is no loader script (the existing wine start).</param>
    /// <param name="realmAddress">The realm this launch goes to. The loader script writes the realmlist
    /// itself, from its own hardcoded default, AFTER the launcher wrote it — so without handing it
    /// <c>REALMLIST</c> here, a player's own realm was silently replaced by the shipped Stonetavern
    /// address at start time (see <see cref="WowLauncher.Services.RealmBinding"/>). Null = do not set it
    /// (the script keeps its default), which is the pre-2026-07-27 behaviour.</param>
    /// <param name="runScript">Test seam: given the resolved script path and the environment it must run
    /// with, start it and return the result. Null = start <c>bash &lt;script&gt;</c> for real.</param>
    public LoaderScriptLauncher(
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

    /// <summary>
    /// Durchgereicht, weil dieser Launcher nur den WEG zum Start aendert, nicht seine
    /// Voraussetzungen: ob Wine da ist und taugt, entscheidet der innere.
    ///
    /// <para>🔴 Ohne diese drei Zeilen meldete die Bereitschaftspruefung „bereit" auf einer Maschine
    /// ganz ohne Wine - die Vorgabe der Schnittstelle sagt ja, und jede Huelle, die sie nicht
    /// ueberschreibt, sagt es mit. Gefunden am 2026-08-05 in der nackten VM, zweimal hintereinander:
    /// erst hat die Weiche den Falschen gefragt, dann hat diese Huelle gar nicht gefragt. Deshalb
    /// steht der Hinweis auch hier und nicht nur einmal.</para>
    /// </summary>
    public Task<string?> CheckReadyAsync(string exeName) => _inner.CheckReadyAsync(exeName);

    public async Task<GameLaunchResult> LaunchAsync(string exePath, string workingDirectory)
    {
        var script = FindLoaderScript(exePath, workingDirectory, File.Exists);

        // The realm is settled BEFORE the fork between loader script and plain wine start (Codex review
        // 2026-08-09, finding 2 — the Linux twin of the Windows fail-open closed in 891b94f). Until then
        // the "no launch.sh" branch returned straight into the inner launcher — no RealmAddress.Parse,
        // no WriteClientRealm — so a player using the supported "Locate installed WoW" on a 1.12.1
        // install without a loader script started on whatever realm the files happened to carry.
        // ConfigureClient does not close that: it logs and returns on an invalid address and swallows
        // write failures, so the ONLY guarantee is the one taken here.
        if (!TryResolveRealmEnvironment(out var environment, out var address, out var realmError))
            return GameLaunchResult.Failed(realmError!);

        // Only now, with a validated address, may anything be written into the client. With a script
        // present this duplicates what the script itself does from REALMLIST — byte-for-byte the same
        // two writes, so running both is idempotent, and doing it here is what makes the guarantee hold
        // for the install that has no script.
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

        _log.Information("Launching the tuned client through its loader script {Script}", script);
        try
        {
            return _runScript is not null ? _runScript(script, environment) : StartBash(script, environment);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Loader script {Script} failed to start", script);
            return GameLaunchResult.Failed($"Could not start the client loader: {ex.Message}");
        }
    }

    /// <summary>
    /// The loader script for this install, or null when there is none. Looks in the working directory and
    /// next to the resolved exe (the client dir either way). Pure so a test can drive it without disk.
    /// </summary>
    internal static string? FindLoaderScript(string? exePath, string? workingDirectory, Func<string, bool> fileExists)
        => LoaderScriptPaths.FindLoader(LoaderName, exePath, workingDirectory, fileExists);

    /// <summary>
    /// The realm environment for this launch: <c>REALMLIST=&lt;address&gt;</c> when a valid address is
    /// resolvable, otherwise a refusal. An address that fails validation is NOT passed on and NOT
    /// written — a mangled hostname must never reach a file the client executes as configuration, and
    /// an empty environment would leave the script on its shipped default.
    /// <paramref name="realm"/> carries the validated address out so the caller can write it into the
    /// client itself; it stays null in standalone mode (no resolver wired), which is the
    /// pre-2026-07-27 behaviour.
    /// </summary>
    internal bool TryResolveRealmEnvironment(
        out IReadOnlyDictionary<string, string> environment,
        out WowLauncher.Services.RealmAddress? realm,
        out string? error)
    {
        // No resolver is the backwards-compatible standalone-client mode. Once the launcher has a
        // selected realm, however, bad input must stop the launch: an empty environment makes
        // launch.sh silently use its shipped Stonetavern default instead.
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

        _log.Information("Loader script realm: {Var}={Address}", RealmBinding.RealmlistEnvVar, address.Value);
        environment = RealmBinding.LoaderEnvironment(address);
        realm = address;
        error = null;
        return true;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private GameLaunchResult StartBash(string script, IReadOnlyDictionary<string, string> environment)
    {
        var psi = new ProcessStartInfo("bash")
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(script) ?? Environment.CurrentDirectory,
        };
        foreach (var (key, value) in environment) psi.Environment[key] = value;
        psi.ArgumentList.Add(script);
        var proc = Process.Start(psi);
        if (proc is null)
            return GameLaunchResult.Failed("The client loader did not start.");
        _log.Information("Client loader started (pid {Pid})", proc.Id);
        return GameLaunchResult.Ok(proc.Id);
    }
}
