using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using Snappier;

namespace Lean.Network;

public static class LeanPubsubMessageId
{
    private static readonly byte[] MessageDomainValidSnappy = [0x01, 0x00, 0x00, 0x00];
    private static readonly byte[] MessageDomainInvalidSnappy = [0x00, 0x00, 0x00, 0x00];

    public static MessageId Compute(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var topic = message.Topic ?? string.Empty;
        var topicBytes = Encoding.ASCII.GetBytes(topic);
        var dataBytes = message.Data.ToByteArray();
        var domain = MessageDomainInvalidSnappy;

        try
        {
            dataBytes = Snappy.DecompressToArray(dataBytes);
            domain = MessageDomainValidSnappy;
        }
        catch
        {
            // Keep raw bytes when payload is not snappy-encoded.
        }

        var digestInput = new byte[domain.Length + sizeof(ulong) + topicBytes.Length + dataBytes.Length];
        var offset = 0;

        domain.CopyTo(digestInput, offset);
        offset += domain.Length;

        BinaryPrimitives.WriteUInt64LittleEndian(digestInput.AsSpan(offset, sizeof(ulong)), (ulong)topicBytes.Length);
        offset += sizeof(ulong);

        topicBytes.CopyTo(digestInput.AsSpan(offset, topicBytes.Length));
        offset += topicBytes.Length;

        dataBytes.CopyTo(digestInput.AsSpan(offset, dataBytes.Length));

        var digest = SHA256.HashData(digestInput);
        return new MessageId(digest.AsSpan(0, 20).ToArray());
    }
}
