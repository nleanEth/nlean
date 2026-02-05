using Lean.Consensus.Types;
using Nethermind.Serialization.Ssz;
using NUnit.Framework;

namespace Lean.Consensus.Tests;

public sealed class SszEncodingTests
{
    [Test]
    public void Bytes32EncodingMatchesSsz()
    {
        var bytes = Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray();
        var value = new Bytes32(bytes);

        var encoded = SszEncoding.Encode(value);

        var expected = new byte[SszEncoding.Bytes32Length];
        Ssz.Encode(expected, bytes);

        Assert.That(encoded, Is.EqualTo(expected));
    }

    [Test]
    public void CheckpointEncodingMatchesSsz()
    {
        var root = new Bytes32(Enumerable.Range(0, 32).Select(i => (byte)(255 - i)).ToArray());
        var checkpoint = new Checkpoint(root, new Slot(42));

        var encoded = SszEncoding.Encode(checkpoint);
        var expected = new byte[SszEncoding.CheckpointLength];

        var offset = 0;
        Ssz.Encode(expected.AsSpan(offset, SszEncoding.Bytes32Length), root.AsSpan());
        offset += SszEncoding.Bytes32Length;
        Ssz.Encode(expected.AsSpan(offset, SszEncoding.UInt64Length), checkpoint.Slot.Value);

        Assert.That(encoded, Is.EqualTo(expected));
    }

    [Test]
    public void AttestationDataEncodingMatchesSsz()
    {
        var data = new AttestationData(
            new Slot(7),
            new Checkpoint(new Bytes32(new byte[32]), new Slot(1)),
            new Checkpoint(new Bytes32(Enumerable.Repeat((byte)9, 32).ToArray()), new Slot(2)),
            new Checkpoint(new Bytes32(Enumerable.Repeat((byte)5, 32).ToArray()), new Slot(3)));

        var encoded = SszEncoding.Encode(data);
        var expected = new byte[SszEncoding.AttestationDataLength];
        var offset = 0;

        Ssz.Encode(expected.AsSpan(offset, SszEncoding.UInt64Length), data.Slot.Value);
        offset += SszEncoding.UInt64Length;
        offset = WriteCheckpoint(expected, offset, data.Head);
        offset = WriteCheckpoint(expected, offset, data.Target);
        WriteCheckpoint(expected, offset, data.Source);

        Assert.That(encoded, Is.EqualTo(expected));
    }

    [Test]
    public void AttestationEncodingMatchesSsz()
    {
        var data = new AttestationData(
            new Slot(1),
            new Checkpoint(new Bytes32(new byte[32]), new Slot(2)),
            new Checkpoint(new Bytes32(Enumerable.Repeat((byte)4, 32).ToArray()), new Slot(3)),
            new Checkpoint(new Bytes32(Enumerable.Repeat((byte)7, 32).ToArray()), new Slot(4)));
        var attestation = new Attestation(128, data);

        var encoded = SszEncoding.Encode(attestation);
        var expected = new byte[SszEncoding.AttestationLength];
        var offset = 0;

        Ssz.Encode(expected.AsSpan(offset, SszEncoding.UInt64Length), attestation.ValidatorId);
        offset += SszEncoding.UInt64Length;
        offset = WriteAttestationData(expected, offset, data);

        Assert.That(encoded, Is.EqualTo(expected));
    }

    [Test]
    public void BlockHeaderEncodingMatchesSsz()
    {
        var header = new BlockHeader(
            new Slot(16),
            3,
            new Bytes32(Enumerable.Repeat((byte)1, 32).ToArray()),
            new Bytes32(Enumerable.Repeat((byte)2, 32).ToArray()),
            new Bytes32(Enumerable.Repeat((byte)3, 32).ToArray()));

        var encoded = SszEncoding.Encode(header);
        var expected = new byte[SszEncoding.BlockHeaderLength];
        var offset = 0;

        Ssz.Encode(expected.AsSpan(offset, SszEncoding.UInt64Length), header.Slot.Value);
        offset += SszEncoding.UInt64Length;
        Ssz.Encode(expected.AsSpan(offset, SszEncoding.UInt64Length), header.ProposerIndex);
        offset += SszEncoding.UInt64Length;
        offset = WriteBytes32(expected, offset, header.ParentRoot);
        offset = WriteBytes32(expected, offset, header.StateRoot);
        WriteBytes32(expected, offset, header.BodyRoot);

        Assert.That(encoded, Is.EqualTo(expected));
    }

    private static int WriteBytes32(byte[] buffer, int offset, Bytes32 value)
    {
        Ssz.Encode(buffer.AsSpan(offset, SszEncoding.Bytes32Length), value.AsSpan());
        return offset + SszEncoding.Bytes32Length;
    }

    private static int WriteCheckpoint(byte[] buffer, int offset, Checkpoint checkpoint)
    {
        offset = WriteBytes32(buffer, offset, checkpoint.Root);
        Ssz.Encode(buffer.AsSpan(offset, SszEncoding.UInt64Length), checkpoint.Slot.Value);
        return offset + SszEncoding.UInt64Length;
    }

    private static int WriteAttestationData(byte[] buffer, int offset, AttestationData data)
    {
        Ssz.Encode(buffer.AsSpan(offset, SszEncoding.UInt64Length), data.Slot.Value);
        offset += SszEncoding.UInt64Length;
        offset = WriteCheckpoint(buffer, offset, data.Head);
        offset = WriteCheckpoint(buffer, offset, data.Target);
        return WriteCheckpoint(buffer, offset, data.Source);
    }
}
