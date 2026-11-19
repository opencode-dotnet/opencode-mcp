using TUnit.Assertions.Enums;

namespace OpenCode.Mcp.Tools.Tests.Native.Support;

public sealed class ManualTimeProviderTests
{
    [Test]
    public async Task CreateTimer_Should_Register_An_Armed_Timer_Before_Returning()
    {
        var clock = new ManualTimeProvider();
        using var timer = clock.CreateTimer(static _ => { }, null, TimeSpan.FromMilliseconds(10), Timeout.InfiniteTimeSpan);

        await Assert.That(clock.ArmedTimerCount).IsEqualTo(1);
    }

    [Test]
    public async Task Advance_Should_Fire_Timers_In_Due_Then_Registration_Order()
    {
        var clock = new ManualTimeProvider();
        var calls = new List<string>();
        using var later = clock.CreateTimer(_ => calls.Add("later"), null, TimeSpan.FromMilliseconds(20), Timeout.InfiniteTimeSpan);
        using var first = clock.CreateTimer(_ => calls.Add("first"), null, TimeSpan.FromMilliseconds(10), Timeout.InfiniteTimeSpan);
        using var second = clock.CreateTimer(_ => calls.Add("second"), null, TimeSpan.FromMilliseconds(10), Timeout.InfiniteTimeSpan);

        await Assert.That(clock.Advance(TimeSpan.FromMilliseconds(20))).IsEqualTo(3);
        await Assert.That(calls).IsEquivalentTo(["first", "second", "later"], CollectionOrdering.Matching);
        await Assert.That(clock.ArmedTimerCount).IsEqualTo(0);
    }

