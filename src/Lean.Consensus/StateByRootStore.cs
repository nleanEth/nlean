using System.Diagnostics.CodeAnalysis;
using System.Text;
using Lean.Consensus.Types;
using Lean.Storage;

namespace Lean.Consensus;

public sealed class StateByRootStore : IStateByRootStore
{
    private const string KeyPrefix = "consensus:state:";
    internal static readonly byte[] KeyPrefixBytes = Encoding.ASCII.GetBytes(KeyPrefix);
    private readonly IKeyValueStore _store;

    public StateByRootStore(IKeyValueStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public void Save(Bytes32 blockRoot, State state)
    {
        _store.Put(BuildKey(blockRoot), SszEncoding.Encode(state));
    }

    public bool TryLoad(Bytes32 blockRoot, [NotNullWhen(true)] out State? state)
    {
        state = null;
        var payload = _store.Get(BuildKey(blockRoot));
        if (payload is null)
            return false;

        try
        {
            state = SszDecoding.DecodeState(payload);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Delete(Bytes32 blockRoot)
    {
        _store.Delete(BuildKey(blockRoot));
    }

    private static byte[] BuildKey(Bytes32 blockRoot)
    {
        var rootHex = Convert.ToHexString(blockRoot.AsSpan());
        return Encoding.ASCII.GetBytes($"{KeyPrefix}{rootHex}");
    }
}
