using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace Lean.Consensus;

public sealed class ConsensusHeadState
{
    private const byte FormatVersionV1 = 1;
    private const byte FormatVersionV2 = 2;
    private const int VersionOffset = 0;
    private const int V1HeadSlotOffset = VersionOffset + sizeof(byte);
    private const int V1HeadRootLengthOffset = V1HeadSlotOffset + sizeof(ulong);
    private const int V1HeaderLength = V1HeadRootLengthOffset + sizeof(int);

    public ConsensusHeadState(ulong headSlot, ReadOnlySpan<byte> headRoot)
        : this(
            headSlot,
            headRoot,
            headSlot,
            headRoot,
            headSlot,
            headRoot,
            headSlot,
            headRoot)
    {
    }

    public ConsensusHeadState(
        ulong headSlot,
        ReadOnlySpan<byte> headRoot,
        ulong latestJustifiedSlot,
        ReadOnlySpan<byte> latestJustifiedRoot,
        ulong latestFinalizedSlot,
        ReadOnlySpan<byte> latestFinalizedRoot,
        ulong safeTargetSlot,
        ReadOnlySpan<byte> safeTargetRoot)
    {
        HeadSlot = headSlot;
        HeadRoot = headRoot.ToArray();
        LatestJustifiedSlot = latestJustifiedSlot;
        LatestJustifiedRoot = latestJustifiedRoot.ToArray();
        LatestFinalizedSlot = latestFinalizedSlot;
        LatestFinalizedRoot = latestFinalizedRoot.ToArray();
        SafeTargetSlot = safeTargetSlot;
        SafeTargetRoot = safeTargetRoot.ToArray();
    }

    public ulong HeadSlot { get; }

    public byte[] HeadRoot { get; }

    public ulong LatestJustifiedSlot { get; }

    public byte[] LatestJustifiedRoot { get; }

    public ulong LatestFinalizedSlot { get; }

    public byte[] LatestFinalizedRoot { get; }

    public ulong SafeTargetSlot { get; }

    public byte[] SafeTargetRoot { get; }

    public byte[] Serialize()
    {
        var payloadLength =
            sizeof(byte) +
            sizeof(ulong) + sizeof(int) + HeadRoot.Length +
            sizeof(ulong) + sizeof(int) + LatestJustifiedRoot.Length +
            sizeof(ulong) + sizeof(int) + LatestFinalizedRoot.Length +
            sizeof(ulong) + sizeof(int) + SafeTargetRoot.Length;
        var payload = new byte[payloadLength];

        var cursor = 0;
        payload[cursor++] = FormatVersionV2;

        cursor = WriteField(payload, cursor, HeadSlot, HeadRoot);
        cursor = WriteField(payload, cursor, LatestJustifiedSlot, LatestJustifiedRoot);
        cursor = WriteField(payload, cursor, LatestFinalizedSlot, LatestFinalizedRoot);
        cursor = WriteField(payload, cursor, SafeTargetSlot, SafeTargetRoot);
        return payload;
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> payload, [NotNullWhen(true)] out ConsensusHeadState? state)
    {
        state = null;
        if (payload.Length < sizeof(byte))
        {
            return false;
        }

        var version = payload[VersionOffset];
        if (version == FormatVersionV1)
        {
            return TryDeserializeV1(payload, out state);
        }

        if (version == FormatVersionV2)
        {
            return TryDeserializeV2(payload, out state);
        }

        return false;
    }

    private static bool TryDeserializeV1(ReadOnlySpan<byte> payload, [NotNullWhen(true)] out ConsensusHeadState? state)
    {
        state = null;
        if (payload.Length < V1HeaderLength)
        {
            return false;
        }

        var headSlot = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(V1HeadSlotOffset, sizeof(ulong)));
        var headRootLength = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(V1HeadRootLengthOffset, sizeof(int)));
        if (headRootLength < 0 || payload.Length != V1HeaderLength + headRootLength)
        {
            return false;
        }

        var headRoot = payload.Slice(V1HeaderLength, headRootLength);
        state = new ConsensusHeadState(headSlot, headRoot);
        return true;
    }

    private static bool TryDeserializeV2(ReadOnlySpan<byte> payload, [NotNullWhen(true)] out ConsensusHeadState? state)
    {
        state = null;

        var cursor = sizeof(byte);
        if (!TryReadField(payload, ref cursor, out var headSlot, out var headRoot))
        {
            return false;
        }

        if (!TryReadField(payload, ref cursor, out var justifiedSlot, out var justifiedRoot))
        {
            return false;
        }

        if (!TryReadField(payload, ref cursor, out var finalizedSlot, out var finalizedRoot))
        {
            return false;
        }

        if (!TryReadField(payload, ref cursor, out var safeTargetSlot, out var safeTargetRoot))
        {
            return false;
        }

        if (cursor != payload.Length)
        {
            return false;
        }

        state = new ConsensusHeadState(
            headSlot,
            headRoot,
            justifiedSlot,
            justifiedRoot,
            finalizedSlot,
            finalizedRoot,
            safeTargetSlot,
            safeTargetRoot);
        return true;
    }

    private static int WriteField(byte[] payload, int cursor, ulong slot, ReadOnlySpan<byte> root)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(cursor, sizeof(ulong)), slot);
        cursor += sizeof(ulong);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(cursor, sizeof(int)), root.Length);
        cursor += sizeof(int);
        root.CopyTo(payload.AsSpan(cursor, root.Length));
        cursor += root.Length;
        return cursor;
    }

    private static bool TryReadField(
        ReadOnlySpan<byte> payload,
        ref int cursor,
        out ulong slot,
        out ReadOnlySpan<byte> root)
    {
        slot = 0;
        root = default;

        if (payload.Length < cursor + sizeof(ulong) + sizeof(int))
        {
            return false;
        }

        slot = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(cursor, sizeof(ulong)));
        cursor += sizeof(ulong);
        var rootLength = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(cursor, sizeof(int)));
        cursor += sizeof(int);
        if (rootLength < 0 || payload.Length < cursor + rootLength)
        {
            return false;
        }

        root = payload.Slice(cursor, rootLength);
        cursor += rootLength;
        return true;
    }
}
