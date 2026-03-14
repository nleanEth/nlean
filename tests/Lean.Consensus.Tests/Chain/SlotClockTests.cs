using Lean.Consensus.Chain;
using NUnit.Framework;

namespace Lean.Consensus.Tests.Chain;

[TestFixture]
public sealed class SlotClockTests
{
    private const int SecondsPerSlot = 4;
    private const int IntervalsPerSlot = 5;

    // 800ms per interval = 4000ms / 5
    private static readonly DateTimeOffset GenesisTime = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public void CurrentSlot_AtGenesis_IsZero()
    {
        var clock = CreateClock(GenesisTime);
        Assert.That(clock.CurrentSlot, Is.EqualTo(0UL));
    }

    [Test]
    public void CurrentSlot_AfterOneSlot_IsOne()
    {
        var clock = CreateClock(GenesisTime.AddSeconds(SecondsPerSlot));
        Assert.That(clock.CurrentSlot, Is.EqualTo(1UL));
    }

    [Test]
    public void CurrentSlot_MidSlot_StillSameSlot()
    {
        var clock = CreateClock(GenesisTime.AddMilliseconds(2500));
        Assert.That(clock.CurrentSlot, Is.EqualTo(0UL));
    }

    [Test]
    public void CurrentInterval_AtSlotStart_IsZero()
    {
        var clock = CreateClock(GenesisTime);
        Assert.That(clock.CurrentInterval, Is.EqualTo(0));
    }

    [Test]
    public void CurrentInterval_At800ms_IsOne()
    {
        // 800ms = 1 interval (4000ms / 5 = 800ms per interval)
        var clock = CreateClock(GenesisTime.AddMilliseconds(800));
        Assert.That(clock.CurrentInterval, Is.EqualTo(1));
    }

    [Test]
    public void CurrentInterval_At3200ms_IsFour()
    {
        // 3200ms = 4 intervals
        var clock = CreateClock(GenesisTime.AddMilliseconds(3200));
        Assert.That(clock.CurrentInterval, Is.EqualTo(4));
    }

    [Test]
    public void CurrentInterval_AtNextSlotStart_IsZero()
    {
        var clock = CreateClock(GenesisTime.AddSeconds(SecondsPerSlot));
        Assert.That(clock.CurrentInterval, Is.EqualTo(0));
    }

    [Test]
    public void TotalIntervals_Calculation()
    {
        // At slot 2, interval 3 => total = 2*5 + 3 = 13
        var clock = CreateClock(GenesisTime.AddMilliseconds(2 * 4000 + 3 * 800));
        Assert.That(clock.CurrentSlot, Is.EqualTo(2UL));
        Assert.That(clock.CurrentInterval, Is.EqualTo(3));
        Assert.That(clock.TotalIntervals, Is.EqualTo(13UL));
    }

    [Test]
    public void SecondsUntilNextInterval_IsPositive()
    {
        // At genesis exactly, next interval is 800ms away
        var clock = CreateClock(GenesisTime);
        var seconds = clock.SecondsUntilNextInterval;
        Assert.That(seconds, Is.GreaterThan(0.0));
        Assert.That(seconds, Is.LessThanOrEqualTo(0.8));
    }

    [Test]
    public void SecondsUntilNextInterval_MidInterval()
    {
        // 400ms into first interval => 400ms remaining
        var clock = CreateClock(GenesisTime.AddMilliseconds(400));
        var seconds = clock.SecondsUntilNextInterval;
        Assert.That(seconds, Is.EqualTo(0.4).Within(0.01));
    }

    [Test]
    public void BeforeGenesis_SlotIsZero()
    {
        var clock = CreateClock(GenesisTime.AddSeconds(-10));
        Assert.That(clock.CurrentSlot, Is.EqualTo(0UL));
        Assert.That(clock.CurrentInterval, Is.EqualTo(0));
        Assert.That(clock.TotalIntervals, Is.EqualTo(0UL));
    }

    [Test]
    public void LargeSlotNumber_DoesNotOverflow()
    {
        // 1 million seconds after genesis
        var clock = CreateClock(GenesisTime.AddSeconds(1_000_000));
        Assert.That(clock.CurrentSlot, Is.EqualTo(250_000UL));
    }

    private static SlotClock CreateClock(DateTimeOffset now)
    {
        var timeSource = new FakeTimeSource(now);
        return new SlotClock(
            (ulong)GenesisTime.ToUnixTimeSeconds(),
            SecondsPerSlot,
            IntervalsPerSlot,
            timeSource);
    }

    private sealed class FakeTimeSource : ITimeSource
    {
        public FakeTimeSource(DateTimeOffset utcNow) => UtcNow = utcNow;
        public DateTimeOffset UtcNow { get; set; }
    }
}
