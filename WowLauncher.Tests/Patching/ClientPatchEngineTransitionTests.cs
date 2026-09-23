using System;
using WowLauncher.Services.Patching;
using Xunit;

namespace WowLauncher.Tests.Patching;

/// <summary>
/// The explicit transition table (ARCHITEKTUR-v2-patcher.md §4: "Nicht gelistete Übergänge werfen —
/// Lumenia-Lehre: der tote Reparieren-Knopf"). Exercises <see cref="ClientPatchEngine.Transition"/>
/// directly via <c>InternalsVisibleTo</c> rather than driving a whole <see cref="ClientPatchEngine"/>
/// run for every case — the table itself is the unit under test here.
/// </summary>
public sealed class ClientPatchEngineTransitionTests
{
    [Theory]
    [InlineData(PatchState.Idle, PatchState.LoadFilesJson)]
    [InlineData(PatchState.LoadFilesJson, PatchState.Plan)]
    [InlineData(PatchState.LoadFilesJson, PatchState.Error)]
    [InlineData(PatchState.Plan, PatchState.FullZip)]
    [InlineData(PatchState.Plan, PatchState.Delta)]
    [InlineData(PatchState.Plan, PatchState.PerFile)]
    [InlineData(PatchState.Plan, PatchState.UpToDate)]
    [InlineData(PatchState.Plan, PatchState.GameRunning)]
    [InlineData(PatchState.Plan, PatchState.Error)]
    [InlineData(PatchState.FullZip, PatchState.Verify)]
    [InlineData(PatchState.Delta, PatchState.Verify)]
    [InlineData(PatchState.Delta, PatchState.PerFile)]
    [InlineData(PatchState.PerFile, PatchState.Verify)]
    [InlineData(PatchState.Verify, PatchState.WriteState)]
    [InlineData(PatchState.Verify, PatchState.PerFile)]
    [InlineData(PatchState.WriteState, PatchState.Ready)]
    [InlineData(PatchState.UpToDate, PatchState.Ready)]
    public void Legal_transitions_do_not_throw(PatchState from, PatchState to)
    {
        var ex = Record.Exception(() => ClientPatchEngine.Transition(from, to));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData(PatchState.Idle, PatchState.Plan)]              // skips LOAD_FILES_JSON
    [InlineData(PatchState.Idle, PatchState.Ready)]              // skips everything
    [InlineData(PatchState.Plan, PatchState.WriteState)]         // skips the route entirely
    [InlineData(PatchState.Plan, PatchState.Ready)]              // skips VERIFY/WRITE_STATE (except via UpToDate)
    [InlineData(PatchState.FullZip, PatchState.PerFile)]         // FULL_ZIP has no PER_FILE fallback
    [InlineData(PatchState.PerFile, PatchState.PerFile)]         // no self-loop in the table (round limit is business logic)
    [InlineData(PatchState.Ready, PatchState.Plan)]              // terminal state, cannot resume in place
    [InlineData(PatchState.Error, PatchState.Ready)]             // terminal state
    [InlineData(PatchState.GameRunning, PatchState.PerFile)]     // terminal state, must not silently proceed
    [InlineData(PatchState.WriteState, PatchState.UpToDate)]     // not a real predecessor of UpToDate
    public void Illegal_transitions_throw(PatchState from, PatchState to)
    {
        Assert.Throws<InvalidOperationException>(() => ClientPatchEngine.Transition(from, to));
    }

    /// <summary>Every state the enum declares appears as a KEY in the table (even if its value is an
    /// empty array for a terminal state) — a state missing from the table entirely would make every
    /// outgoing transition from it throw "not in the transition table", which reads identically to a
    /// genuinely forbidden transition and would be easy to miss in a review.</summary>
    [Fact]
    public void Every_declared_state_has_a_table_entry()
    {
        foreach (var state in Enum.GetValues<PatchState>())
            Assert.True(ClientPatchEngine.Transitions.ContainsKey(state), $"{state} has no entry in the transition table");
    }
}
