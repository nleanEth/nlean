using Lean.Storage;
using NUnit.Framework;

namespace Lean.Consensus.Tests.Infrastructure;

[TestFixture]
public sealed class PrefixedDatabaseTests
{
    [Test]
    public void PutBlock_GetBlock_RoundTrips()
    {
        var db = CreateDatabase();
        var root = MakeBytes(32, 0x01);
        var data = MakeBytes(100, 0xAA);

        db.PutBlock(root, data);
        var result = db.GetBlock(root);

        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.EqualTo(data));
    }

    [Test]
    public void HasBlock_ReturnsTrueAfterPut()
    {
        var db = CreateDatabase();
        var root = MakeBytes(32, 0x01);

        Assert.That(db.HasBlock(root), Is.False);
        db.PutBlock(root, MakeBytes(10, 0xFF));
        Assert.That(db.HasBlock(root), Is.True);
    }

    [Test]
    public void GetBlock_ReturnsNull_WhenMissing()
    {
        var db = CreateDatabase();
        Assert.That(db.GetBlock(MakeBytes(32, 0x99)), Is.Null);
    }

    [Test]
    public void PutState_GetState_RoundTrips()
    {
        var db = CreateDatabase();
        var root = MakeBytes(32, 0x02);
        var data = MakeBytes(200, 0xBB);

        db.PutState(root, data);

        Assert.That(db.GetState(root), Is.EqualTo(data));
    }

    [Test]
    public void JustifiedCheckpoint_RoundTrips()
    {
        var db = CreateDatabase();
        var cp = MakeBytes(40, 0x03);

        Assert.That(db.GetJustifiedCheckpoint(), Is.Null);
        db.PutJustifiedCheckpoint(cp);
        Assert.That(db.GetJustifiedCheckpoint(), Is.EqualTo(cp));
    }

    [Test]
    public void FinalizedCheckpoint_RoundTrips()
    {
        var db = CreateDatabase();
        var cp = MakeBytes(40, 0x04);

        db.PutFinalizedCheckpoint(cp);
        Assert.That(db.GetFinalizedCheckpoint(), Is.EqualTo(cp));
    }

    [Test]
    public void HeadRoot_RoundTrips()
    {
        var db = CreateDatabase();
        var root = MakeBytes(32, 0x05);

        Assert.That(db.GetHeadRoot(), Is.Null);
        db.PutHeadRoot(root);
        Assert.That(db.GetHeadRoot(), Is.EqualTo(root));
    }

    [Test]
    public void BlockRootsBySlot_RoundTrips()
    {
        var db = CreateDatabase();
        var roots = MakeBytes(64, 0x06); // two 32-byte roots

        db.PutBlockRootsBySlot(42, roots);
        Assert.That(db.GetBlockRootsBySlot(42), Is.EqualTo(roots));
        Assert.That(db.GetBlockRootsBySlot(43), Is.Null);
    }

    [Test]
    public void Attestation_RoundTrips()
    {
        var db = CreateDatabase();
        var key = MakeBytes(32, 0x07);
        var data = MakeBytes(50, 0xCC);

        db.PutAttestation(key, data);
        Assert.That(db.GetAttestation(key), Is.EqualTo(data));
    }

    [Test]
    public void DifferentPrefixes_DoNotCollide()
    {
        var db = CreateDatabase();
        var sameKey = MakeBytes(32, 0x01);
        var blockData = MakeBytes(10, 0xAA);
        var stateData = MakeBytes(10, 0xBB);

        db.PutBlock(sameKey, blockData);
        db.PutState(sameKey, stateData);

        Assert.That(db.GetBlock(sameKey), Is.EqualTo(blockData));
        Assert.That(db.GetState(sameKey), Is.EqualTo(stateData));
    }

    private static PrefixedDatabase CreateDatabase() =>
        new(new InMemoryKeyValueStore());

    private static byte[] MakeBytes(int length, byte fill) =>
        Enumerable.Repeat(fill, length).ToArray();
}
