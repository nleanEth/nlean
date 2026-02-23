namespace Lean.Storage;

/// <summary>
/// Typed consensus database interface matching leanSpec's Database protocol.
/// All keys and values are byte arrays; callers handle serialization.
/// </summary>
public interface IDatabase
{
    // Blocks
    byte[]? GetBlock(ReadOnlySpan<byte> root);
    void PutBlock(ReadOnlySpan<byte> root, ReadOnlySpan<byte> sszBlock);
    bool HasBlock(ReadOnlySpan<byte> root);

    // States
    byte[]? GetState(ReadOnlySpan<byte> root);
    void PutState(ReadOnlySpan<byte> root, ReadOnlySpan<byte> sszState);

    // Checkpoints
    byte[]? GetJustifiedCheckpoint();
    void PutJustifiedCheckpoint(ReadOnlySpan<byte> sszCheckpoint);
    byte[]? GetFinalizedCheckpoint();
    void PutFinalizedCheckpoint(ReadOnlySpan<byte> sszCheckpoint);

    // Head tracking
    byte[]? GetHeadRoot();
    void PutHeadRoot(ReadOnlySpan<byte> root);

    // Slot index: maps slot -> list of block roots at that slot
    byte[]? GetBlockRootsBySlot(ulong slot);
    void PutBlockRootsBySlot(ulong slot, ReadOnlySpan<byte> roots);

    // Attestations
    byte[]? GetAttestation(ReadOnlySpan<byte> key);
    void PutAttestation(ReadOnlySpan<byte> key, ReadOnlySpan<byte> sszAttestation);
}
