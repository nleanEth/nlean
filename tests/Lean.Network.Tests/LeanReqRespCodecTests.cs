using NUnit.Framework;

namespace Lean.Network.Tests;

[TestFixture]
public class LeanReqRespCodecTests
{
    [Test]
    public void EncodeDecodeStatus_RoundTrips()
    {
        var finalizedRoot = Enumerable.Range(0, LeanStatusMessage.RootLength).Select(i => (byte)i).ToArray();
        var headRoot = Enumerable.Range(0, LeanStatusMessage.RootLength).Select(i => (byte)(255 - i)).ToArray();
        var status = new LeanStatusMessage(
            finalizedRoot,
            finalizedSlot: 42,
            headRoot,
            headSlot: 64);

        var encoded = LeanReqRespCodec.EncodeStatus(status);
        var decoded = LeanReqRespCodec.DecodeStatus(encoded);

        Assert.That(decoded.FinalizedSlot, Is.EqualTo(42));
        Assert.That(decoded.HeadSlot, Is.EqualTo(64));
        Assert.That(decoded.FinalizedRoot, Is.EqualTo(finalizedRoot));
        Assert.That(decoded.HeadRoot, Is.EqualTo(headRoot));
    }

    [Test]
    public void EncodeDecodeBlocksByRootRequest_RoundTrips()
    {
        var roots = new[]
        {
            Enumerable.Range(0, LeanReqRespCodec.RootLength).Select(i => (byte)i).ToArray(),
            Enumerable.Range(0, LeanReqRespCodec.RootLength).Select(i => (byte)(i + 10)).ToArray()
        };

        var encoded = LeanReqRespCodec.EncodeBlocksByRootRequest(roots);
        var decoded = LeanReqRespCodec.DecodeBlocksByRootRequest(encoded);

        Assert.That(decoded, Has.Length.EqualTo(2));
        Assert.That(decoded[0], Is.EqualTo(roots[0]));
        Assert.That(decoded[1], Is.EqualTo(roots[1]));
    }

    [Test]
    public void DecodeBlocksByRootRequest_WithInvalidLength_Throws()
    {
        var invalid = new byte[LeanReqRespCodec.RootLength + 1];

        Assert.That(
            () => LeanReqRespCodec.DecodeBlocksByRootRequest(invalid),
            Throws.InvalidOperationException);
    }

    [Test]
    public void EncodeDecodeBlocksByRangeRequest_RoundTrips()
    {
        var encoded = LeanReqRespCodec.EncodeBlocksByRangeRequest(startSlot: 7, count: 32);
        var (startSlot, count) = LeanReqRespCodec.DecodeBlocksByRangeRequest(encoded);

        Assert.That(encoded, Has.Length.EqualTo(LeanReqRespCodec.BlocksByRangeRequestLength));
        Assert.That(startSlot, Is.EqualTo(7UL));
        Assert.That(count, Is.EqualTo(32UL));
    }

    [Test]
    public void EncodeBlocksByRangeRequest_LittleEndianLayout()
    {
        // hive's mock sender expects two little-endian uint64s back-to-back —
        // verifying the byte layout here keeps us pinned to the on-wire format.
        var encoded = LeanReqRespCodec.EncodeBlocksByRangeRequest(startSlot: 0x0102030405060708UL, count: 0xAABBCCDDEEFF0011UL);
        Assert.That(encoded, Is.EqualTo(new byte[]
        {
            0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01,
            0x11, 0x00, 0xFF, 0xEE, 0xDD, 0xCC, 0xBB, 0xAA
        }));
    }

    [Test]
    public void DecodeBlocksByRangeRequest_WithWrongLength_Throws()
    {
        var tooShort = new byte[LeanReqRespCodec.BlocksByRangeRequestLength - 1];
        Assert.That(
            () => LeanReqRespCodec.DecodeBlocksByRangeRequest(tooShort),
            Throws.InvalidOperationException);

        var tooLong = new byte[LeanReqRespCodec.BlocksByRangeRequestLength + 1];
        Assert.That(
            () => LeanReqRespCodec.DecodeBlocksByRangeRequest(tooLong),
            Throws.InvalidOperationException);
    }

    [Test]
    public void EncodeBlocksByRootRequest_MatchesGoldenPayload()
    {
        var roots = new[]
        {
            Enumerable.Range(0x00, LeanReqRespCodec.RootLength).Select(i => (byte)i).ToArray(),
            Enumerable.Range(0x80, LeanReqRespCodec.RootLength).Select(i => (byte)i).ToArray()
        };

        var encoded = LeanReqRespCodec.EncodeBlocksByRootRequest(roots);

        const string expectedHex =
            "04000000" +
            "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F" +
            "808182838485868788898A8B8C8D8E8F909192939495969798999A9B9C9D9E9F";

        Assert.That(Convert.ToHexString(encoded), Is.EqualTo(expectedHex));
    }

