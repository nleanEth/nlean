using Lean.Consensus.Types;
using NUnit.Framework;

namespace Lean.Consensus.Tests.Types;

[TestFixture]
public sealed class SignedAggregatedAttestationTests
{
    [Test]
    public void HashTreeRoot_IsDeterministic()
    {
        var data = new AttestationData(
            new Slot(1),
            new Checkpoint(Bytes32.Zero(), new Slot(0)),
            new Checkpoint(Bytes32.Zero(), new Slot(0)),
            new Checkpoint(Bytes32.Zero(), new Slot(0)));
        var proof = new AggregatedSignatureProof(
            new AggregationBits(new[] { true, false }),
            new byte[] { 0x01, 0x02 });
        var signed = new SignedAggregatedAttestation(data, proof);

        var root1 = signed.HashTreeRoot();
        var root2 = signed.HashTreeRoot();

        Assert.That(root1, Is.EqualTo(root2));
        Assert.That(root1, Has.Length.EqualTo(32));
    }

    [Test]
    public void HashTreeRoot_DiffersWithDifferentData()
    {
        var data1 = new AttestationData(
            new Slot(1),
            new Checkpoint(Bytes32.Zero(), new Slot(0)),
            new Checkpoint(Bytes32.Zero(), new Slot(0)),
            new Checkpoint(Bytes32.Zero(), new Slot(0)));
        var data2 = new AttestationData(
            new Slot(2),
            new Checkpoint(Bytes32.Zero(), new Slot(0)),
            new Checkpoint(Bytes32.Zero(), new Slot(0)),
            new Checkpoint(Bytes32.Zero(), new Slot(0)));
        var proof = new AggregatedSignatureProof(
            new AggregationBits(new[] { true, false }),
            new byte[] { 0x01, 0x02 });

        var signed1 = new SignedAggregatedAttestation(data1, proof);
        var signed2 = new SignedAggregatedAttestation(data2, proof);

        Assert.That(signed1.HashTreeRoot(), Is.Not.EqualTo(signed2.HashTreeRoot()));
    }

    [Test]
    public void Encode_HasCorrectLayout()
    {
        var data = new AttestationData(
            new Slot(1),
            new Checkpoint(Bytes32.Zero(), new Slot(0)),
            new Checkpoint(Bytes32.Zero(), new Slot(0)),
            new Checkpoint(Bytes32.Zero(), new Slot(0)));
        var proof = new AggregatedSignatureProof(
            new AggregationBits(new[] { true }),
            new byte[] { 0xAB });
        var signed = new SignedAggregatedAttestation(data, proof);

        var encoded = SszEncoding.Encode(signed);
        // AttestationData = 104 bytes, offset = 4 bytes, proof = variable
        Assert.That(encoded.Length, Is.GreaterThan(108));
    }
}
