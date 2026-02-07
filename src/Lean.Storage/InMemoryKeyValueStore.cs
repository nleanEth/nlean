using System.Collections.Concurrent;

namespace Lean.Storage;

public sealed class InMemoryKeyValueStore : IKeyValueStore
{
    private readonly ConcurrentDictionary<string, byte[]> _store = new();

    public byte[]? Get(ReadOnlySpan<byte> key)
    {
        return _store.TryGetValue(Convert.ToHexString(key), out var value) ? value : null;
    }

    public void Put(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        _store[Convert.ToHexString(key)] = value.ToArray();
    }

    public void Delete(ReadOnlySpan<byte> key)
    {
        _store.TryRemove(Convert.ToHexString(key), out _);
    }
}
