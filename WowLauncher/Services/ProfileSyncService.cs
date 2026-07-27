namespace WowLauncher.Services;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using WowLauncher.Models;

/// <summary>What one sync attempt did, so the caller can say something true instead of guessing.</summary>
public enum ProfileSyncOutcome
{
    /// <summary>Switched off, or nobody is signed in. Not an error.</summary>
    Skipped,
    /// <summary>Server and local agree; nothing was written anywhere.</summary>
    UpToDate,
    /// <summary>The merged profile is now on the server and in the local config.</summary>
    Synced,
    /// <summary>The session is gone (401). The caller must show this, not swallow it.</summary>
    SignedOut,
    /// <summary>Server unreachable or broken. The local config is untouched and still authoritative.</summary>
    Unavailable,
}

public interface IProfileSyncService
{
    /// <summary>Pull, union with the local realms, push the result back if it differs. Never throws.</summary>
    Task<ProfileSyncOutcome> SyncAsync(CancellationToken ct = default);
}

/// <summary>
/// Carries the player's own realms between machines, hung off the Stonetavern account.
///
/// <para><b>The local config stays the source of truth.</b> This is a reconciler on top of it, not a
/// replacement: every failure path leaves the config exactly as it was. That is not politeness, it is
/// the difference between "we could not check" and "you have no realms" - and writing the second when
/// the first is true would delete a player's setup because a server had a bad minute.</para>
///
/// <para>🔴 <b>401 is surfaced, never swallowed.</b> The launcher token lives 24 hours and is not
/// renewed. The friends service treats any non-2xx as "degraded list" and keeps going, which is
/// harmless for a roster but would mean a profile that silently stops syncing on day two and a player
/// who only finds out on the other machine.</para>
/// </summary>
public sealed class HttpProfileSyncService : IProfileSyncService
{
    private readonly HttpClient _http;
    private readonly IConfigService _config;
    private readonly ILauncherAuthService _auth;
    private readonly Serilog.ILogger _log;

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    // One sync at a time, plus a "do it again afterwards" flag.
    //
    // 🔴 Adding and removing realms each fire a sync, and a player editing two realms in a row would
    // otherwise have two runs in flight. The older one holds a stale local snapshot; if it wins the
    // race after a conflict retry it writes the OLD state back and the newer edit is gone for good.
    // Serialising costs nothing here (a sync is seconds apart at most) and removes the whole class.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _rerunRequested;

    public HttpProfileSyncService(HttpClient http, IConfigService config, ILauncherAuthService auth,
        Serilog.ILogger log)
    {
        _http = http; _config = config; _auth = auth;
        _log = log.ForContext<HttpProfileSyncService>();
    }

