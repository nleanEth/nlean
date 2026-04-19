using System.Text.Json;
using System.Text.Json.Serialization;
using Lean.Consensus.Types;
using Lean.SpecTests.Types;
using NUnit.Framework;

namespace Lean.SpecTests.Runners;

public sealed class JustifiabilityRunner : ISpecTestRunner
{
    public void Run(string testId, string testJson)
    {
        var test = JsonSerializer.Deserialize<JustifiabilityTest>(testJson)
            ?? throw new InvalidOperationException($"Failed to deserialize: {testId}");

        // 3SF-mini's is_justifiable_after is a pure function from (slot, finalized)
        // to a bool. The spec fixture captures expected delta = slot - finalized
        // and the predicate result. We round-trip through nlean's Slot type to
        // exercise the same implementation the state transition uses.
        var actualDelta = test.Slot - test.FinalizedSlot;
        Assert.That(actualDelta, Is.EqualTo(test.Output.Delta),
            "delta calculation mismatch");

        var actual = new Slot(test.Slot).IsJustifiableAfter(new Slot(test.FinalizedSlot));
        Assert.That(actual, Is.EqualTo(test.Output.IsJustifiable),
            $"is_justifiable_after({test.Slot}, finalized={test.FinalizedSlot}) mismatch");
    }

    private sealed record JustifiabilityTest(
        [property: JsonPropertyName("network")] string Network,
        [property: JsonPropertyName("leanEnv")] string LeanEnv,
        [property: JsonPropertyName("slot")] ulong Slot,
        [property: JsonPropertyName("finalizedSlot")] ulong FinalizedSlot,
        [property: JsonPropertyName("output")] JustifiabilityOutput Output,
        [property: JsonPropertyName("_info")] TestInfo? Info);

    private sealed record JustifiabilityOutput(
        [property: JsonPropertyName("delta")] ulong Delta,
        [property: JsonPropertyName("isJustifiable")] bool IsJustifiable);
}
