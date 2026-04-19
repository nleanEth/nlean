using Lean.SpecTests.Runners;
using NUnit.Framework;

namespace Lean.SpecTests;

[TestFixture]
[Category("SpecTest")]
[Parallelizable(ParallelScope.Children)]
public sealed class VerifySignaturesSpecTests
{
    private static readonly VerifySignaturesRunner Runner = new();

    public static IEnumerable<TestCaseData> LoadTests() =>
        FixtureDiscovery.DiscoverTests("verify_signatures");

    [TestCaseSource(nameof(LoadTests))]
    public void RunVerifySignaturesTest(string testId, string testJson)
    {
        Runner.Run(testId, testJson);
    }
}
