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

        // Advance 1600ms = 2 intervals
        time.UtcNow = GenesisTime.AddMilliseconds(1600);
        chain.TickToCurrent();

        // Should emit ticks for intervals 0 and 1 (total intervals 0 and 1)
        Assert.That(target.Ticks.Count, Is.EqualTo(2));
        Assert.That(target.Ticks[0], Is.EqualTo((0UL, 0)));
        Assert.That(target.Ticks[1], Is.EqualTo((0UL, 1)));
    }

    [Test]
    public void TickToCurrent_SkipsAlreadyProcessed()
    {
        var time = new FakeTimeSource(GenesisTime);
        var clock = new SlotClock((ulong)GenesisTime.ToUnixTimeSeconds(), SecondsPerSlot, IntervalsPerSlot, time);
        var target = new FakeTickTarget();
        var chain = new ChainService(clock, target, IntervalsPerSlot);

        // First advance: 1600ms = 2 intervals
        time.UtcNow = GenesisTime.AddMilliseconds(1600);
        chain.TickToCurrent();
        Assert.That(target.Ticks.Count, Is.EqualTo(2));

        // Second advance: same time — no new ticks
        chain.TickToCurrent();
        Assert.That(target.Ticks.Count, Is.EqualTo(2));

        // Third advance: 2400ms = 3 intervals — only 1 new tick
        time.UtcNow = GenesisTime.AddMilliseconds(2400);
        chain.TickToCurrent();
        Assert.That(target.Ticks.Count, Is.EqualTo(3));
        Assert.That(target.Ticks[2], Is.EqualTo((0UL, 2)));
    }

    [Test]
    public void TickToCurrent_CrossesSlotBoundary()
    {
        var time = new FakeTimeSource(GenesisTime);
        var clock = new SlotClock((ulong)GenesisTime.ToUnixTimeSeconds(), SecondsPerSlot, IntervalsPerSlot, time);
        var target = new FakeTickTarget();
        var chain = new ChainService(clock, target, IntervalsPerSlot);

        // Jump to slot 1, interval 1 = 4800ms = 6 total intervals
        time.UtcNow = GenesisTime.AddMilliseconds(4800);
        chain.TickToCurrent();

        Assert.That(target.Ticks.Count, Is.EqualTo(6));
        // Last tick should be slot 1, interval 0
        Assert.That(target.Ticks[4], Is.EqualTo((0UL, 4))); // last interval of slot 0
        Assert.That(target.Ticks[5], Is.EqualTo((1UL, 0))); // first interval of slot 1
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