    [Test]
    public void DecodeBlocksByRootRequest_GoldenPayload_MatchesRoots()
    {
        var payload = Convert.FromHexString(
            "04000000" +
            "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F" +
            "808182838485868788898A8B8C8D8E8F909192939495969798999A9B9C9D9E9F");

        var decoded = LeanReqRespCodec.DecodeBlocksByRootRequest(payload);

        Assert.That(decoded, Has.Length.EqualTo(2));
        Assert.That(Convert.ToHexString(decoded[0]),
            Is.EqualTo("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F"));
        Assert.That(Convert.ToHexString(decoded[1]),
            Is.EqualTo("808182838485868788898A8B8C8D8E8F909192939495969798999A9B9C9D9E9F"));
    }

    [Test]
    public void DecodeBlocksByRootRequest_LegacyPayload_Throws()
    {
        var payload = Convert.FromHexString(
            "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F" +
            "808182838485868788898A8B8C8D8E8F909192939495969798999A9B9C9D9E9F");

        Assert.That(
            () => LeanReqRespCodec.DecodeBlocksByRootRequest(payload),
            Throws.InvalidOperationException);
    }

    [Test]
    public void EncodeSnappyFramedPayload_StartsWithSnappyStreamHeader()
    {
        var payload = Enumerable.Range(0, 80).Select(i => (byte)i).ToArray();

        var encoded = LeanReqRespCodec.EncodeSnappyFramedPayload(payload);

        const string expectedHeaderHex = "FF060000734E61507059";
        Assert.That(Convert.ToHexString(encoded.AsSpan(0, 10)), Is.EqualTo(expectedHeaderHex));
    }

    [Test]
    public void EncodeDecodeSnappyFramedPayload_RoundTrips()
    {
        var payload = Enumerable.Range(0, 128).Select(i => (byte)(255 - i)).ToArray();

        var snappyPayload = LeanReqRespCodec.EncodeSnappyFramedPayload(payload);
        var decoded = LeanReqRespCodec.DecodeSnappyFramedPayload(snappyPayload, payload.Length);

        Assert.That(decoded, Is.EqualTo(payload));
    }

    [Test]
    public void EncodeDecodeSnappyFramedPayload_ZeroLength_RoundTrips()
    {
        var payload = Array.Empty<byte>();

        var snappyPayload = LeanReqRespCodec.EncodeSnappyFramedPayload(payload);
        var decoded = LeanReqRespCodec.DecodeSnappyFramedPayload(snappyPayload, payload.Length);

        Assert.That(decoded, Is.Empty);
    }

    [Test]
    public void DecodeSnappyFramedPayload_WithoutStreamIdentifier_UncompressedChunk_RoundTrips()
    {
        var payload = Enumerable.Range(0, 64).Select(i => (byte)(i + 1)).ToArray();
        var chunkLength = payload.Length + 4;
        var framed = new byte[4 + 4 + payload.Length];
        framed[0] = 0x01; // uncompressed data chunk
        framed[1] = (byte)(chunkLength & 0xFF);
        framed[2] = (byte)((chunkLength >> 8) & 0xFF);
        framed[3] = (byte)((chunkLength >> 16) & 0xFF);
        // Keep checksum bytes as zero for decode compatibility checks.
        payload.CopyTo(framed, 8);

        var decoded = LeanReqRespCodec.DecodeSnappyFramedPayload(framed, payload.Length);

        Assert.That(decoded, Is.EqualTo(payload));
    }

    [Test]
    public void DecodeSnappyFramedPayload_RawUncompressedFallback_RoundTrips()
    {
        var payload = Enumerable.Range(0, 48).Select(i => (byte)(255 - i)).ToArray();

        var decoded = LeanReqRespCodec.DecodeSnappyFramedPayload(payload, payload.Length);

        Assert.That(decoded, Is.EqualTo(payload));
    }

    [Test]
    public void DecodeSnappyFramedPayload_WithTrailingBytes_Throws()
    {
        var payload = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var snappyPayload = LeanReqRespCodec.EncodeSnappyFramedPayload(payload);
        var withTrailingBytes = snappyPayload.Concat(new byte[] { 0x00, 0x01, 0x02 }).ToArray();

        Assert.That(
            () => LeanReqRespCodec.DecodeSnappyFramedPayload(withTrailingBytes, payload.Length),
            Throws.InvalidOperationException.With.Message.Contains("trailing or truncated"));
    }

    [Test]
    public void DecodeSnappyFramedPayload_WithInvalidDeclaredLength_Throws()
    {
        var payload = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var snappyPayload = LeanReqRespCodec.EncodeSnappyFramedPayload(payload);

        Assert.That(
            () => LeanReqRespCodec.DecodeSnappyFramedPayload(snappyPayload, payload.Length + 1),
            Throws.InvalidOperationException.With.Message.Contains("Unexpected end of snappy payload"));
    }
}
