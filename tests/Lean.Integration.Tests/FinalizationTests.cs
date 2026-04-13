using NUnit.Framework;

namespace Lean.Integration.Tests;

[TestFixture]
[Category("Integration")]
[NonParallelizable]
public class FinalizationTests
{
    // Port ranges: each 4-node test needs base..base+203 (QUIC +0, Metrics +100, API +200).
    // Space base ports 300 apart to avoid overlap between tests.

    [Test]
    public async Task FourNode_ReachesFinalization()
    {
        using var cluster = new DevnetCluster(nodeCount: 4, basePort: 19000);
        cluster.StartAll();

        await cluster.WaitForFinalization(
            targetSlot: 20,
            timeout: TimeSpan.FromMinutes(10));

        var checkpoints = new List<(ulong slot, string root)>();
        for (int i = 0; i < 4; i++)
        {
            var cp = await cluster.GetFinalizedCheckpoint(i);
            Assert.That(cp, Is.Not.Null, $"Node {i} finalized checkpoint is null");
            checkpoints.Add(cp!.Value);
        }

        Assert.That(
            checkpoints.Select(c => c.root).Distinct().Count(),
            Is.EqualTo(1),
            "Nodes disagree on finalized root");
    }

    [Test]
    public async Task FourNode_CatchupAfterRestart_Finalizes()
    {
        using var cluster = new DevnetCluster(nodeCount: 4, basePort: 19300);
        cluster.StartAll();

        // Phase 1: 4 nodes finalize to >= 20
        await cluster.WaitForFinalization(
            targetSlot: 20,
            timeout: TimeSpan.FromMinutes(10));

        // Phase 2: stop node 3, 3 nodes continue finalizing to >= 30
        cluster.StopNode(3);

        await cluster.WaitForFinalization(
            targetSlot: 30,
            timeout: TimeSpan.FromMinutes(10),
            nodeIndices: new[] { 0, 1, 2 });

        // Phase 3: restart node 3, all 4 nodes finalize to >= 40
        cluster.RestartNode(3);

        await cluster.WaitForFinalization(
            targetSlot: 40,
            timeout: TimeSpan.FromMinutes(10));
    }

    [Test]
    public async Task FourNode_CheckpointSync_JoinsAndFinalizes()
    {
        using var cluster = new DevnetCluster(nodeCount: 4, basePort: 19600);
        cluster.StartAll();

        await cluster.WaitForFinalization(
            targetSlot: 20,
            timeout: TimeSpan.FromMinutes(10));

        cluster.StopNode(3);
        cluster.CleanNodeData(3);

        var checkpointUrl = $"http://127.0.0.1:{cluster.ApiPort(0)}/lean/v0/states/finalized";
        cluster.RestartNode(3, checkpointSyncUrl: checkpointUrl);

        var node0Cp = await cluster.GetFinalizedCheckpoint(0);
        var targetSlot = (node0Cp?.slot ?? 20) + 20;
        await cluster.WaitForNodeFinalization(3, targetSlot,
            timeout: TimeSpan.FromMinutes(10));
    }

    [Test]
    public async Task TwoNode_TwoValidatorsEach_ReachesFinalization()
    {
        using var cluster = new DevnetCluster(nodeCount: 2, basePort: 19900, validatorsPerNode: 2);
        cluster.StartAll();

        await cluster.WaitForFinalization(
            targetSlot: 20,
            timeout: TimeSpan.FromMinutes(10));

        var checkpoints = new List<(ulong slot, string root)>();
        for (int i = 0; i < 2; i++)
        {
            var cp = await cluster.GetFinalizedCheckpoint(i);
            Assert.That(cp, Is.Not.Null, $"Node {i} finalized checkpoint is null");
            checkpoints.Add(cp!.Value);
        }

        Assert.That(
            checkpoints.Select(c => c.root).Distinct().Count(),
            Is.EqualTo(1),
            "Nodes disagree on finalized root");
    }

    [Test]
    public async Task FourNode_FourSubnets_ReachesFinalization()
    {
        // 4 nodes x 2 validators = 8 validators across 4 subnets
        // validator_id % 4: v0->s0, v1->s1, v2->s2, v3->s3, v4->s0, v5->s1, v6->s2, v7->s3
        // Aggregator (node 0) subscribes to all 4 subnets via --aggregate-subnet-ids
        using var cluster = new DevnetCluster(
            nodeCount: 4, basePort: 20200, validatorsPerNode: 2, attestationCommitteeCount: 4);
        cluster.StartAll();

        await cluster.WaitForFinalization(
            targetSlot: 20,
            timeout: TimeSpan.FromMinutes(10));

        var checkpoints = new List<(ulong slot, string root)>();
        for (int i = 0; i < 4; i++)
        {
            var cp = await cluster.GetFinalizedCheckpoint(i);
            Assert.That(cp, Is.Not.Null, $"Node {i} finalized checkpoint is null");
            checkpoints.Add(cp!.Value);
        }

        Assert.That(
            checkpoints.Select(c => c.root).Distinct().Count(),
            Is.EqualTo(1),
            "Nodes disagree on finalized root");
    }
}
