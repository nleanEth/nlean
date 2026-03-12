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

    public IWriteBatch StartBatch() => new InMemoryWriteBatch(this);

    private sealed class InMemoryWriteBatch : IWriteBatch
    {
        private readonly InMemoryKeyValueStore _store;
        private readonly List<(bool IsPut, byte[] Key, byte[]? Value)> _ops = new();

        public InMemoryWriteBatch(InMemoryKeyValueStore store) => _store = store;

        public void Put(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
            => _ops.Add((true, key.ToArray(), value.ToArray()));

        public void Delete(ReadOnlySpan<byte> key)
            => _ops.Add((false, key.ToArray(), null));

        public void Commit()
        {
            foreach (var (isPut, key, value) in _ops)
            {
                if (isPut)
                    _store.Put(key, value!);
                else
                    _store.Delete(key);
            }
        }

        public void Dispose() { }
    }
}
