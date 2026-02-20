using NUnit.Framework;

namespace Lean.Network.Tests;

[TestFixture]
public class BlocksByRootPeerSelectorTests
{
    [Test]
    public void GetRequestOrder_PrefersConnectedPeerWithLowerLatency()
    {
        var selector = new BlocksByRootPeerSelector();
        selector.RegisterCandidates(new[] { "peer-a", "peer-b", "peer-c" });

        selector.MarkConnected("peer-a");
        selector.MarkConnected("peer-b");
        selector.RecordAttempt("peer-a", BlocksByRootPeerAttemptResult.Success, TimeSpan.FromMilliseconds(20));
        selector.RecordAttempt("peer-b", BlocksByRootPeerAttemptResult.Success, TimeSpan.FromMilliseconds(250));

        var ordered = selector.GetRequestOrder();

        Assert.That(ordered[0], Is.EqualTo("peer-a"));
        Assert.That(ordered[1], Is.EqualTo("peer-b"));
        Assert.That(ordered[2], Is.EqualTo("peer-c"));
    }

    [Test]
    public void GetRequestOrder_DemotesPeersWithConsecutiveFailures()
    {
        var selector = new BlocksByRootPeerSelector();
        selector.RegisterCandidates(new[] { "peer-a", "peer-b" });

        selector.MarkConnected("peer-a");
        selector.MarkConnected("peer-b");
        selector.RecordAttempt("peer-a", BlocksByRootPeerAttemptResult.Success, TimeSpan.FromMilliseconds(120));
        selector.RecordAttempt("peer-b", BlocksByRootPeerAttemptResult.Success, TimeSpan.FromMilliseconds(10));

        selector.RecordAttempt("peer-b", BlocksByRootPeerAttemptResult.Failure, TimeSpan.FromMilliseconds(300));
        selector.RecordAttempt("peer-b", BlocksByRootPeerAttemptResult.Failure, TimeSpan.FromMilliseconds(300));
        selector.RecordAttempt("peer-b", BlocksByRootPeerAttemptResult.Failure, TimeSpan.FromMilliseconds(300));

        var ordered = selector.GetRequestOrder();

        Assert.That(ordered[0], Is.EqualTo("peer-a"));
        Assert.That(ordered[1], Is.EqualTo("peer-b"));
    }

    [Test]
    public void RegisterCandidates_IgnoresDuplicatesAndWhitespace()
    {
        var selector = new BlocksByRootPeerSelector();
        selector.RegisterCandidates(new[] { "peer-a", " ", "peer-a", "peer-b" });

        var ordered = selector.GetRequestOrder();

        Assert.That(ordered, Is.EquivalentTo(new[] { "peer-a", "peer-b" }));
    }
}
