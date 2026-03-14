using Lean.Storage;
using NUnit.Framework;

namespace Lean.Consensus.Tests;

public sealed class PrefixScanTests
{
    [Test]
    public void PrefixScan_ReturnsMatchingEntries()
    {
        var store = new InMemoryKeyValueStore();
        var prefix = "test:prefix:"u8.ToArray();
        var otherPrefix = "other:prefix:"u8.ToArray();

        store.Put("test:prefix:key1"u8, new byte[] { 0x01 });
        store.Put("test:prefix:key2"u8, new byte[] { 0x02 });
        store.Put("other:prefix:key3"u8, new byte[] { 0x03 });

        var results = store.PrefixScan(prefix).ToList();

        Assert.That(results.Count, Is.EqualTo(2));
    }

    [Test]
    public void PrefixScan_ReturnsEmptyForNoMatch()
    {
        var store = new InMemoryKeyValueStore();
        store.Put("abc:key1"u8, new byte[] { 0x01 });

        var results = store.PrefixScan("xyz:"u8.ToArray()).ToList();

        Assert.That(results, Is.Empty);
    }

    [Test]
    public void PrefixScan_ReturnsEmptyForEmptyStore()
    {
        var store = new InMemoryKeyValueStore();

        var results = store.PrefixScan("any:"u8.ToArray()).ToList();

        Assert.That(results, Is.Empty);
    }
}
