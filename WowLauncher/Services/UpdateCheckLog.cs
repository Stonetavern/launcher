namespace WowLauncher.Services;

using System.Text.Json;
using System.Text.Json.Serialization;
using WowLauncher.Services.Platform;

/// <summary>
/// When the launcher last REACHED the update server, and what it was offered when it did.
///
/// <para><b>Why this is written down at all.</b> A launcher that cannot reach the update server does
/// not look broken. It looks finished: no error, no banner, no red anything, and it keeps starting the
/// game perfectly well for months while every fix ships past it. On 2026-08-05 the live build was
/// 1.6.4, the built one was 1.7.4 and the download page still offered 1.5.0 - three answers to one
/// question, and no player could see any of them.</para>
///
/// <para><b>Success only.</b> An attempt is not a check. A reachable server that returns something
/// unparsable, a proxy that answers 200 with a login page, a DNS entry that resolves to nothing useful
/// - all of those must leave this file exactly as it was, or the launcher reports a fresh check while
/// nothing was ever checked. That is the same class of plausible-but-wrong state the rest of this
/// codebase is built against, and the one this file exists to expose.</para>
///
/// <para>Lives in the state directory, not in the config: it is something that HAPPENED, not something
/// the player chose. A lost state file costs one line of history and nothing else.</para>
/// </summary>
public interface IUpdateCheckLog
{
    /// <summary>Record that the launcher manifest was fetched AND parsed. Says nothing about which
    /// version was offered - that is a separate claim, see <see cref="RecordOffered"/>.</summary>
    void RecordReached();

    /// <summary>
    /// Record the version currently on offer for this platform. Called ONLY after the manifest
    /// signature verified, because a version number the launcher cannot attribute to the release key
    /// is not worth putting in front of a player - the same fail-closed rule the update hint follows.
    /// Null or empty means the verified manifest named none, which is itself worth showing.
    /// </summary>
    void RecordOffered(string? version);

    /// <summary>The last successful check, or null when there has never been one.</summary>
    DateTimeOffset? LastSuccess { get; }

    /// <summary>The version offered at that check. Empty when unknown.</summary>
    string LastOffered { get; }
}

/// <inheritdoc cref="IUpdateCheckLog"/>
public sealed class UpdateCheckLog : IUpdateCheckLog
{
    internal const string FileName = "update-check.json";

    private readonly string _path;
    private readonly Serilog.ILogger _log;
    private readonly Func<DateTimeOffset> _now;
    private Entry _entry = new();

    public UpdateCheckLog(IAppPaths paths, Serilog.ILogger log, Func<DateTimeOffset>? now = null)
    {
        _path = Path.Combine(paths.StateDir, FileName);
        _log = log.ForContext<UpdateCheckLog>();
        _now = now ?? (() => DateTimeOffset.UtcNow);
        Load();
    }

    public DateTimeOffset? LastSuccess => _entry.LastSuccessUtc;
    public string LastOffered => _entry.LastOffered ?? "";

    public void RecordReached()
    {
        _entry = _entry with { LastSuccessUtc = _now() };
        Write();
    }

    public void RecordOffered(string? version)
    {
        _entry = _entry with { LastOffered = (version ?? "").Trim() };
        Write();
    }

    private void Write()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (dir is not null) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(_entry));
        }
        catch (Exception ex)
        {
            // The in-memory value stands either way: this session still reports the truth, and the
            // next start falls back to the older entry. Losing a line of history is not worth a crash
            // in the middle of an update check.
            _log.Debug(ex, "Could not write the update-check log at {Path}", _path);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            _entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(_path)) ?? new Entry();
        }
        catch (Exception ex)
        {
            // A damaged file reads as "never checked", which is the safe direction: it understates
            // rather than claiming a check that may not have happened.
            _log.Debug(ex, "Could not read the update-check log at {Path}", _path);
            _entry = new Entry();
        }
    }

    /// <summary>A record so an update to one field cannot silently drop the other - the first
    /// version of this class replaced the whole entry on every write, and a reach without an offer
    /// would have erased the last known offer.</summary>
    private sealed record Entry
    {
        [JsonPropertyName("lastSuccessUtc")] public DateTimeOffset? LastSuccessUtc { get; init; }
        [JsonPropertyName("lastOffered")] public string? LastOffered { get; init; }
    }
}
