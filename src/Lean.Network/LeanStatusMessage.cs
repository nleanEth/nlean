namespace Lean.Network;

public sealed class LeanStatusMessage
{
    public const int RootLength = 32;
    public const int CheckpointLength = RootLength + sizeof(ulong);
    public const int Length = CheckpointLength * 2;

    public LeanStatusMessage(
        ReadOnlySpan<byte> finalizedRoot,
        ulong finalizedSlot,
        ReadOnlySpan<byte> headRoot,
        ulong headSlot)
    {
        if (finalizedRoot.Length != RootLength)
        {
            throw new ArgumentException($"Finalized root must be {RootLength} bytes.", nameof(finalizedRoot));
        }

        if (headRoot.Length != RootLength)
        {
            throw new ArgumentException($"Head root must be {RootLength} bytes.", nameof(headRoot));
        }

        FinalizedRoot = finalizedRoot.ToArray();
        FinalizedSlot = finalizedSlot;
        HeadRoot = headRoot.ToArray();
        HeadSlot = headSlot;
    }

    public byte[] FinalizedRoot { get; }

    public ulong FinalizedSlot { get; }

    public byte[] HeadRoot { get; }

    public ulong HeadSlot { get; }

    public static LeanStatusMessage Zero() =>
        new(new byte[RootLength], 0, new byte[RootLength], 0);
}
