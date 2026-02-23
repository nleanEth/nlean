using System.Buffers.Binary;

namespace Lean.Storage;

/// <summary>
/// Implements IDatabase on top of any IKeyValueStore using prefixed keys.
/// </summary>
public sealed class PrefixedDatabase : IDatabase
{
    private static readonly byte[] BlockPrefix = "blk:"u8.ToArray();
    private static readonly byte[] StatePrefix = "st:"u8.ToArray();
    private static readonly byte[] AttestationPrefix = "att:"u8.ToArray();
    private static readonly byte[] SlotIndexPrefix = "idx:"u8.ToArray();
    private static readonly byte[] JustifiedKey = "cp:justified"u8.ToArray();
    private static readonly byte[] FinalizedKey = "cp:finalized"u8.ToArray();
    private static readonly byte[] HeadRootKey = "meta:head"u8.ToArray();

    private readonly IKeyValueStore _store;

    public PrefixedDatabase(IKeyValueStore store)
    {
        _store = store;
    }

    public byte[]? GetBlock(ReadOnlySpan<byte> root) => _store.Get(Prefixed(BlockPrefix, root));
    public void PutBlock(ReadOnlySpan<byte> root, ReadOnlySpan<byte> sszBlock) =>
        _store.Put(Prefixed(BlockPrefix, root), sszBlock);
    public bool HasBlock(ReadOnlySpan<byte> root) => _store.Get(Prefixed(BlockPrefix, root)) is not null;

    public byte[]? GetState(ReadOnlySpan<byte> root) => _store.Get(Prefixed(StatePrefix, root));
    public void PutState(ReadOnlySpan<byte> root, ReadOnlySpan<byte> sszState) =>
        _store.Put(Prefixed(StatePrefix, root), sszState);

    public byte[]? GetJustifiedCheckpoint() => _store.Get(JustifiedKey);
    public void PutJustifiedCheckpoint(ReadOnlySpan<byte> sszCheckpoint) =>
        _store.Put(JustifiedKey, sszCheckpoint);

    public byte[]? GetFinalizedCheckpoint() => _store.Get(FinalizedKey);
    public void PutFinalizedCheckpoint(ReadOnlySpan<byte> sszCheckpoint) =>
        _store.Put(FinalizedKey, sszCheckpoint);

    public byte[]? GetHeadRoot() => _store.Get(HeadRootKey);
    public void PutHeadRoot(ReadOnlySpan<byte> root) => _store.Put(HeadRootKey, root);

    public byte[]? GetBlockRootsBySlot(ulong slot) => _store.Get(SlotKey(slot));
    public void PutBlockRootsBySlot(ulong slot, ReadOnlySpan<byte> roots) =>
        _store.Put(SlotKey(slot), roots);

    public byte[]? GetAttestation(ReadOnlySpan<byte> key) =>
        _store.Get(Prefixed(AttestationPrefix, key));
    public void PutAttestation(ReadOnlySpan<byte> key, ReadOnlySpan<byte> sszAttestation) =>
        _store.Put(Prefixed(AttestationPrefix, key), sszAttestation);

    private static byte[] Prefixed(byte[] prefix, ReadOnlySpan<byte> key)
    {
        var result = new byte[prefix.Length + key.Length];
        prefix.CopyTo(result, 0);
        key.CopyTo(result.AsSpan(prefix.Length));
        return result;
    }

    private static byte[] SlotKey(ulong slot)
    {
        var result = new byte[SlotIndexPrefix.Length + 8];
        SlotIndexPrefix.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(SlotIndexPrefix.Length), slot);
        return result;
    }
}
