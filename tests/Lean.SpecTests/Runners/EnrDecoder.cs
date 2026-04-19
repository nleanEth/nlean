using System.Net;
using System.Text;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Math;

namespace Lean.SpecTests.Runners;

internal sealed class DecodedEnr
{
    public byte[] Rlp { get; init; } = Array.Empty<byte>();
    public ulong Seq { get; init; }
    public string? IdentityScheme { get; init; }
    public byte[]? Secp256k1PublicKeyCompressed { get; init; }
    public byte[]? NodeId { get; init; }
    public IPAddress? Ip4 { get; init; }
    public IPAddress? Ip6 { get; init; }
    public int? UdpPort { get; init; }
    public int? TcpPort { get; init; }
    public int? Udp6Port { get; init; }
    public int? Tcp6Port { get; init; }
    public int? QuicPort { get; init; }
    public int? Quic6Port { get; init; }
    public byte[]? AttnetsRaw { get; init; }
    public byte[]? SyncnetsRaw { get; init; }
    public byte[]? Eth2Raw { get; init; }
    public bool IsAggregator { get; init; }
    public bool SignatureValid { get; init; }
}

internal static class EnrDecoder
{
    public static DecodedEnr Decode(string enrString)
    {
        if (!enrString.StartsWith("enr:", StringComparison.Ordinal))
            throw new FormatException("ENR string must start with 'enr:'");

        var body = enrString[4..];
        var rlpBytes = Base64UrlDecode(body);
        return DecodeRlp(rlpBytes);
    }

    public static DecodedEnr DecodeRlp(byte[] rlpBytes)
    {
        var reader = new RlpReader(rlpBytes);
        var listItems = reader.ReadList();

        // ENR RLP = [signature, seq, k1, v1, k2, v2, ...]
        if (listItems.Count < 2 || (listItems.Count - 2) % 2 != 0)
            throw new FormatException("Malformed ENR RLP layout");

        var signature = listItems[0];
        var seqBytes = listItems[1];
        var seq = ReadUnsignedInteger(seqBytes);

        var pairs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (var i = 2; i < listItems.Count; i += 2)
        {
            var key = Encoding.ASCII.GetString(listItems[i]);
            pairs[key] = listItems[i + 1];
        }

        string? scheme = pairs.TryGetValue("id", out var id) ? Encoding.ASCII.GetString(id) : null;
        pairs.TryGetValue("secp256k1", out var secpKey);

        IPAddress? ip4 = null, ip6 = null;
        if (pairs.TryGetValue("ip", out var ipBytes) && ipBytes.Length == 4)
            ip4 = new IPAddress(ipBytes);
        if (pairs.TryGetValue("ip6", out var ip6Bytes) && ip6Bytes.Length == 16)
            ip6 = new IPAddress(ip6Bytes);

        int? ReadPort(string key) => pairs.TryGetValue(key, out var b) ? (int)ReadUnsignedInteger(b) : null;
        var udp = ReadPort("udp");
        var tcp = ReadPort("tcp");
        var udp6 = ReadPort("udp6");
        var tcp6 = ReadPort("tcp6");
        var quic = ReadPort("quic");
        var quic6 = ReadPort("quic6");

        pairs.TryGetValue("attnets", out var attnets);
        pairs.TryGetValue("syncnets", out var syncnets);
        pairs.TryGetValue("eth2", out var eth2);

        var isAggregator = pairs.TryGetValue("is_aggregator", out var agg)
                           && agg.Length > 0 && agg[agg.Length - 1] != 0;

        byte[]? nodeId = null;
        var sigValid = false;
        if (scheme == "v4" && secpKey is { Length: 33 })
        {
            nodeId = ComputeNodeId(secpKey);

            // Signature covers keccak256(RLP([seq, k1, v1, ...])) — i.e. the whole ENR list
            // minus the leading signature element.
            var contentRlp = BuildContentRlp(seqBytes, listItems);
            sigValid = VerifySecp256k1Signature(secpKey, Keccak256(contentRlp), signature);
        }

        return new DecodedEnr
        {
            Rlp = rlpBytes,
            Seq = seq,
            IdentityScheme = scheme,
            Secp256k1PublicKeyCompressed = secpKey,
            NodeId = nodeId,
            Ip4 = ip4,
            Ip6 = ip6,
            UdpPort = udp,
            TcpPort = tcp,
            Udp6Port = udp6,
            Tcp6Port = tcp6,
            QuicPort = quic,
            Quic6Port = quic6,
            AttnetsRaw = attnets,
            SyncnetsRaw = syncnets,
            Eth2Raw = eth2,
            IsAggregator = isAggregator,
            SignatureValid = sigValid,
        };
    }

