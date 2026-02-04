namespace Lean.Consensus.Types;

public sealed record Checkpoint(Bytes32 Root, Slot Slot)
{
    public static Checkpoint Default() => new(Bytes32.Zero(), new Slot(0));
}
