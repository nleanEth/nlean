using RocksDbSharp;

namespace Lean.Storage;

public sealed class RocksDbKeyValueStore : IKeyValueStore, IDisposable
{
    private readonly RocksDb _db;

    public RocksDbKeyValueStore(StorageConfig config, string name)
    {
        var path = Path.Combine(config.DataDir, name);
        Directory.CreateDirectory(path);

        var options = new DbOptions()
            .SetCreateIfMissing(true)
            .SetCreateMissingColumnFamilies(true);

        _db = RocksDb.Open(options, path);
    }

    public byte[]? Get(ReadOnlySpan<byte> key)
    {
        return _db.Get(key.ToArray());
    }

    public void Put(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        _db.Put(key.ToArray(), value.ToArray());
    }

    public void Delete(ReadOnlySpan<byte> key)
    {
        _db.Remove(key.ToArray());
    }

    public IWriteBatch StartBatch() => new RocksDbWriteBatch(_db);

    public void Dispose()
    {
        _db.Dispose();
    }

    private sealed class RocksDbWriteBatch : IWriteBatch
    {
        private readonly RocksDb _db;
        private readonly WriteBatch _batch = new();

        public RocksDbWriteBatch(RocksDb db) => _db = db;

        public void Put(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
            => _batch.Put(key.ToArray(), value.ToArray());

        public void Delete(ReadOnlySpan<byte> key)
            => _batch.Delete(key.ToArray());

        public void Commit() => _db.Write(_batch);

        public void Dispose() => _batch.Dispose();
    }
}
