using Lean.Consensus.Sync;
using NUnit.Framework;

namespace Lean.Consensus.Tests.Sync;

[TestFixture]
public sealed class SyncPeerManagerTests
{
    [Test]
    public void AddPeer_IncreasesPeerCount()
    {
        var mgr = new SyncPeerManager();
        mgr.AddPeer("peer-1");
        Assert.That(mgr.PeerCount, Is.EqualTo(1));
    }

    [Test]
    public void AddPeer_Duplicate_IsIgnored()
    {
        var mgr = new SyncPeerManager();
        mgr.AddPeer("peer-1");
        mgr.AddPeer("peer-1");
        Assert.That(mgr.PeerCount, Is.EqualTo(1));
    }

    [Test]
    public void RemovePeer_DecreasesPeerCount()
    {
        var mgr = new SyncPeerManager();
        mgr.AddPeer("peer-1");
        mgr.RemovePeer("peer-1");
        Assert.That(mgr.PeerCount, Is.EqualTo(0));
    }

    [Test]
    public void UpdatePeerStatus_SetsHeadAndFinalizedSlot()
    {
        var mgr = new SyncPeerManager();
        mgr.AddPeer("peer-1");
        mgr.UpdatePeerStatus("peer-1", headSlot: 100, finalizedSlot: 50);

        Assert.That(mgr.GetNetworkHeadSlot(), Is.EqualTo(100UL));
    }

    [Test]
    public void GetNetworkHeadSlot_ReturnsMaxAcrossPeers()
    {
        var mgr = new SyncPeerManager();
        mgr.AddPeer("peer-1");
        mgr.AddPeer("peer-2");
        mgr.UpdatePeerStatus("peer-1", headSlot: 50, finalizedSlot: 20);
        mgr.UpdatePeerStatus("peer-2", headSlot: 100, finalizedSlot: 60);

        Assert.That(mgr.GetNetworkHeadSlot(), Is.EqualTo(100UL));
    }

    [Test]
    public void GetNetworkHeadSlot_NoPeers_ReturnsZero()
    {
        var mgr = new SyncPeerManager();
        Assert.That(mgr.GetNetworkHeadSlot(), Is.EqualTo(0UL));
    }

    [Test]
    public void OnRequestSuccess_IncreasesScore()
    {
        var mgr = new SyncPeerManager();
        mgr.AddPeer("peer-1");
        var scoreBefore = mgr.GetPeerScore("peer-1");

        mgr.OnRequestSuccess("peer-1");

        Assert.That(mgr.GetPeerScore("peer-1"), Is.GreaterThan(scoreBefore));
    }

    [Test]
    public void OnRequestFailure_DecreasesScore()
    {
        var mgr = new SyncPeerManager();
        mgr.AddPeer("peer-1");
        var scoreBefore = mgr.GetPeerScore("peer-1");

        mgr.OnRequestFailure("peer-1");

        Assert.That(mgr.GetPeerScore("peer-1"), Is.LessThan(scoreBefore));
    }

    [Test]
    public void Score_ClampedToRange_10_200()
    {
        var mgr = new SyncPeerManager();
        mgr.AddPeer("peer-1");

        // Drive score to min (floor is 10, not 0)
        for (int i = 0; i < 20; i++)
            mgr.OnRequestFailure("peer-1");

        Assert.That(mgr.GetPeerScore("peer-1"), Is.EqualTo(10));

        // Drive score to max
        for (int i = 0; i < 30; i++)
            mgr.OnRequestSuccess("peer-1");

        Assert.That(mgr.GetPeerScore("peer-1"), Is.EqualTo(200));
    }

    [Test]
    public void SelectPeerForRequest_StillSelectsPeerAtMinScore()
    {
        var mgr = new SyncPeerManager();
        mgr.AddPeer("peer-1");

        // Drive score to floor
        for (int i = 0; i < 20; i++)
            mgr.OnRequestFailure("peer-1");

        // Peer should still be selectable at MinScore=10
        Assert.That(mgr.SelectPeerForRequest(), Is.EqualTo("peer-1"));
    }

    [Test]
    public void SelectPeerForRequest_ReturnsPeer_WhenAvailable()
    {
        var mgr = new SyncPeerManager();
        mgr.AddPeer("peer-1");

        var peer = mgr.SelectPeerForRequest();
        Assert.That(peer, Is.EqualTo("peer-1"));
    }

    [Test]
    public void SelectPeerForRequest_ReturnsNull_WhenNoPeers()
    {
        var mgr = new SyncPeerManager();
        Assert.That(mgr.SelectPeerForRequest(), Is.Null);
    }

    [Test]
    public void SelectPeerForRequest_SkipsPeersWithTooManyInflight()
    {
        var mgr = new SyncPeerManager();
        mgr.AddPeer("peer-1");
        mgr.AddPeer("peer-2");

        // Saturate peer-1 with 2 in-flight requests
        mgr.IncrementInflight("peer-1");
        mgr.IncrementInflight("peer-1");

        // Should always select peer-2 now
        for (int i = 0; i < 10; i++)
        {
            var peer = mgr.SelectPeerForRequest();
            Assert.That(peer, Is.EqualTo("peer-2"));
        }
    }

    [Test]
    public void DecrementInflight_AllowsPeerToBeSelectedAgain()
    {
        var mgr = new SyncPeerManager();
        mgr.AddPeer("peer-1");
        mgr.IncrementInflight("peer-1");
        mgr.IncrementInflight("peer-1");

        // peer-1 saturated
        Assert.That(mgr.SelectPeerForRequest(), Is.Null);

        mgr.DecrementInflight("peer-1");

        // Now available again
        Assert.That(mgr.SelectPeerForRequest(), Is.EqualTo("peer-1"));
    }

    [Test]
    public void InitialScore_Is100()
    {
        var mgr = new SyncPeerManager();
        mgr.AddPeer("peer-1");
        Assert.That(mgr.GetPeerScore("peer-1"), Is.EqualTo(100));
    }
}
