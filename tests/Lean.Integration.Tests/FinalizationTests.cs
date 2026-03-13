using NUnit.Framework;

namespace Lean.Integration.Tests;

[TestFixture]
[Category("Integration")]
[NonParallelizable]
public class FinalizationTests
{
    [Test]
    public async Task FourNode_ReachesFinalization()
    {
        using var cluster = new DevnetCluster(nodeCount: 4, basePort: 19400);
        cluster.StartAll();

        await cluster.WaitForFinalization(
            targetSlot: 20,
            timeout: TimeSpan.FromMinutes(3));

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
        using var cluster = new DevnetCluster(nodeCount: 4, basePort: 19200);
        cluster.StartAll();

        // Phase 1: 4 nodes finalize to >= 20
        await cluster.WaitForFinalization(
            targetSlot: 20,
            timeout: TimeSpan.FromMinutes(3));

        // Phase 2: stop node 3, 3 nodes continue finalizing to >= 30
        cluster.StopNode(3);

        await cluster.WaitForFinalization(
            targetSlot: 30,
            timeout: TimeSpan.FromMinutes(3),
            nodeIndices: new[] { 0, 1, 2 });

        // Phase 3: restart node 3, all 4 nodes finalize to >= 40
        cluster.RestartNode(3);

        await cluster.WaitForFinalization(
            targetSlot: 40,
            timeout: TimeSpan.FromMinutes(3));
    }

    [Test]
    public async Task FourNode_CheckpointSync_JoinsAndFinalizes()
    {
        using var cluster = new DevnetCluster(nodeCount: 4, basePort: 19300);
        cluster.StartAll();

        await cluster.WaitForFinalization(
            targetSlot: 20,
            timeout: TimeSpan.FromMinutes(3));

        cluster.StopNode(3);
        cluster.CleanNodeData(3);

        var checkpointUrl = $"http://127.0.0.1:{cluster.ApiPort(0)}/lean/v0/states/finalized";
        cluster.RestartNode(3, checkpointSyncUrl: checkpointUrl);

        var node0Cp = await cluster.GetFinalizedCheckpoint(0);
        var targetSlot = (node0Cp?.slot ?? 20) + 20;
        await cluster.WaitForNodeFinalization(3, targetSlot,
            timeout: TimeSpan.FromMinutes(3));
    }
}
