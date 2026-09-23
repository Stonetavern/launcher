using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace WowLauncher.Services.Platform;

/// <summary>A display mode as the 1.12.1 client names it: <c>gxResolution "WxH"</c> and
/// <c>gxRefresh "Hz"</c>. <see cref="Hz"/> 0 means "not known" — never written, never compared.</summary>
public readonly record struct DisplayMode(int Width, int Height, int Hz)
{
    public string Resolution => Width.ToString(CultureInfo.InvariantCulture) + "x" +
                                Height.ToString(CultureInfo.InvariantCulture);

    /// <summary>The client reads refresh rates as whole numbers; anything below 24 is a driver's
    /// placeholder (WMI hands out 0 and 1), not a monitor.</summary>
    public bool HzKnown => Hz >= 24;

    public static bool TryParseResolution(string? text, out int width, out int height)
    {
        width = height = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split('x', 'X');
        return parts.Length == 2
               && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out width)
               && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out height)
               && width > 0 && height > 0;
    }
}

/// <summary>What the desktop reports: the primary monitor's current mode plus every mode any
/// attached monitor can be switched to. <see cref="Modes"/> may be empty when the platform cannot enumerate; the policy then
/// skips the checks that need a list rather than guessing.</summary>
public sealed record DisplayProbeResult(DisplayMode Current, IReadOnlyList<DisplayMode> Modes);

/// <summary>Asks the running desktop about the primary monitor. Null when nothing can be read —
/// the caller then leaves the client's own files alone and lets the package's loader script try.</summary>
public interface IDisplayProbe
{
    DisplayProbeResult? Probe();
}

/// <summary>The display-relevant lines of <c>WTF/Config.wtf</c> as the client stores them.</summary>
public sealed record StoredDisplay(string? Resolution, string? Refresh, bool Windowed);

/// <summary>What the launcher wrote last time, kept in <c>WTF/.display-detected</c>. Equal to what
/// the config now holds ⇒ the player never touched it, and it may follow the monitor. Different ⇒
/// the player chose it, and it is kept unless it cannot work.</summary>
public sealed record DisplayStamp(string Resolution, string? Refresh)
{
    public const string FileName = ".display-detected";

    public static DisplayStamp? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var at = text.Trim().Split('@');
        if (at.Length is < 1 or > 2 || !DisplayMode.TryParseResolution(at[0], out _, out _)) return null;
        var hz = at.Length == 2 && at[1] != "?" ? at[1] : null;
        return new DisplayStamp(at[0], hz);
    }

    public override string ToString() => Resolution + "@" + (Refresh ?? "?");

    public static DisplayStamp For(DisplayMode mode) =>
        new(mode.Resolution, mode.HzKnown ? mode.Hz.ToString(CultureInfo.InvariantCulture) : null);

    /// <summary>The stored resolution is the one the launcher wrote.</summary>
    public bool ResolutionMatches(StoredDisplay stored) =>
        string.Equals(stored.Resolution?.Trim(), Resolution, StringComparison.OrdinalIgnoreCase);

    /// <summary>The stored refresh rate is the one the launcher wrote. A stamp without a rate
    /// (the monitor did not say) vouches for NO rate: whatever is stored then is the player's
    /// (review 2026-09-05, finding 1 — an unknown-rate stamp used to pass for "unchanged").</summary>
    public bool RefreshMatches(StoredDisplay stored) =>
        Refresh is not null && string.Equals(stored.Refresh?.Trim(), Refresh, StringComparison.Ordinal);
}

/// <summary>The outcome of <see cref="ClientDisplayPolicy.Decide"/>: what to write, and why in one
/// line for the log — the reason is the thing a support reader needs, not the values.</summary>
public sealed record DisplayDecision(
    string? Resolution, string? Refresh, string Reason)
{
    public bool WritesAnything => Resolution is not null || Refresh is not null;
    public static DisplayDecision Keep(string reason) => new(null, null, reason);
}