    public static string BuildMultiaddr(DecodedEnr enr)
    {
        // leanSpec always advertises UDP as QUIC transport — the UDP port carries QUIC-v1
        // regardless of whether the ENR names it via `quic` or falls back to `udp`.
        // Priority: IPv4 first, prefer explicit QUIC port over UDP.
        if (enr.Ip4 is not null && enr.QuicPort is not null)
            return $"/ip4/{enr.Ip4}/udp/{enr.QuicPort}/quic-v1";
        if (enr.Ip4 is not null && enr.UdpPort is not null)
            return $"/ip4/{enr.Ip4}/udp/{enr.UdpPort}/quic-v1";
        if (enr.Ip6 is not null && enr.Quic6Port is not null)
            return $"/ip6/{ExpandIpv6(enr.Ip6)}/udp/{enr.Quic6Port}/quic-v1";
        if (enr.Ip6 is not null && enr.Udp6Port is not null)
            return $"/ip6/{ExpandIpv6(enr.Ip6)}/udp/{enr.Udp6Port}/quic-v1";
        return string.Empty;
    }

    public static string ExpandIpv6(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        var groups = new string[8];
        for (var i = 0; i < 8; i++)
            groups[i] = ((bytes[i * 2] << 8) | bytes[i * 2 + 1]).ToString("x4");
        return string.Join(":", groups);
    }

    private static byte[] BuildContentRlp(byte[] seqBytes, List<byte[]> listItems)
    {
        // Rebuild the RLP of [seq, k1, v1, ...]: the same list minus the leading signature.
        var items = new List<byte[]> { seqBytes };
        for (var i = 2; i < listItems.Count; i++) items.Add(listItems[i]);

        var encoded = new List<byte>();
        foreach (var item in items)
            encoded.AddRange(RlpEncodeString(item));

        var payload = encoded.ToArray();
        return RlpEncodeListPrefix(payload.Length).Concat(payload).ToArray();
    }

    internal static byte[] RlpEncodeList(IEnumerable<byte[]> items)
    {
        var encodedItems = items.Select(RlpEncodeString).ToArray();
        var contentLength = encodedItems.Sum(b => b.Length);
        var buffer = new List<byte>(contentLength + 9);
        buffer.AddRange(RlpEncodeListPrefix(contentLength));
        foreach (var e in encodedItems) buffer.AddRange(e);
        return buffer.ToArray();
    }

    internal static byte[] RlpEncodeListOfLists(IEnumerable<byte[]> listEncodings)
    {
        var items = listEncodings.ToArray();
        var contentLength = items.Sum(b => b.Length);
        var buffer = new List<byte>(contentLength + 9);
        buffer.AddRange(RlpEncodeListPrefix(contentLength));
        foreach (var e in items) buffer.AddRange(e);
        return buffer.ToArray();
    }

    internal static byte[] RlpEncodeString(byte[] bytes)
    {
        if (bytes.Length == 1 && bytes[0] < 0x80) return new[] { bytes[0] };
        if (bytes.Length < 56)
            return new[] { (byte)(0x80 + bytes.Length) }.Concat(bytes).ToArray();

        var lenBytes = BigEndianBytes((ulong)bytes.Length);
        return new[] { (byte)(0xb7 + lenBytes.Length) }.Concat(lenBytes).Concat(bytes).ToArray();
    }

    internal static byte[] RlpEncodeListPrefix(int contentLength)
    {
        if (contentLength < 56)
            return new[] { (byte)(0xc0 + contentLength) };

        var lenBytes = BigEndianBytes((ulong)contentLength);
        return new[] { (byte)(0xf7 + lenBytes.Length) }.Concat(lenBytes).ToArray();
    }

    internal static byte[] BigEndianBytes(ulong value)
    {
        var bytes = new List<byte>();
        while (value != 0)
        {
            bytes.Insert(0, (byte)(value & 0xFF));
            value >>= 8;
        }
        return bytes.Count == 0 ? new byte[] { 0 } : bytes.ToArray();
    }

    /// <summary>
    /// Minimal big-endian representation used for RLP-encoded integers. Zero is encoded as
    /// empty bytes so that RLP emits 0x80 (empty string).
    /// </summary>
    internal static byte[] RlpIntBytes(ulong value)
    {
        if (value == 0) return Array.Empty<byte>();
        return BigEndianBytes(value);
    }

