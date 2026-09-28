using System;
using System.IO;
using System.Threading;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// One launcher per player: a second start brings the running one forward and quits. There was no
/// lock at all before (two windows fought over the config and the realm proxy), and the Stonetavern
/// folder made a second start more likely (E2E 2026-09-28).
/// </summary>
public sealed class SingleInstanceTests : IDisposable
{
    // Short on purpose: a Unix socket path is limited to about 104 bytes.
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "stsi-" + Guid.NewGuid().ToString("N")[..8]);
    public SingleInstanceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (Exception) { } }

    private SingleInstance New() => new(Path.Combine(_dir, "l.lock"), Path.Combine(_dir, "l.sock"));

    [Fact]
    public void ASecondStart_FindsTheLockHeld_UntilTheFirstLetsGo()
    {
        using var first = New();
        using var second = New();

        Assert.True(first.TryAcquire());
        Assert.False(second.TryAcquire());

        first.Release();
        Assert.True(second.TryAcquire());
    }

    [Fact]
    public void ASecondStart_BringsTheFirstToTheFront()
    {
        using var first = New();
        Assert.True(first.TryAcquire());
        using var shown = new ManualResetEventSlim();
        first.Listen(() => shown.Set());

        var delivered = New().SignalFirst(TimeSpan.FromSeconds(3));

        Assert.True(delivered);
        Assert.True(shown.Wait(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void NobodyListening_MeansStartAsAlways()
    {
        Assert.False(New().SignalFirst(TimeSpan.FromSeconds(1)));
    }

    /// <summary>A crash leaves the socket file behind; the next launcher holds the lock and must still
    /// be reachable, or every later start would open a second window again.</summary>
    [Fact]
    public void ASocketLeftByACrash_DoesNotBlockTheNextLauncher()
    {
        File.WriteAllText(Path.Combine(_dir, "l.sock"), "left over");
        using var first = New();
        Assert.True(first.TryAcquire());
        using var shown = new ManualResetEventSlim();
        first.Listen(() => shown.Set());

        Assert.True(New().SignalFirst(TimeSpan.FromSeconds(3)));
        Assert.True(shown.Wait(TimeSpan.FromSeconds(3)));
    }

    /// <summary>The setup hands the role to the launcher in the Stonetavern folder: after Release that
    /// one takes the lock and is the one a later start reaches.</summary>
    [Fact]
    public void AfterTheHandOn_TheSuccessorIsTheOneReached()
    {
        using var original = New();
        Assert.True(original.TryAcquire());
        var originalShown = 0;
        original.Listen(() => Interlocked.Increment(ref originalShown));

        original.Release();
        using var successor = New();
        Assert.True(successor.TryAcquire());
        using var shown = new ManualResetEventSlim();
        successor.Listen(() => shown.Set());

        Assert.True(New().SignalFirst(TimeSpan.FromSeconds(3)));
        Assert.True(shown.Wait(TimeSpan.FromSeconds(3)));
        Assert.Equal(0, originalShown);
    }

    /// <summary>Two launchers with different state folders hold different locks; the second must not take
    /// the first one's socket over (one fixed socket name let it, E2E harness 2026-09-28).</summary>
    [Fact]
    public void ALauncherWithAnotherStateFolder_LeavesTheFirstReachable()
    {
        var dirA = Path.Combine(_dir, "a");
        var dirB = Path.Combine(_dir, "b");
        using var first = SingleInstance.ForCurrentUser(dirA);
        Assert.True(first.TryAcquire());
        using var shownFirst = new ManualResetEventSlim();
        first.Listen(() => shownFirst.Set());

        using var other = SingleInstance.ForCurrentUser(dirB);
        Assert.True(other.TryAcquire());
        var otherShown = 0;
        other.Listen(() => Interlocked.Increment(ref otherShown));

        using var secondStartOfFirst = SingleInstance.ForCurrentUser(dirA);
        Assert.False(secondStartOfFirst.TryAcquire());
        Assert.True(secondStartOfFirst.SignalFirst(TimeSpan.FromSeconds(3)));
        Assert.True(shownFirst.Wait(TimeSpan.FromSeconds(3)));
        Assert.Equal(0, Volatile.Read(ref otherShown));
    }

    [Fact]
    public void TheSocketName_FollowsTheLock_AndFitsASocketPath()
    {
        var a = SingleInstance.SocketNameFor(Path.Combine(_dir, "a", "launcher.instance.lock"));
        Assert.Equal(a, SingleInstance.SocketNameFor(Path.Combine(_dir, "a", "launcher.instance.lock")));
        Assert.NotEqual(a, SingleInstance.SocketNameFor(Path.Combine(_dir, "b", "launcher.instance.lock")));
        Assert.Matches("^stonetavern-launcher-[0-9a-f]{12}\\.sock$", a);
    }
}
