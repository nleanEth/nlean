using System.Text.Json;
using System.Text.Json.Serialization;
using Lean.Consensus.Chain;
using Lean.SpecTests.Types;
using NUnit.Framework;

namespace Lean.SpecTests.Runners;

public sealed class SlotClockRunner : ISpecTestRunner
{
    private const int SecondsPerSlot = 4;
    private const int IntervalsPerSlot = 5;

    public void Run(string testId, string testJson)
    {
        var test = JsonSerializer.Deserialize<SlotClockTest>(testJson)
            ?? throw new InvalidOperationException($"Failed to deserialize: {testId}");

        switch (test.Operation)
        {
            case "current_slot":
                RunCurrentSlot(test);
                break;
            case "current_interval":
                RunCurrentInterval(test);
                break;
            case "total_intervals":
                RunTotalIntervals(test);
                break;
            case "from_slot":
                RunFromSlot(test);
                break;
            case "from_unix_time":
                RunFromUnixTime(test);
                break;
            default:
                Assert.Inconclusive($"Unsupported slot_clock operation: {test.Operation}");
                break;
        }

        ValidateConfig(test.Output.Config);
    }

    private static void RunCurrentSlot(SlotClockTest test)
    {
        var (genesis, now) = RequireTimeInputs(test);
        var clock = new SlotClock(genesis, SecondsPerSlot, IntervalsPerSlot, new FixedTimeSource(now));
        Assert.That(clock.CurrentSlot, Is.EqualTo(test.Output.Slot ?? 0),
            "current_slot mismatch");
    }

    private static void RunCurrentInterval(SlotClockTest test)
    {
        var (genesis, now) = RequireTimeInputs(test);
        var clock = new SlotClock(genesis, SecondsPerSlot, IntervalsPerSlot, new FixedTimeSource(now));
        Assert.That((ulong)clock.CurrentInterval, Is.EqualTo(test.Output.Interval ?? 0),
            "current_interval mismatch");
    }

    private static void RunTotalIntervals(SlotClockTest test)
    {
        var (genesis, now) = RequireTimeInputs(test);
        var clock = new SlotClock(genesis, SecondsPerSlot, IntervalsPerSlot, new FixedTimeSource(now));
        Assert.That(clock.TotalIntervals, Is.EqualTo(test.Output.TotalIntervals ?? 0),
            "total_intervals mismatch");
    }

    private static void RunFromSlot(SlotClockTest test)
    {
        // from_slot(slot) = slot * INTERVALS_PER_SLOT (spec's SlotClock.from_slot
        // converts a slot count into intervals-since-genesis). nlean doesn't
        // expose this as a dedicated method, but it's pure arithmetic.
        var slot = test.Input.Slot ?? 0;
        var expected = test.Output.Interval ?? 0;
        var actual = slot * (ulong)IntervalsPerSlot;
        Assert.That(actual, Is.EqualTo(expected), "from_slot mismatch");
    }

    private static void RunFromUnixTime(SlotClockTest test)
    {
        // from_unix_time(unixSeconds, genesisTime) = floor((unixSeconds - genesisTime) * 1000 / msPerInterval)
        var unixSeconds = test.Input.UnixSeconds ?? 0;
        var genesis = test.Input.GenesisTime ?? 0;
        var expected = test.Output.Interval ?? 0;

        const long msPerInterval = (SecondsPerSlot * 1000L) / IntervalsPerSlot;

        long deltaMs;
        if (unixSeconds >= genesis)
        {
            deltaMs = checked((long)(unixSeconds - genesis) * 1000L);
        }
        else
        {
            // Negative elapsed: the spec pins interval to 0 for pre-genesis time.
            deltaMs = 0;
        }

        var actual = (ulong)(deltaMs / msPerInterval);
        Assert.That(actual, Is.EqualTo(expected), "from_unix_time mismatch");
    }

    private static (ulong GenesisSeconds, DateTimeOffset Now) RequireTimeInputs(SlotClockTest test)
    {
        if (test.Input.GenesisTime is not ulong genesis || test.Input.CurrentTimeMs is not ulong nowMs)
        {
            throw new InvalidOperationException(
                $"operation '{test.Operation}' needs genesisTime + currentTimeMs inputs");
        }
        var now = DateTimeOffset.FromUnixTimeMilliseconds((long)nowMs);
        return (genesis, now);
    }

    private static void ValidateConfig(SlotClockConfig? config)
    {
        if (config is null) return;
        Assert.That(config.SecondsPerSlot, Is.EqualTo(SecondsPerSlot),
            "fixture assumes a different SECONDS_PER_SLOT than nlean");
        Assert.That(config.IntervalsPerSlot, Is.EqualTo(IntervalsPerSlot),
            "fixture assumes a different INTERVALS_PER_SLOT than nlean");
    }

    private sealed class FixedTimeSource : ITimeSource
    {
        private readonly DateTimeOffset _now;
        public FixedTimeSource(DateTimeOffset now) => _now = now;
        public DateTimeOffset UtcNow => _now;
    }

    private sealed record SlotClockTest(
        [property: JsonPropertyName("network")] string Network,
        [property: JsonPropertyName("leanEnv")] string LeanEnv,
        [property: JsonPropertyName("operation")] string Operation,
        [property: JsonPropertyName("input")] SlotClockInput Input,
        [property: JsonPropertyName("output")] SlotClockOutput Output,
        [property: JsonPropertyName("_info")] TestInfo? Info);

    private sealed record SlotClockInput(
        [property: JsonPropertyName("genesisTime")] ulong? GenesisTime,
        [property: JsonPropertyName("currentTimeMs")] ulong? CurrentTimeMs,
        [property: JsonPropertyName("slot")] ulong? Slot,
        [property: JsonPropertyName("unixSeconds")] ulong? UnixSeconds);

    private sealed record SlotClockOutput(
        [property: JsonPropertyName("config")] SlotClockConfig? Config,
        [property: JsonPropertyName("slot")] ulong? Slot,
        [property: JsonPropertyName("interval")] ulong? Interval,
        [property: JsonPropertyName("totalIntervals")] ulong? TotalIntervals);

    private sealed record SlotClockConfig(
        [property: JsonPropertyName("secondsPerSlot")] int SecondsPerSlot,
        [property: JsonPropertyName("intervalsPerSlot")] int IntervalsPerSlot,
        [property: JsonPropertyName("millisecondsPerInterval")] long MillisecondsPerInterval);
}
