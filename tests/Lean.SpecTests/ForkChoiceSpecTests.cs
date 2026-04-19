using Lean.SpecTests.Runners;
using NUnit.Framework;

namespace Lean.SpecTests;

[TestFixture]
[Category("SpecTest")]
[Parallelizable(ParallelScope.Children)]
public sealed class ForkChoiceSpecTests
{
    private static readonly ForkChoiceRunner Runner = new();

    public static IEnumerable<TestCaseData> LoadTests() =>
        FixtureDiscovery.DiscoverTests("fork_choice");

    [TestCaseSource(nameof(LoadTests))]
    public void RunForkChoiceTest(string testId, string testJson)
    {
        Runner.Run(testId, testJson);
    }
}
