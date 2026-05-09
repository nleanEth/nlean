using Lean.Consensus.Chain;
using NUnit.Framework;

namespace Lean.Consensus.Tests.Chain;

[TestFixture]
public sealed class ChainServiceTests
{
    private const int SecondsPerSlot = 4;
    private const int IntervalsPerSlot = 5;
    private static readonly DateTimeOffset GenesisTime = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public void TickToCurrent_AdvancesIntervals()
    {
        var time = new FakeTimeSource(GenesisTime);
        var clock = new SlotClock((ulong)GenesisTime.ToUnixTimeSeconds(), SecondsPerSlot, IntervalsPerSlot, time);
        var target = new FakeTickTarget();
        var chain = new ChainService(clock, target, IntervalsPerSlot);

        // Advance 1600ms = 2 total intervals (we are at the start of interval 2).
        // First call skips replay and fires OnTick for the CURRENT interval.
        time.UtcNow = GenesisTime.AddMilliseconds(1600);
        chain.TickToCurrent();

        // totalIntervals=2 → tick at slot=2/5=0, interval=2%5=2 (current interval, not previous).
        Assert.That(target.Ticks.Count, Is.EqualTo(1));
        Assert.That(target.Ticks[0], Is.EqualTo((0UL, 2)));
    }

    [Test]
    public void TickToCurrent_SkipsAlreadyProcessed()
    {
        var time = new FakeTimeSource(GenesisTime);
        var clock = new SlotClock((ulong)GenesisTime.ToUnixTimeSeconds(), SecondsPerSlot, IntervalsPerSlot, time);
        var target = new FakeTickTarget();
        var chain = new ChainService(clock, target, IntervalsPerSlot);

        // First advance: 1600ms = 2 total intervals → 1 init tick at (0, 2)
        time.UtcNow = GenesisTime.AddMilliseconds(1600);
        chain.TickToCurrent();
        Assert.That(target.Ticks.Count, Is.EqualTo(1));

        // Second advance: same time — no new ticks
        chain.TickToCurrent();
        Assert.That(target.Ticks.Count, Is.EqualTo(1));

        // Third advance: 2400ms = 3 total intervals — 1 new tick at (0, 3)
        time.UtcNow = GenesisTime.AddMilliseconds(2400);
        chain.TickToCurrent();
        Assert.That(target.Ticks.Count, Is.EqualTo(2));
        Assert.That(target.Ticks[1], Is.EqualTo((0UL, 3)));
    }

    [Test]
    public void TickToCurrent_CrossesSlotBoundary()
    {
        var time = new FakeTimeSource(GenesisTime);
        var clock = new SlotClock((ulong)GenesisTime.ToUnixTimeSeconds(), SecondsPerSlot, IntervalsPerSlot, time);
        var target = new FakeTickTarget();
        var chain = new ChainService(clock, target, IntervalsPerSlot);

        // Jump to 4800ms = 6 total intervals.
        // First call skips replay and emits one tick at the CURRENT interval.
        time.UtcNow = GenesisTime.AddMilliseconds(4800);
        chain.TickToCurrent();

        // totalIntervals=6 → tick at slot=6/5=1, interval=6%5=1
        Assert.That(target.Ticks.Count, Is.EqualTo(1));
        Assert.That(target.Ticks[0], Is.EqualTo((1UL, 1)));
    }

    [Test]
    public void TickToCurrent_BeforeGenesis_NoTicks()
    {
        var time = new FakeTimeSource(GenesisTime.AddSeconds(-5));
        var clock = new SlotClock((ulong)GenesisTime.ToUnixTimeSeconds(), SecondsPerSlot, IntervalsPerSlot, time);
        var target = new FakeTickTarget();
        var chain = new ChainService(clock, target, IntervalsPerSlot);

        chain.TickToCurrent();
        Assert.That(target.Ticks.Count, Is.EqualTo(0));
    }

    [Test]
    public async Task RunAsync_EmitsTicksAndStopsOnCancellation()
    {
        // Start 1600ms after genesis so there's already work to do
        var time = new FakeTimeSource(GenesisTime.AddMilliseconds(1600));
        var clock = new SlotClock((ulong)GenesisTime.ToUnixTimeSeconds(), SecondsPerSlot, IntervalsPerSlot, time);
        var target = new FakeTickTarget();
        var chain = new ChainService(clock, target, IntervalsPerSlot);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        try { await chain.RunAsync(cts.Token); }
        catch (OperationCanceledException) { }

        Assert.That(target.Ticks.Count, Is.GreaterThan(0));
    }

    private sealed class FakeTimeSource : ITimeSource
    {
        public FakeTimeSource(DateTimeOffset utcNow) => UtcNow = utcNow;
        public DateTimeOffset UtcNow { get; set; }
    }

    private sealed class FakeTickTarget : ITickTarget
    {
        public List<(ulong Slot, int Interval)> Ticks { get; } = new();

        public void OnTick(ulong slot, int intervalInSlot)
        {
            Ticks.Add((slot, intervalInSlot));
        }
    }
}