    [Test]
    public async Task Change_Should_Replace_The_Due_Time_And_Disable_An_Armed_Timer()
    {
        var clock = new ManualTimeProvider();
        var calls = new List<string>();
        using var timer = clock.CreateTimer(_ => calls.Add("fired"), null, TimeSpan.FromMilliseconds(5), Timeout.InfiniteTimeSpan);

        await Assert.That(timer.Change(TimeSpan.FromMilliseconds(10), Timeout.InfiniteTimeSpan)).IsTrue();
        await Assert.That(clock.Advance(TimeSpan.FromMilliseconds(5))).IsEqualTo(0);
        await Assert.That(calls).IsEmpty();
        await Assert.That(clock.Advance(TimeSpan.FromMilliseconds(5))).IsEqualTo(1);
        await Assert.That(calls).IsEquivalentTo(["fired"], CollectionOrdering.Matching);
        await Assert.That(timer.Change(TimeSpan.FromMilliseconds(5), Timeout.InfiniteTimeSpan)).IsTrue();
        await Assert.That(timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan)).IsTrue();
        await Assert.That(clock.ArmedTimerCount).IsEqualTo(0);
        await Assert.That(clock.Advance(TimeSpan.FromMilliseconds(20))).IsEqualTo(0);
        await Assert.That(calls).IsEquivalentTo(["fired"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Dispose_Should_Cancel_Timers_Before_They_Fire()
    {
        var clock = new ManualTimeProvider();
        var calls = new List<string>();
        var disposed = clock.CreateTimer(_ => calls.Add("disposed"), null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        var asyncDisposed = clock.CreateTimer(_ => calls.Add("async disposed"), null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        using var live = clock.CreateTimer(_ => calls.Add("live"), null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);

        disposed.Dispose();
        await asyncDisposed.DisposeAsync();
        await Assert.That(disposed.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan)).IsFalse();
        await Assert.That(asyncDisposed.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan)).IsFalse();
        await Assert.That(clock.Advance(TimeSpan.Zero)).IsEqualTo(1);
        await Assert.That(calls).IsEquivalentTo(["live"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task CreateTimer_Should_Keep_Infinite_Due_Time_Disarmed_Until_Changed()
    {
        var clock = new ManualTimeProvider();
        var calls = new List<string>();
        using var timer = clock.CreateTimer(_ => calls.Add("fired"), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        await Assert.That(clock.ArmedTimerCount).IsEqualTo(0);
        await Assert.That(clock.Advance(TimeSpan.FromSeconds(1))).IsEqualTo(0);
        await Assert.That(timer.Change(TimeSpan.Zero, TimeSpan.Zero)).IsTrue();
        await Assert.That(clock.Advance(TimeSpan.Zero)).IsEqualTo(1);
        await Assert.That(clock.Advance(TimeSpan.FromSeconds(1))).IsEqualTo(0);
        await Assert.That(calls).IsEquivalentTo(["fired"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Advance_Should_Fire_Periodic_Timers_At_Each_Due_Time()
    {
        var clock = new ManualTimeProvider();
        var calls = new List<DateTimeOffset>();
        using var timer = clock.CreateTimer(_ => calls.Add(clock.GetUtcNow()), null, TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(3));
        var before = clock.GetTimestamp();

        await Assert.That(clock.Advance(TimeSpan.FromMilliseconds(12))).IsEqualTo(3);
        await Assert.That(calls).IsEquivalentTo([DateTimeOffset.UnixEpoch.AddMilliseconds(5), DateTimeOffset.UnixEpoch.AddMilliseconds(8),
            DateTimeOffset.UnixEpoch.AddMilliseconds(11)], CollectionOrdering.Matching);
        await Assert.That(clock.GetUtcNow()).IsEqualTo(DateTimeOffset.UnixEpoch.AddMilliseconds(12));
        await Assert.That(clock.GetElapsedTime(before)).IsEqualTo(TimeSpan.FromMilliseconds(12));
    }

    [Test]
    public async Task Advance_Should_Observe_Timer_Changes_Made_By_Callbacks()
    {
        var clock = new ManualTimeProvider();
        var calls = new List<string>();
        using var cancelled = clock.CreateTimer(_ => calls.Add("cancelled"), null, TimeSpan.FromMilliseconds(2), Timeout.InfiniteTimeSpan);
        using var changed = clock.CreateTimer(_ => calls.Add("changed"), null, TimeSpan.FromMilliseconds(3), Timeout.InfiniteTimeSpan);
        ITimer? created = null;
        using var first = clock.CreateTimer(_ =>
        {
            cancelled.Dispose();
            created = clock.CreateTimer(_ => calls.Add("created"), null, TimeSpan.FromMilliseconds(1), Timeout.InfiniteTimeSpan);
            changed.Change(TimeSpan.FromMilliseconds(1), Timeout.InfiniteTimeSpan);
            calls.Add("first");
        }, null, TimeSpan.FromMilliseconds(1), Timeout.InfiniteTimeSpan);

        await Assert.That(clock.Advance(TimeSpan.FromMilliseconds(2))).IsEqualTo(3);
        await Assert.That(calls).IsEquivalentTo(["first", "created", "changed"], CollectionOrdering.Matching);
        created?.Dispose();
    }

    [Test]
    public async Task Advance_Should_Reject_Negative_Amounts()
    {
        var clock = new ManualTimeProvider();

        await Assert.That(() => clock.Advance(TimeSpan.FromMilliseconds(-1))).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Advance_Should_Allow_A_Callback_To_Dispose_Its_Own_Timer()
    {
        var clock = new ManualTimeProvider();
        var calls = new List<string>();
        ITimer? timer = null;
        using var ownedTimer = timer = clock.CreateTimer(_ =>
        {
            calls.Add("fired");
            timer!.Dispose();
        }, null, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1));

        await Assert.That(clock.Advance(TimeSpan.FromMilliseconds(10))).IsEqualTo(1);
        await Assert.That(calls).IsEquivalentTo(["fired"], CollectionOrdering.Matching);
        await Assert.That(clock.ArmedTimerCount).IsEqualTo(0);
    }

    [Test]
    [Arguments(-2, 0)]
    [Arguments(0, -2)]
    public async Task CreateTimer_Should_Reject_Invalid_Negative_Durations(int dueMilliseconds, int periodMilliseconds)
    {
        var clock = new ManualTimeProvider();
        var due = TimeSpan.FromMilliseconds(dueMilliseconds);
        var period = TimeSpan.FromMilliseconds(periodMilliseconds);
        using var timer = clock.CreateTimer(static _ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        await Assert.That(() => clock.CreateTimer(static _ => { }, null, due, period)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => timer.Change(due, period)).Throws<ArgumentOutOfRangeException>();
    }
}