    /// <summary>
    /// Strip leading zero bytes from a byte string — discv5 treats requestId as a
    /// variable-length big-endian integer, so callers should canonicalise before RLP-encoding.
    /// </summary>
    internal static byte[] StripLeadingZeros(byte[] bytes)
    {
        var i = 0;
        while (i < bytes.Length && bytes[i] == 0) i++;
        return i == bytes.Length ? Array.Empty<byte>() : bytes.AsSpan(i).ToArray();
    }

    private static byte[] ComputeNodeId(byte[] compressedPubkey)
    {
        // Decompress secp256k1 point to 64-byte uncompressed x||y, then keccak256.
        var curve = SecNamedCurves.GetByName("secp256k1");
        var point = curve.Curve.DecodePoint(compressedPubkey);
        var x = point.AffineXCoord.ToBigInteger().ToByteArrayUnsigned();
        var y = point.AffineYCoord.ToBigInteger().ToByteArrayUnsigned();

        var xy = new byte[64];
        Array.Copy(x, 0, xy, 32 - x.Length, x.Length);
        Array.Copy(y, 0, xy, 64 - y.Length, y.Length);

        return Keccak256(xy);
    }

    private static bool VerifySecp256k1Signature(byte[] compressedPubkey, byte[] messageHash, byte[] signature)
    {
        if (signature.Length != 64) return false;

        try
        {
            var curve = SecNamedCurves.GetByName("secp256k1");
            var point = curve.Curve.DecodePoint(compressedPubkey);
            var domain = new ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H);
            var pubKeyParams = new ECPublicKeyParameters(point, domain);

            var r = new BigInteger(1, signature.AsSpan(0, 32).ToArray());
            var s = new BigInteger(1, signature.AsSpan(32, 32).ToArray());

            var signer = new ECDsaSigner();
            signer.Init(false, pubKeyParams);
            return signer.VerifySignature(messageHash, r, s);
        }
        catch
        {
            return false;
        }
    }

    public static byte[] Keccak256(byte[] input)
    {
        var digest = new KeccakDigest(256);
        digest.BlockUpdate(input, 0, input.Length);
        var output = new byte[32];
        digest.DoFinal(output, 0);
        return output;
    }

    private static ulong ReadUnsignedInteger(byte[] bytes)
    {
        if (bytes.Length == 0) return 0;
        ulong value = 0;
        foreach (var b in bytes) value = (value << 8) | b;
        return value;
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        var pad = 4 - (s.Length % 4);
        if (pad > 0 && pad < 4) s += new string('=', pad);
        return Convert.FromBase64String(s);
    }

    private sealed class RlpReader
    {
        private readonly byte[] _bytes;
        private int _position;

        public RlpReader(byte[] bytes) => _bytes = bytes;

        public List<byte[]> ReadList()
        {
            var prefix = _bytes[_position++];
            int contentLength;
            if (prefix >= 0xc0 && prefix <= 0xf7)
            {
                contentLength = prefix - 0xc0;
            }
            else if (prefix >= 0xf8)
            {
                var lengthOfLength = prefix - 0xf7;
                contentLength = 0;
                for (var i = 0; i < lengthOfLength; i++)
                    contentLength = (contentLength << 8) | _bytes[_position++];
            }
            else
            {
                throw new FormatException($"Expected RLP list, got byte 0x{prefix:x2}");
            }

            var end = _position + contentLength;
            var items = new List<byte[]>();
            while (_position < end)
                items.Add(ReadString());
            if (_position != end) throw new FormatException("RLP list length overrun");
            return items;
        }

        private byte[] ReadString()
        {
            var prefix = _bytes[_position];
            if (prefix < 0x80)
            {
                _position++;
                return new[] { prefix };
            }
            if (prefix <= 0xb7)
            {
                _position++;
                var len = prefix - 0x80;
                var bytes = _bytes.AsSpan(_position, len).ToArray();
                _position += len;
                return bytes;
            }
            if (prefix <= 0xbf)
            {
                _position++;
                var lengthOfLength = prefix - 0xb7;
                var len = 0;
                for (var i = 0; i < lengthOfLength; i++)
                    len = (len << 8) | _bytes[_position++];
                var bytes = _bytes.AsSpan(_position, len).ToArray();
                _position += len;
                return bytes;
            }
            throw new FormatException($"Unexpected RLP string prefix 0x{prefix:x2}");
        }
    }
}
