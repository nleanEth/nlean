using Lean.SpecTests.Runners;
using NUnit.Framework;

namespace Lean.SpecTests;

[TestFixture]
[Category("SpecTest")]
[Parallelizable(ParallelScope.Children)]
public sealed class SlotClockSpecTests
{
    private static readonly SlotClockRunner Runner = new();

    public static IEnumerable<TestCaseData> LoadTests() =>
        FixtureDiscovery.DiscoverTests("slot_clock");

    [TestCaseSource(nameof(LoadTests))]
    public void RunSlotClockTest(string testId, string testJson)
    {
        Runner.Run(testId, testJson);
    }
}
