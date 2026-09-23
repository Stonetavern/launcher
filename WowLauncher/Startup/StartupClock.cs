using System;
using System.Diagnostics;

namespace WowLauncher.Startup;

/// <summary>
/// One stopwatch from the first line of <c>Main</c>, so "time to first frame" (Spec §4, budget
/// 0 to 800 ms) is measured against the process, not against whenever a window happened to be
/// constructed. The login shell logs the first rendered frame against this clock.
/// </summary>
public static class StartupClock
{
    private static readonly Stopwatch Clock = new();

    /// <summary>Called once, first thing in <c>Main</c>. Idempotent.</summary>
    public static void Start()
    {
        if (!Clock.IsRunning) Clock.Start();
    }

    public static TimeSpan Elapsed => Clock.Elapsed;
}
