namespace Lean.Consensus.Types;

public enum BlockGossipDecodeFailure
{
    None = 0,
    EmptyPayload = 1,
    InvalidSsz = 2,
    MessageRootDerivationFailed = 3
}

public sealed record BlockGossipDecodeResult
{
    private BlockGossipDecodeResult(
        bool isSuccess,
        SignedBlock? signedBlock,
        Bytes32? blockMessageRoot,
        BlockGossipDecodeFailure failure,
        string reason)
    {
        IsSuccess = isSuccess;
        SignedBlock = signedBlock;
        BlockMessageRoot = blockMessageRoot;
        Failure = failure;
        Reason = reason;
    }

    public bool IsSuccess { get; }

    public SignedBlock? SignedBlock { get; }

    public Bytes32? BlockMessageRoot { get; }

    public BlockGossipDecodeFailure Failure { get; }

    public string Reason { get; }

    public static BlockGossipDecodeResult Success(SignedBlock signedBlock, Bytes32 blockMessageRoot)
    {
        return new BlockGossipDecodeResult(
            isSuccess: true,
            signedBlock: signedBlock,
            blockMessageRoot: blockMessageRoot,
            failure: BlockGossipDecodeFailure.None,
            reason: "Payload decoded and validated.");
    }

    public static BlockGossipDecodeResult Fail(BlockGossipDecodeFailure failure, string reason)
    {
        return new BlockGossipDecodeResult(
            isSuccess: false,
            signedBlock: null,
            blockMessageRoot: null,
            failure: failure,
            reason: reason);
    }
}
