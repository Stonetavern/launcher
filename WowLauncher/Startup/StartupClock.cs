using System.Collections.Concurrent;
using System.Diagnostics;

namespace WowLauncher.Startup;

/// <summary>
/// One stopwatch from the first line of <c>Main</c>, so "time to first frame" (Spec §4, budget
/// 0 to 800 ms) is measured against the process, not against whenever a window happened to be
/// constructed. The login shell logs the first rendered frame against this clock.
///
/// <para><b>Marks.</b> Named points on the way to the first frame (framework up, DI host, config, the
/// login window built, ...), written as ONE line when the first frame is on screen. Added 2026-09-28
/// after a start that took 12.5 s to its first frame on the owner's machine (1.1 s is normal) and
/// could not be attributed: the login path logged only the end result, and nothing before the DI
/// host existed. <see cref="BeforeMain"/> covers the part before <c>Main</c> (runtime start, the
/// AppImage mount), which no stopwatch inside the process can see.</para>
/// </summary>
public static class StartupClock
{
    private static readonly Stopwatch Clock = new();
    private static readonly ConcurrentQueue<(string Name, double Ms)> Marks = new();

    /// <summary>Called once, first thing in <c>Main</c>. Idempotent.</summary>
    public static void Start()
    {
        if (Clock.IsRunning) return;
        Clock.Start();
        try
        {
            // The OS start time of this process against now: what passed before Main ran. Best effort,
            // a platform that cannot tell leaves it null.
            BeforeMain = DateTime.Now - Process.GetCurrentProcess().StartTime;
        }
        catch (Exception)
        {
            BeforeMain = null;
        }
    }

    public static TimeSpan Elapsed => Clock.Elapsed;

    /// <summary>Time between the process being created and <c>Main</c>, or null when unknown.</summary>
    public static TimeSpan? BeforeMain { get; private set; }

    /// <summary>Record a named point on the start path. Cheap; safe from any thread.</summary>
    public static void Mark(string name) => Marks.Enqueue((name, Clock.Elapsed.TotalMilliseconds));

    /// <summary>"before-main 180 ms | framework 340, host 395, ..." for the log.</summary>
    public static string Summary()
    {
        var head = BeforeMain is { } b ? $"before-main {b.TotalMilliseconds:F0} ms | " : "";
        return head + string.Join(", ", Marks.Select(m => $"{m.Name} {m.Ms:F0}"));
    }
}
