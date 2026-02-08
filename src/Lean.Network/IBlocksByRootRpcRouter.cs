namespace Lean.Network;

public interface IBlocksByRootRpcRouter
{
    void SetHandler(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<byte[]?>>? handler);
    ValueTask<byte[]?> ResolveAsync(ReadOnlyMemory<byte> blockRoot, CancellationToken cancellationToken);
}