/// <summary>
/// Which resolution and refresh rate the 1.12.1 client should start with. Pure, so the rule can be
/// proven on every case the tickets describe without a monitor.
///
/// <para><b>Why this exists.</b> The client packages' loader scripts (<c>detect-display.ps1</c>,
/// <c>launch.sh</c>) wrote the primary monitor's mode into <c>Config.wtf</c> on EVERY start. That
/// fixed one thing (a stored resolution smaller than the monitor crops the login screen) and broke
/// three: a resolution the player picked never survived a restart, a refresh rate the player picked
/// (144 Hz) fell back to whatever the script read (60, or WMI's placeholder 1), and on a Windows
/// desktop with 125–150 % scaling the script read a DPI-virtualised size (1707x960 for a 2560x1440
/// panel) — a mode no monitor has. Leave windowed mode with THAT stored and the client opens an
/// exclusive-fullscreen device on a mode D3D9 cannot set: black screen, sound running, every restart,
/// until someone deletes Config.wtf. Support tickets #11, #63, #92, #96, #98 (2026-05…08).</para>
///
/// <para><b>The rule.</b> The player's choice wins. The launcher only writes when (a) nothing is
/// stored, (b) the stored value is the one the launcher itself wrote last time and the monitor has
/// since changed, or (c) the stored value cannot work: in exclusive fullscreen a mode the monitor
/// does not list (the black screen), in a window a size larger than the monitor (the crop). It
/// never caps the refresh rate (owner correction 2026-07-23: a detected value is taken as detected)
/// and never writes a refresh rate it did not measure.</para>
/// </summary>
public static class ClientDisplayPolicy
{
    public static DisplayDecision Decide(StoredDisplay stored, DisplayProbeResult probe, DisplayStamp? stamp)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(probe);
        var native = probe.Current;
        var nativeHz = native.HzKnown ? native.Hz.ToString(CultureInfo.InvariantCulture) : null;

        if (string.IsNullOrWhiteSpace(stored.Resolution))
            return new DisplayDecision(native.Resolution, nativeHz, "no resolution stored yet");

        if (!DisplayMode.TryParseResolution(stored.Resolution, out var w, out var h))
            return new DisplayDecision(native.Resolution, nativeHz,
                $"stored resolution '{stored.Resolution}' is not a size");

        if (stamp is not null && stamp.ResolutionMatches(stored))
        {
            // Ours from last time, untouched by the player: follow the monitor — but each value
            // only as far as the stamp vouches for it. The rate follows only when we wrote the
            // stored rate; a rate the player set (or one we never knew) stays.
            var resolutionMoved = !string.Equals(stamp.Resolution, native.Resolution, StringComparison.OrdinalIgnoreCase);
            if (resolutionMoved)
                return new DisplayDecision(native.Resolution, nativeHz, "launcher-written size, monitor changed");
            if (stamp.RefreshMatches(stored) && nativeHz is not null && stamp.Refresh != nativeHz)
                return new DisplayDecision(null, nativeHz, "launcher-written rate, monitor now reports another");
            if (!stamp.RefreshMatches(stored) && !string.IsNullOrWhiteSpace(stored.Refresh))
                return DisplayDecision.Keep("launcher-written size on the same monitor, the rate is the player's");
            return DisplayDecision.Keep("launcher-written value, monitor unchanged");
        }

