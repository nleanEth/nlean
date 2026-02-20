using Google.Protobuf;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using NUnit.Framework;
using Snappier;

namespace Lean.Network.Tests;

[TestFixture]
public sealed class LeanPubsubMessageIdTests
{
    [Test]
    public void Compute_IgnoresFromAndSeqno_ForAnonymousInterop()
    {
        var topic = "/leanconsensus/devnet0/attestation/ssz_snappy";
        var data = ByteString.CopyFrom(new byte[] { 0x01, 0x02, 0x03, 0x04 });

        var first = new Message
        {
            Topic = topic,
            Data = data,
            From = ByteString.CopyFrom(new byte[] { 0xAA }),
            Seqno = ByteString.CopyFrom(new byte[] { 0x01 })
        };

        var second = new Message
        {
            Topic = topic,
            Data = data
        };

        var firstId = LeanPubsubMessageId.Compute(first);
        var secondId = LeanPubsubMessageId.Compute(second);

        Assert.That(firstId, Is.EqualTo(secondId));
    }

    [Test]
    public void Compute_ChangesWhenPayloadChanges()
    {
        var topic = "/leanconsensus/devnet0/block/ssz_snappy";

        var first = new Message
        {
            Topic = topic,
            Data = ByteString.CopyFrom(new byte[] { 0x01, 0x02, 0x03, 0x04 })
        };

        var second = new Message
        {
            Topic = topic,
            Data = ByteString.CopyFrom(new byte[] { 0x01, 0x02, 0x03, 0x05 })
        };

        var firstId = LeanPubsubMessageId.Compute(first);
        var secondId = LeanPubsubMessageId.Compute(second);

        Assert.That(firstId, Is.Not.EqualTo(secondId));
    }

    [Test]
    public void Compute_MatchesZeamVector_WithSnappyPayload()
    {
        var message = new Message
        {
            Topic = "test",
            Data = ByteString.CopyFrom(Snappy.CompressToArray(new byte[] { 0x68, 0x65, 0x6C, 0x6C, 0x6F }))
        };

        var messageId = LeanPubsubMessageId.Compute(message);
        var expected = new MessageId(Convert.FromHexString("2e40c861545cc5b46d2220062e7440b9190bc383"));
        Assert.That(messageId, Is.EqualTo(expected));
    }

    [Test]
    public void Compute_MatchesZeamVector_WithRawPayload()
    {
        var message = new Message
        {
            Topic = "test",
            Data = ByteString.CopyFrom(new byte[] { 0x68, 0x65, 0x6C, 0x6C, 0x6F })
        };

        var messageId = LeanPubsubMessageId.Compute(message);
        var expected = new MessageId(Convert.FromHexString("a7f41aaccd241477955c981714eb92244c2efc98"));
        Assert.That(messageId, Is.EqualTo(expected));
    }
}
