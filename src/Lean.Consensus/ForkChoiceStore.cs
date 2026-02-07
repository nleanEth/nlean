using Lean.Consensus.Types;

namespace Lean.Consensus;

public sealed class ForkChoiceStore
{
    private readonly Dictionary<string, ForkChoiceNode> _nodes = new(StringComparer.Ordinal);
    private string _headKey;
    private ulong _headSlot;
    private Bytes32 _headRoot = Bytes32.Zero();

    public ForkChoiceStore()
    {
        _headKey = ToKey(_headRoot);
        _nodes[_headKey] = new ForkChoiceNode(0, null);
    }

    public ulong HeadSlot => _headSlot;

    public Bytes32 HeadRoot => _headRoot;

    public void InitializeHead(ConsensusHeadState state)
    {
        if (state.HeadRoot.Length != SszEncoding.Bytes32Length)
        {
            return;
        }

        _nodes.Clear();
        _headRoot = new Bytes32(state.HeadRoot);
        _headSlot = state.HeadSlot;
        _headKey = ToKey(_headRoot);
        _nodes[_headKey] = new ForkChoiceNode(state.HeadSlot, null);
    }

    public ForkChoiceApplyResult ApplyBlock(SignedBlockWithAttestation signedBlock, Bytes32 blockRoot)
    {
        ArgumentNullException.ThrowIfNull(signedBlock);

        var block = signedBlock.Message.Block;
        var attestationSlot = signedBlock.Message.ProposerAttestation.Data.Slot.Value;
        if (attestationSlot > block.Slot.Value)
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.InvalidAttestation,
                $"Proposer attestation slot {attestationSlot} exceeds block slot {block.Slot.Value}.",
                _headSlot,
                _headRoot);
        }

        var blockKey = ToKey(blockRoot);
        if (_nodes.ContainsKey(blockKey))
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.DuplicateBlock,
                "Duplicate block root.",
                _headSlot,
                _headRoot);
        }

        var parentKey = ToKey(block.ParentRoot);
        if (!_nodes.ContainsKey(parentKey))
        {
            return ForkChoiceApplyResult.Rejected(
                ForkChoiceRejectReason.UnknownParent,
                $"Unknown parent root {parentKey}.",
                _headSlot,
                _headRoot);
        }

        _nodes[blockKey] = new ForkChoiceNode(block.Slot.Value, parentKey);

        var headChanged = ShouldReplaceHead(block.Slot.Value, blockKey);
        if (headChanged)
        {
            _headSlot = block.Slot.Value;
            _headRoot = blockRoot;
            _headKey = blockKey;
        }

        return ForkChoiceApplyResult.AcceptedResult(headChanged, _headSlot, _headRoot);
    }

    private bool ShouldReplaceHead(ulong candidateSlot, string candidateKey)
    {
        if (candidateSlot > _headSlot)
        {
            return true;
        }

        if (candidateSlot < _headSlot)
        {
            return false;
        }

        return string.CompareOrdinal(candidateKey, _headKey) > 0;
    }

    private static string ToKey(Bytes32 root)
    {
        return Convert.ToHexString(root.AsSpan());
    }

    private sealed record ForkChoiceNode(ulong Slot, string? ParentKey);
}
