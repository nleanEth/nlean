namespace Lean.Consensus.Types;

public sealed record Checkpoint(Bytes32 Root, Slot Slot)
{
    public static Checkpoint Default() => new(Bytes32.Zero(), new Slot(0));

    public byte[] HashTreeRoot()
    {
        return SszInterop.HashContainer(
            Root.HashTreeRoot(),
            SszInterop.HashUInt64(Slot.Value));
    }
}