        if (!stored.Windowed)
        {
            // Exclusive fullscreen: the mode has to exist on SOME attached monitor, or the device
            // cannot be created. The list covers every attached monitor (review 2026-09-05,
            // finding 2): a player in fullscreen on a secondary screen has a mode the primary may
            // not list, and "healing" that would cause the very black screen this prevents.
            if (probe.Modes.Count == 0)
                return DisplayDecision.Keep("player's fullscreen mode, mode list unavailable — not judged");
            var sameSize = probe.Modes.Where(m => m.Width == w && m.Height == h).ToList();
            if (sameSize.Count == 0)
                return new DisplayDecision(native.Resolution, nativeHz,
                    $"fullscreen mode {w}x{h} is not one any attached monitor has (this is the black screen)");
            // The rate has to exist for that size as well. A placeholder the old script wrote
            // (WMI's 0 or 1), a rate the monitor never had, or no rate at all: take the highest
            // the monitor lists for this size — the one thing the player cannot have meant less.
            var known = sameSize.Where(m => m.HzKnown).ToList();
            var hasRate = int.TryParse(stored.Refresh?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var hz);
            if (known.Count > 0 && (!hasRate || known.All(m => m.Hz != hz)))
            {
                var best = known.Max(m => m.Hz);
                return new DisplayDecision(null, best.ToString(CultureInfo.InvariantCulture),
                    $"fullscreen {w}x{h} exists but not at '{stored.Refresh ?? ""}' Hz — using the highest it has");
            }
            return DisplayDecision.Keep("player's fullscreen mode, available");
        }

        // Windowed: anything that fits on some attached monitor is fine; larger than all of them
        // crops the login screen's bottom row. Only the size is touched, the rate is the player's.
        var fits = probe.Modes.Count > 0
            ? probe.Modes.Any(m => m.Width >= w && m.Height >= h)
            : w <= native.Width && h <= native.Height;
        if (!fits)
            return new DisplayDecision(native.Resolution, null,
                $"windowed {w}x{h} is larger than any attached monitor and would be cropped");
        return DisplayDecision.Keep("player's windowed size, fits a monitor");
    }
}

/// <summary>The result of applying the policy to an install: <see cref="Handled"/> is true whenever
/// the launcher took responsibility for the display values (written or deliberately kept), so the
/// loader script must not detect again (<c>WOW_KEEP_RESOLUTION=1</c>).</summary>
public sealed record DisplayApplyResult(bool Handled, DisplayDecision? Decision, string? Error)
{
    public static DisplayApplyResult NotHandled(string why) => new(false, null, why);
}

public interface IClientDisplayService
{
    /// <summary>Whether this platform can read the monitor at all (macOS today: no).</summary>
    bool IsSupported { get; }

    /// <summary>Environment variable both loader scripts honour to skip their own detection.</summary>
    const string KeepResolutionEnvVar = "WOW_KEEP_RESOLUTION";

    DisplayApplyResult Apply(string clientDirectory);

    /// <summary>The way out of a black screen without a reinstall: windowed + maximised on the
    /// monitor's own mode. Everything else in Config.wtf (volume, keys, mouse) stays.</summary>
    bool ResetVideo(string clientDirectory, out string? error);
}

/// <summary>
/// Applies <see cref="ClientDisplayPolicy"/> to a 1.12.1 install: reads the three display lines and
/// the stamp, decides, writes through <see cref="WtfFile"/> (atomic, every other line preserved),
/// and records what it wrote in <c>WTF/.display-detected</c>. The package's own opt-out
/// (<c>WTF/.resolution-manual</c>) is honoured as "handled, nothing written".
/// </summary>
public sealed class ClientDisplayService : IClientDisplayService
{
    internal const string ManualMarker = ".resolution-manual";

    private readonly IDisplayProbe? _probe;
    private readonly Serilog.ILogger _log;

    public ClientDisplayService(IDisplayProbe? probe, Serilog.ILogger log)
    {
        _probe = probe;
        _log = log;
    }

    public bool IsSupported => _probe is not null;