    public async Task<ProfileSyncOutcome> SyncAsync(CancellationToken ct = default)
    {
        // A second caller does not queue up behind an unbounded line: it asks the running one to go
        // around once more with the then-current config and returns.
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            Interlocked.Exchange(ref _rerunRequested, 1);
            return ProfileSyncOutcome.Skipped;
        }
        try
        {
            var outcome = await SyncCoreAsync(ct).ConfigureAwait(false);
            while (Interlocked.Exchange(ref _rerunRequested, 0) == 1)
                outcome = await SyncCoreAsync(ct).ConfigureAwait(false);
            return outcome;
        }
        finally { _gate.Release(); }
    }

    private async Task<ProfileSyncOutcome> SyncCoreAsync(CancellationToken ct)
    {
        var cfg = _config.Load();
        if (!cfg.ProfileSyncEnabled) return ProfileSyncOutcome.Skipped;

        var token = _auth.CurrentToken;
        if (string.IsNullOrEmpty(token)) return ProfileSyncOutcome.Skipped;

        try
        {
            var (getOutcome, server) = await GetAsync(token, ct).ConfigureAwait(false);
            if (getOutcome is not null) return getOutcome.Value;

            var local = LauncherProfile.FromConfig(cfg);
            var merged = ProfileMerge.Union(server!.Profile, local);

            // Nothing to do when all three agree. Pushing anyway would bump the version on every
            // start and turn a quiet feature into a write amplifier.
            var serverProfile = server.Profile ?? new LauncherProfile();
            if (ProfileMerge.SameContent(merged, serverProfile)
                && ProfileMerge.SameContent(merged, local))
            {
                Apply(merged, server.Version);
                return ProfileSyncOutcome.UpToDate;
            }

            var put = await PutAsync(token, server.Version, merged, ct).ConfigureAwait(false);
            if (put.outcome is not null) return put.outcome.Value;

            // 409: someone else wrote in between. Union again against what they wrote and retry ONCE.
            // A loop here would be a fight between two machines; one retry settles the normal case and
            // the next start settles anything rarer.
            if (put.conflict is not null)
            {
                var second = ProfileMerge.Union(put.conflict.Profile, merged);
                var retry = await PutAsync(token, put.conflict.Version, second, ct).ConfigureAwait(false);
                if (retry.outcome is not null) return retry.outcome.Value;
                if (retry.conflict is not null)
                {
                    _log.Warning("Profile sync gave up after a second conflict; local config untouched");
                    return ProfileSyncOutcome.Unavailable;
                }
                Apply(second, retry.version);
                return ProfileSyncOutcome.Synced;
            }

            Apply(merged, put.version);
            return ProfileSyncOutcome.Synced;
        }
        catch (OperationCanceledException) { return ProfileSyncOutcome.Unavailable; }
        catch (Exception ex)
        {
            _log.Warning(ex, "Profile sync failed; local config is unchanged");
            return ProfileSyncOutcome.Unavailable;
        }
    }

    private async Task<(ProfileSyncOutcome? outcome, LauncherProfileEnvelope? env)> GetAsync(
        string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiEndpoints.Base(_config)}/launcher/profile");
        req.Headers.Authorization = new("Bearer", token);
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);

        if (res.StatusCode == HttpStatusCode.Unauthorized) return (ProfileSyncOutcome.SignedOut, null);
        if (!res.IsSuccessStatusCode)
        {
            _log.Warning("Profile fetch returned {Status}", (int)res.StatusCode);
            return (ProfileSyncOutcome.Unavailable, null);
        }

        var env = await res.Content.ReadFromJsonAsync<LauncherProfileEnvelope>(Json, ct).ConfigureAwait(false);
        // A 200 with a body we cannot read is NOT an empty profile. Treating it as one would hand an
        // empty list to the union and, on the next push, wipe the server copy.
        if (env is null) return (ProfileSyncOutcome.Unavailable, null);
        return (null, env);
    }

    private async Task<(ProfileSyncOutcome? outcome, LauncherProfileEnvelope? conflict, long version)> PutAsync(
        string token, long baseVersion, LauncherProfile profile, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, $"{ApiEndpoints.Base(_config)}/launcher/profile")
        {
            Content = JsonContent.Create(
                new LauncherProfilePutRequest { BaseVersion = baseVersion, Profile = profile }, options: Json),
        };
        req.Headers.Authorization = new("Bearer", token);
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);

        if (res.StatusCode == HttpStatusCode.Unauthorized) return (ProfileSyncOutcome.SignedOut, null, 0);
        if (res.StatusCode == HttpStatusCode.Conflict)
        {
            var current = await res.Content.ReadFromJsonAsync<LauncherProfileEnvelope>(Json, ct)
                .ConfigureAwait(false);
            if (current is null) return (ProfileSyncOutcome.Unavailable, null, 0);
            return (null, current, 0);
        }
        if (!res.IsSuccessStatusCode)
        {
            _log.Warning("Profile push returned {Status}", (int)res.StatusCode);
            return (ProfileSyncOutcome.Unavailable, null, 0);
        }

        var env = await res.Content.ReadFromJsonAsync<LauncherProfileEnvelope>(Json, ct).ConfigureAwait(false);
        return (null, null, env?.Version ?? baseVersion + 1);
    }

    /// <summary>Write the merged realms back into the local config, leaving presets and every
    /// machine-local field alone.</summary>
    private void Apply(LauncherProfile merged, long version)
    {
        // 🔴 Re-read and merge against what the config says NOW, not against the snapshot this run
        // started with. A sync takes seconds of network time, and a player who adds or removes a realm
        // during those seconds would otherwise have it wiped by our own stale copy - and the queued
        // rerun would then read the already-overwritten state and never notice.
        var cfg = _config.Load();
        var current = LauncherProfile.FromConfig(cfg);
        var safe = ProfileMerge.SameContent(merged, current) ? merged : ProfileMerge.Union(merged, current);

        var presets = (cfg.Realms ?? []).Where(r => r.IsPreset).ToList();
        cfg.Realms = presets.Concat(safe.Realms.Select(r => r.ToEntry())).ToList();
        cfg.DeletedRealms = safe.DeletedRealms.ToList();
        // The selected realm comes back too. Union already decided whose choice wins; writing only
        // the realm list here would upload the field and then ignore it - a half mechanism.
        if (!string.IsNullOrWhiteSpace(safe.SelectedRealmId)) cfg.SelectedRealmId = safe.SelectedRealmId;
        cfg.ProfileVersion = version;
        _config.Save(cfg);

        // Etwas hat sich waehrend des Laufs geaendert: der Server kennt es noch nicht, also muss ein
        // weiterer Durchgang folgen. Ohne das laege die Aenderung nur lokal und der naechste Sync
        // haette keinen Anlass, sie hochzuladen.
        if (!ProfileMerge.SameContent(safe, merged)) Interlocked.Exchange(ref _rerunRequested, 1);
    }
}

/// <summary>Demo/offline stand-in: reports "nothing to do" without touching the network.</summary>
public sealed class NoProfileSync : IProfileSyncService
{
    public Task<ProfileSyncOutcome> SyncAsync(CancellationToken ct = default) =>
        Task.FromResult(ProfileSyncOutcome.Skipped);
}
