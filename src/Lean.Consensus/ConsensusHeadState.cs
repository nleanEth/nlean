using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace Lean.Consensus;

public sealed class ConsensusHeadState
{
    private const byte FormatVersion = 1;
    private const int VersionOffset = 0;
    private const int HeadSlotOffset = VersionOffset + sizeof(byte);
    private const int HeadRootLengthOffset = HeadSlotOffset + sizeof(ulong);
    private const int HeaderLength = HeadRootLengthOffset + sizeof(int);

    public ConsensusHeadState(ulong headSlot, ReadOnlySpan<byte> headRoot)
    {
        HeadSlot = headSlot;
        HeadRoot = headRoot.ToArray();
    }

    public ulong HeadSlot { get; }

    public byte[] HeadRoot { get; }

    public byte[] Serialize()
    {
        var payload = new byte[HeaderLength + HeadRoot.Length];
        payload[VersionOffset] = FormatVersion;
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(HeadSlotOffset, sizeof(ulong)), HeadSlot);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(HeadRootLengthOffset, sizeof(int)), HeadRoot.Length);
        HeadRoot.CopyTo(payload.AsSpan(HeaderLength));
        return payload;
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> payload, [NotNullWhen(true)] out ConsensusHeadState? state)
    {
        if (payload.Length < HeaderLength)
        {
            state = null;
            return false;
        }

        if (payload[VersionOffset] != FormatVersion)
        {
            state = null;
            return false;
        }

        var headSlot = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(HeadSlotOffset, sizeof(ulong)));
        var headRootLength = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(HeadRootLengthOffset, sizeof(int)));
        if (headRootLength < 0 || payload.Length != HeaderLength + headRootLength)
        {
            state = null;
            return false;
        }

        state = new ConsensusHeadState(headSlot, payload.Slice(HeaderLength, headRootLength));
        return true;
    }
}
