using Lean.Consensus.Types;

namespace Lean.Consensus;

public enum ForkChoiceRejectReason
{
    None = 0,
    DuplicateBlock = 1,
    UnknownParent = 2,
    FutureSlot = 3,
    InvalidSlot = 4,
    ProposerMismatch = 5,
    InvalidAttestation = 6,
    StateTransitionFailed = 7
}

public sealed class ForkChoiceApplyResult
{
    private ForkChoiceApplyResult(
        bool accepted,
        bool headChanged,
        ForkChoiceRejectReason rejectReason,
        string reason,
        ulong headSlot,
        Bytes32 headRoot)
    {
        Accepted = accepted;
        HeadChanged = headChanged;
        RejectReason = rejectReason;
        Reason = reason;
        HeadSlot = headSlot;
        HeadRoot = headRoot;
    }

    public bool Accepted { get; }

    public bool HeadChanged { get; }

    public ForkChoiceRejectReason RejectReason { get; }

    public string Reason { get; }

    public ulong HeadSlot { get; }

    public Bytes32 HeadRoot { get; }

    public static ForkChoiceApplyResult AcceptedResult(bool headChanged, ulong headSlot, Bytes32 headRoot)
    {
        return new ForkChoiceApplyResult(
            accepted: true,
            headChanged: headChanged,
            rejectReason: ForkChoiceRejectReason.None,
            reason: headChanged ? "Head updated." : "Block accepted without head change.",
            headSlot: headSlot,
            headRoot: headRoot);
    }

    public static ForkChoiceApplyResult Rejected(
        ForkChoiceRejectReason rejectReason,
        string reason,
        ulong headSlot,
        Bytes32 headRoot)
    {
        return new ForkChoiceApplyResult(
            accepted: false,
            headChanged: false,
            rejectReason: rejectReason,
            reason: reason,
            headSlot: headSlot,
            headRoot: headRoot);
    }
}