    public DisplayApplyResult Apply(string clientDirectory)
    {
        if (_probe is null) return DisplayApplyResult.NotHandled("no display probe on this platform");
        var wtf = Path.Combine(clientDirectory, "WTF");
        if (File.Exists(Path.Combine(wtf, ManualMarker)))
        {
            _log.Information("Display: {Marker} present — the player's resolution is not touched", ManualMarker);
            return new DisplayApplyResult(true, DisplayDecision.Keep("manual marker"), null);
        }

        DisplayProbeResult? probe;
        try { probe = _probe.Probe(); }
        catch (Exception ex)
        {
            _log.Warning(ex, "Display: the monitor could not be read — leaving the client's values alone");
            return DisplayApplyResult.NotHandled("probe threw: " + ex.Message);
        }
        if (probe is null)
        {
            _log.Warning("Display: the monitor could not be read — leaving the client's values alone");
            return DisplayApplyResult.NotHandled("probe returned nothing");
        }

        try
        {
            Directory.CreateDirectory(wtf);
            var config = Path.Combine(wtf, "Config.wtf");
            var stored = ReadStored(config);
            var stampPath = Path.Combine(wtf, DisplayStamp.FileName);
            var stamp = File.Exists(stampPath) ? DisplayStamp.Parse(File.ReadAllText(stampPath)) : null;

            var decision = ClientDisplayPolicy.Decide(stored, probe, stamp);
            _log.Information(
                "Display: monitor {Native}@{Hz}Hz ({Modes} modes), stored {Res}@{Ref} {Mode}, last written {Stamp} → {Reason}",
                probe.Current.Resolution, probe.Current.HzKnown ? probe.Current.Hz : 0, probe.Modes.Count,
                stored.Resolution ?? "-", stored.Refresh ?? "-", stored.Windowed ? "windowed" : "fullscreen",
                stamp?.ToString() ?? "-", decision.Reason);

            if (decision.Resolution is not null) WtfFile.SetVar(config, "gxResolution", decision.Resolution);
            if (decision.Refresh is not null) WtfFile.SetVar(config, "gxRefresh", decision.Refresh);
            if (decision.WritesAnything)
            {
                var written = new DisplayStamp(
                    decision.Resolution ?? stored.Resolution!.Trim(),
                    decision.Refresh ?? (decision.Resolution is not null ? null : stored.Refresh?.Trim()));
                WtfFile.WriteAtomic(stampPath, written + "\n");
            }
            return new DisplayApplyResult(true, decision, null);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Display: could not apply the resolution policy in {Dir}", clientDirectory);
            return DisplayApplyResult.NotHandled("write failed: " + ex.Message);
        }
    }

    public bool ResetVideo(string clientDirectory, out string? error)
    {
        error = null;
        if (_probe is null) { error = "no display probe on this platform"; return false; }
        try
        {
            var probe = _probe.Probe();
            if (probe is null) { error = "the monitor could not be read"; return false; }
            var wtf = Path.Combine(clientDirectory, "WTF");
            Directory.CreateDirectory(wtf);
            var config = Path.Combine(wtf, "Config.wtf");
            WtfFile.SetVar(config, "gxWindow", "1");
            WtfFile.SetVar(config, "gxMaximize", "1");
            WtfFile.SetVar(config, "gxResolution", probe.Current.Resolution);
            if (probe.Current.HzKnown)
                WtfFile.SetVar(config, "gxRefresh", probe.Current.Hz.ToString(CultureInfo.InvariantCulture));
            var marker = Path.Combine(wtf, ManualMarker);
            if (File.Exists(marker)) File.Delete(marker);
            WtfFile.WriteAtomic(Path.Combine(wtf, DisplayStamp.FileName), DisplayStamp.For(probe.Current) + "\n");
            _log.Information("Display: video settings reset to windowed+maximised {Mode}@{Hz}Hz in {Dir}",
                probe.Current.Resolution, probe.Current.Hz, clientDirectory);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Display: video reset failed in {Dir}", clientDirectory);
            error = ex.Message;
            return false;
        }
    }

    internal static StoredDisplay ReadStored(string configPath)
    {
        var window = WtfFile.ReadVar(configPath, "gxWindow");
        // The client's own default is fullscreen; only an explicit "1" is a window.
        var windowed = string.Equals(window?.Trim(), "1", StringComparison.Ordinal);
        return new StoredDisplay(
            WtfFile.ReadVar(configPath, "gxResolution"),
            WtfFile.ReadVar(configPath, "gxRefresh"),
            windowed);
    }
}
