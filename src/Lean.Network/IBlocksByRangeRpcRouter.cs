namespace Lean.Network;

/// <summary>
/// Routes incoming BlocksByRange RPC requests to a slot-keyed lookup against
/// the local block store. Mirrors <see cref="IBlocksByRootRpcRouter"/> but
/// keyed by slot — the protocol handler iterates the requested range and
/// calls <see cref="ResolveAsync"/> once per slot.
/// </summary>
public interface IBlocksByRangeRpcRouter
{
    void SetHandler(Func<ulong, CancellationToken, ValueTask<byte[]?>>? handler);
    ValueTask<byte[]?> ResolveAsync(ulong slot, CancellationToken cancellationToken);
}
