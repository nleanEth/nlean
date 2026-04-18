using Lean.SpecTests.Runners;
using NUnit.Framework;

namespace Lean.SpecTests;

[TestFixture]
[Category("SpecTest")]
[Parallelizable(ParallelScope.Children)]
public sealed class StateTransitionSpecTests
{
    private static readonly StateTransitionRunner Runner = new();

    public static IEnumerable<TestCaseData> LoadTests() =>
        FixtureDiscovery.DiscoverTests("state_transition");

    [TestCaseSource(nameof(LoadTests))]
    public void RunStateTransitionTest(string testId, string testJson)
    {
        Runner.Run(testId, testJson);
    }
}
