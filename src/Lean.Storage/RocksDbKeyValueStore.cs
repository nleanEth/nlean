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

    public void Dispose()
    {
        _db.Dispose();
    }
}
