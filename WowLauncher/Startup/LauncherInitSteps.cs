using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Localization;
using WowLauncher.Models;
using WowLauncher.Services;

namespace WowLauncher.Startup;

/// <summary>
/// What the five startup steps found out, for the login surface to read. Written by the steps on
/// their own threads, read by the ViewModel after the matching <see cref="InitPipeline.StepChanged"/>
/// arrived on the UI thread; the report is the memory barrier, nothing here is polled.
/// </summary>
public sealed class InitFacts
{
    /// <summary>The loaded config (step 1). Null until step 1 ran.</summary>
    public LauncherConfig? Config { get; internal set; }

    /// <summary>Notify-only launcher update (step 2, Linux/macOS): a newer version is published.
    /// Null when up to date, when the channel auto-applies, or when the check failed.</summary>
    public LauncherUpdateNotice? UpdateNotice { get; internal set; }

    /// <summary>True when the self-update decided to replace the binary (step 2). The pipeline halts;
    /// the process is expected to end.</summary>
    public bool UpdateSwapStarted { get; internal set; }

    /// <summary>Realm probe results, one per shipped realm entry (step 3).</summary>
    public IReadOnlyList<RealmProbe> Realms { get; internal set; } = [];

    /// <summary>The download origin answered with a manifest (step 4).</summary>
    public bool CdnReachable { get; internal set; }

    /// <summary>Client installs found on disk, build -> folder (step 5).</summary>
    public IReadOnlyDictionary<int, string> Installs { get; internal set; } = new Dictionary<int, string>();
}

/// <summary>One realm entry and what the probe said about it.</summary>
public sealed record RealmProbe(string Id, string Name, string Address, bool Online, int? PlayerCount);

/// <summary>
/// Builds the five real steps of Spec §5.2 on top of the services the launcher already has. Nothing
/// here is a second implementation: step 2 is <see cref="IUpdateService"/>, step 3 is
/// <see cref="IServerStatusService"/>, step 4 is <see cref="IManifestService"/>, step 5 is
/// <see cref="IClientService.DetectInstalls"/> over the config's install roots, exactly what
/// <c>PlayViewModel.InitAsync</c> runs today for the v3 shell.
///
/// <para>The v3 shell still runs its own <c>InitAsync</c> when it comes up in Phase 4, so the
/// manifest is fetched twice on a cold start (once here, once there). That is a deliberate cost of
/// leaving the shipped surface untouched: the manifest is a few kilobytes, and the alternative of
/// threading pipeline results into PlayViewModel touches the update path that was just repaired.</para>
/// </summary>
public static class LauncherInitSteps
{
    public const string ConfigKey = "config";
    public const string UpdateKey = "update";
    public const string RealmKey = "realm";
    public const string CdnKey = "cdn";
    public const string InstallsKey = "installs";

    /// <summary>Budget for each network step (2, 3, 4). Eight seconds: comfortably above a healthy
    /// answer over a slow link and below the HTTP layer's full retry ladder (~14 s), so a dead
    /// origin costs the player at most this before the login gate opens with the realm row
    /// reporting what it knows. A budgeted-out self-update check is retried by the v3 shell's own
    /// InitAsync in Phase 4, so nothing is lost, only deferred.</summary>
    public static readonly TimeSpan NetworkBudget = TimeSpan.FromSeconds(8);

    public static (InitPipeline Pipeline, InitFacts Facts) Build(
        IConfigService config,
        IManifestService manifest,
        IUpdateService update,
        IServerStatusService status,
        IClientService client)
    {
        var facts = new InitFacts();
        InitPipeline? pipeline = null;

        var steps = new List<InitStep>
        {
            // 1. Lokale Config laden
            new(ConfigKey, Loc.T("Init_Status_Config"), gatesLogin: true, async ct =>
            {
                facts.Config = await Task.Run(config.Load, ct).ConfigureAwait(false);
                return null;
            }),

            // 2. Launcher-Selbst-Update pruefen. Same call chain as PlayViewModel.InitAsync: the
            //    launcher manifest (always Stonetavern's, never the selected realm's), then the
            //    signature-gated CheckAndApply. True means the swap is under way and the process must
            //    end; the pipeline halts and leaves the update sentence on screen.
            new(UpdateKey, Loc.T("Init_Status_Update"), gatesLogin: true, async ct =>
            {
                var launcherManifest = await manifest.FetchLauncherManifestAsync(ct).ConfigureAwait(false);
                if (await update.CheckAndApplyAsync(launcherManifest, ct).ConfigureAwait(false))
                {
                    facts.UpdateSwapStarted = true;
                    pipeline!.RequestHalt(Loc.T("Splash_Status_UpdatingLauncher"));
                    return "swap";
                }
                facts.UpdateNotice = update.CheckForNotice(launcherManifest);
                return facts.UpdateNotice is null ? null : facts.UpdateNotice.Version;
            }, NetworkBudget),

            // 3. Realm-Status abfragen. One probe per realm entry. Stonetavern is one entry with Elwynn
            //    and Barrens behind the same realmd; its row shows both counted together.
            new(RealmKey, Loc.T("Init_Status_Realm"), gatesLogin: true, async ct =>
            {
                var cfg = facts.Config ?? config.Load();
                var probes = new List<RealmProbe>();
                foreach (var realm in RealmRegistry.All(cfg))
                {
                    // The entry's own address. A manifest may move a preset (RealmBinding.Effective),
                    // but that manifest is step 4, after this; the v3 shell re-resolves in Phase 4.
                    var address = realm.RealmlistAddress?.Trim() ?? "";
                    if (address.Length == 0) continue;
                    // Counted only across the game realms behind a shipped entry (Stonetavern: Elwynn +
                    // Barrens, one number). A custom server has no ArmoryRealms: up/down, no count.
                    var result = await status.CheckAsync(address, realm.ArmoryRealms ?? [], ct).ConfigureAwait(false);
                    probes.Add(new RealmProbe(realm.Id, realm.Name, address, result.Online, result.PlayerCount));
                }
                facts.Realms = probes;
                return probes.Count == 0 ? null : (probes[0].Online ? "online" : "offline");
            }, NetworkBudget),

            // 4. CDN/Bucket-Erreichbarkeit: the realm manifest from the download origin. A null
            //    answer is "not reachable" here, not a failure of the step.
            new(CdnKey, Loc.T("Init_Status_Cdn"), gatesLogin: false, async ct =>
            {
                var m = await manifest.FetchAsync(ct).ConfigureAwait(false);
                facts.CdnReachable = m is not null;
                return facts.CdnReachable ? "reachable" : "unreachable";
            }, NetworkBudget),

            // 5. Lokale Client-Installationen erfassen (1.12 und 1.14.2), over the same install roots
            //    the v3 shell uses. Read-only here: persisting a newly detected folder stays with
            //    PlayViewModel.InitAsync, which owns the config write.
            new(InstallsKey, Loc.T("Init_Status_Installs"), gatesLogin: false, async ct =>
            {
                var cfg = facts.Config ?? config.Load();
                var known = new Dictionary<int, string>(cfg.ClientInstalls);
                facts.Installs = await Task.Run(() => client.DetectInstalls(known), ct).ConfigureAwait(false);
                return facts.Installs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }),
        };

        pipeline = new InitPipeline(steps);
        return (pipeline, facts);
    }
}
