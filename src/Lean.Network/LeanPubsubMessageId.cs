using System.Buffers;
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
        var rawData = message.Data.Span;
        ReadOnlySpan<byte> domain = MessageDomainInvalidSnappy;

        byte[]? decompressed = null;
        try
        {
            decompressed = Snappy.DecompressToArray(rawData);
            domain = MessageDomainValidSnappy;
        }
        catch
        {
            // Keep raw bytes when payload is not snappy-encoded.
        }

        ReadOnlySpan<byte> dataSpan = decompressed != null ? decompressed.AsSpan() : rawData;
        int topicByteCount = Encoding.ASCII.GetByteCount(topic);
        int totalLen = domain.Length + sizeof(ulong) + topicByteCount + dataSpan.Length;

        byte[]? rented = null;
        Span<byte> digestInput = totalLen <= 1024
            ? stackalloc byte[totalLen]
            : (rented = ArrayPool<byte>.Shared.Rent(totalLen)).AsSpan(0, totalLen);
        try
        {
            var offset = 0;

            domain.CopyTo(digestInput.Slice(offset));
            offset += domain.Length;

            BinaryPrimitives.WriteUInt64LittleEndian(digestInput.Slice(offset, sizeof(ulong)), (ulong)topicByteCount);
            offset += sizeof(ulong);

            Encoding.ASCII.GetBytes(topic, digestInput.Slice(offset, topicByteCount));
            offset += topicByteCount;

            dataSpan.CopyTo(digestInput.Slice(offset));

            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(digestInput, hash);
            return new MessageId(hash[..20].ToArray());
        }
        finally
        {
            if (rented != null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
