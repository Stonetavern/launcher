using System;
using CommunityToolkit.Mvvm.ComponentModel;
using WowLauncher.Localization;

namespace WowLauncher.ViewModels;

/// <summary>
/// The right-hand telemetry column of the v2 skin.
///
/// The launcher does not lie (BRAND_BIBLE §3, DESIGN-UI-ENTERPRISE §1.4): a figure is shown only when
/// it comes from a real source. Everything not yet wired to one reads "—" and the block hides itself.
/// <c>--demo</c> fills the not-yet-wired figures with representative values so the design can be judged
/// on a machine that has no realm connection; it is a QA switch, never a default.
/// </summary>
public sealed partial class TelemetryViewModel : ViewModelBase
{
    private readonly PlayViewModel _play;

    public TelemetryViewModel(PlayViewModel play, bool demo)
    {
        _play = play;
        Demo = demo;

        // The realm check finishes after the view is bound, so the column has to hear about it.
        // Without this the pulse label stays frozen on its first (still-checking) value.
        _play.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(PlayViewModel.Realm):
                    // FIX 3 (v1.0.1): PlayerCount no longer projected — the live count is never shown.
                    OnPropertyChanged(nameof(RealmOnline));
                    OnPropertyChanged(nameof(RealmStatusUpper));
                    OnPropertyChanged(nameof(PulseText));
                    break;
                case nameof(PlayViewModel.RealmAddress):
                    OnPropertyChanged(nameof(RealmAddress));
                    OnPropertyChanged(nameof(MetaLine));
                    break;
            }
        };
    }

    public bool Demo { get; }

    /// <summary>The world behind the launcher. One realm today; the profile knows its name.</summary>
    public string RealmName => "Elwynn";

    /// <summary>Mono strapline under the display anchor — what this world is, in the machine's voice.</summary>
    public string MetaLine =>
        $"{_play.EraName.ToUpperInvariant()} 1.12.1  ·  PVE  ·  {RealmAddress}";

    /// <summary>The status in the instrument's voice: mono, uppercase.</summary>
    public string RealmStatusUpper => _play.RealmStatusText.ToUpperInvariant();

    // ─── Real, right now ──────────────────────────────────────────────────
    /// <summary>Server clock. The realm runs on Europe/Vienna, same as the workstation.</summary>
    public string ServerTime => DateTime.Now.ToString("HH:mm");

    /// <summary>Next weekly reset: Sunday 04:00. Deterministic, so no server round-trip needed.</summary>
    public string NextReset
    {
        get
        {
            var now = DateTime.Now;
            var days = ((int)DayOfWeek.Sunday - (int)now.DayOfWeek + 7) % 7;
            var reset = now.Date.AddDays(days).AddHours(4);
            if (reset <= now) reset = reset.AddDays(7);
            return $"{Weekday(reset.DayOfWeek)} {reset:HH:mm}";
        }
    }

    private static string Weekday(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => Loc.T("Telemetry_Day_Mon"),
        DayOfWeek.Tuesday => Loc.T("Telemetry_Day_Tue"),
        DayOfWeek.Wednesday => Loc.T("Telemetry_Day_Wed"),
        DayOfWeek.Thursday => Loc.T("Telemetry_Day_Thu"),
        DayOfWeek.Friday => Loc.T("Telemetry_Day_Fri"),
        DayOfWeek.Saturday => Loc.T("Telemetry_Day_Sat"),
        _ => Loc.T("Telemetry_Day_Sun"),
    };

    // ─── Not wired to a source yet — honest dashes unless --demo ──────────
    // TODO(engine): the realm's status endpoint does not report these yet. Wire them there,
    // do not invent them here.
    public string Latency => Demo ? "24 ms" : "—";
    public string Uptime => Demo ? "11d 04h 22m" : "—";

    public bool HasPhase => Demo;
    public int PhaseDone => Demo ? 61 : 0;
    public int PhaseGoal => Demo ? 80 : 0;
    public string PhaseText => $"{PhaseDone} / {PhaseGoal}";
    public double PhaseRatio => PhaseGoal == 0 ? 0 : PhaseDone / (double)PhaseGoal;

    public bool HasFactions => Demo;
    public int Alliance => Demo ? 54 : 0;
    public int Horde => Demo ? 46 : 0;
    public string AllianceText => Loc.F("Telemetry_Alliance", Alliance);
    public string HordeText => Loc.F("Telemetry_Horde", Horde);

    // The client's WTF config knows the last character played. Reading it is the strongest
    // "this world is yours" signal available — but it must be read, not guessed.
    public bool HasCharacter => Demo;
    public string CharacterName => Demo ? "Ardan" : "—";
    public string CharacterClass => Demo ? "60 · WARRIOR" : "";
    public string CharacterZone => Demo ? "BURNING STEPPES · /PLAYED 21d 07h" : "";

    public string ClientHash => Demo ? "9cd0a7…eb0b" : "—";

    // ─── Pass-through of the live realm state (already real) ──────────────
    public bool RealmOnline => _play.RealmOnline;
    public string RealmAddress => _play.RealmAddress;

    /// <summary>The pulse label. FIX 3 (v1.0.1): the momentary player count is never shown
    /// (owner directive, consistent with the website /play — a small live number scares people
    /// off). Only the honest up/down status remains; latency stays a dash until the realm endpoint
    /// reports it.</summary>
    public string PulseText => RealmOnline
        ? Loc.F("Telemetry_Pulse_Online", Latency)
        : _play.RealmChecking
            ? Loc.T("Telemetry_Pulse_Checking") + "…"
            : Loc.T("Telemetry_Pulse_Down");
}
