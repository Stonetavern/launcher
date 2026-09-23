using System.Text.Json.Serialization;

namespace WowLauncher.Services.AgentControl;

/// <summary>
/// What the launcher looks like right now, in the words a test harness needs.
///
/// <para>Deliberately the same facts the window shows, not more: a harness that reads private
/// internals would pass while the visible app is broken. <see cref="Headline"/> and
/// <see cref="Detail"/> are the very strings on screen, so an assertion here is an assertion about
/// what a player reads.</para>
/// </summary>
public sealed record AgentState
{
    /// <summary><c>LauncherState</c> as its name (Ready, UpdateAvailable, Downloading …).</summary>
    [JsonPropertyName("state")] public required string State { get; init; }

    /// <summary>The primary action's label — the text on the big button.</summary>
    [JsonPropertyName("headline")] public required string Headline { get; init; }

    /// <summary>The line under it: ready detail, download detail, or an error.</summary>
    [JsonPropertyName("detail")] public required string Detail { get; init; }

    /// <summary>0-100 while downloading or verifying, otherwise 0.</summary>
    [JsonPropertyName("progress")] public double Progress { get; init; }

    /// <summary>True while a run owns the app (downloading, verifying, launching).</summary>
    [JsonPropertyName("busy")] public bool Busy { get; init; }

    /// <summary>Client build the picker currently has selected.</summary>
    [JsonPropertyName("clientBuild")] public int? ClientBuild { get; init; }

    /// <summary>
    /// The second line under the status — whatever the window shows there, which depending on the
    /// state is a hint, a client path, or nothing.
    ///
    /// <para>Named after its place on screen rather than its content on purpose: an earlier draft
    /// called this <c>clientPath</c> and the very first live call returned "Download the 1.12.1 (5875)
    /// client straight from our server." into it. A field whose name promises a path and sometimes
    /// hands back a sentence is a trap for whoever writes the assertion.</para>
    /// </summary>
    [JsonPropertyName("subLine")] public string? SubLine { get; init; }

    /// <summary>
    /// Every command by name, with whether it can run right now. The second half is the point:
    /// "Repair exists" is uninteresting, "Repair is enabled in this state" is the assertion.
    /// </summary>
    [JsonPropertyName("commands")] public required IReadOnlyDictionary<string, bool> Commands { get; init; }
}

/// <summary>Outcome of one command invocation.</summary>
/// <param name="Accepted">False when the command is unknown or its guard says no. A refusal is a
/// normal answer, not an error — a harness asserts on it.</param>
/// <param name="Message">Why it was refused, or what was started.</param>
public sealed record AgentCommandResult(
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("message")] string Message);

/// <summary>
/// The running launcher, as far as the control surface is allowed to touch it.
///
/// <para>An interface rather than a direct reference to the ViewModel, for two reasons: the HTTP
/// layer stays testable with no window and no Avalonia at all, and the set of reachable commands is
/// written down in one place instead of being "whatever happens to be public".</para>
/// </summary>
public interface IAgentControlSurface
{
    /// <summary>Reads the current state. Marshals to the UI thread itself where that is needed.</summary>
    Task<AgentState> GetStateAsync(CancellationToken ct = default);

    /// <summary>
    /// Runs one command by name and returns once it has been <em>started</em>, not once it has
    /// finished — <c>Update</c> takes gigabytes and minutes. A harness presses the button, then polls
    /// <see cref="GetStateAsync"/>, exactly as a person would watch the window.
    /// </summary>
    Task<AgentCommandResult> InvokeAsync(string command, CancellationToken ct = default);
}
