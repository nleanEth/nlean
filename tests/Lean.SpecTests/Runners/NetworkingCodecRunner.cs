using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lean.Network;
using Lean.SpecTests.Types;
using NUnit.Framework;
using Snappier;

namespace Lean.SpecTests.Runners;

public sealed class NetworkingCodecRunner : ISpecTestRunner
{
    public void Run(string testId, string testJson)
    {
        var test = JsonSerializer.Deserialize<NetworkingCodecTest>(testJson)
            ?? throw new InvalidOperationException($"Failed to deserialize: {testId}");

        switch (test.CodecName)
        {
            case "varint":
                RunVarint(test);
                break;
            case "gossip_topic":
                RunGossipTopic(test);
                break;
            case "gossip_message_id":
                RunGossipMessageId(test);
                break;
            case "snappy_block":
                RunSnappyBlock(test);
                break;
            case "snappy_frame":
                RunSnappyFrame(test);
                break;
            case "xor_distance":
                RunXorDistance(test);
                break;
            case "log2_distance":
                RunLog2Distance(test);
                break;
            case "peer_id":
                RunPeerId(test);
                break;
            case "enr":
                RunEnr(test);
                break;
            case "discv5_message":
                RunDiscv5Message(test);
                break;
            case "gossipsub_rpc":
                RunGossipsubRpc(test);
                break;
            case "reqresp_request":
                RunReqRespRequest(test);
                break;
            case "reqresp_response":
                RunReqRespResponse(test);
                break;
            default:
                Assert.Inconclusive(
                    $"codec '{test.CodecName}' not implemented yet — covered codecs: " +
                    "varint, gossip_topic, gossip_message_id, snappy_block, snappy_frame, " +
                    "xor_distance, log2_distance, peer_id, enr, discv5_message, gossipsub_rpc, " +
                    "reqresp_request, reqresp_response");
                break;
        }
    }

    // libp2p reqresp request: varint(payload_len) || snappy-frame(payload).
    // Snappy encoders are non-deterministic across impls, so verify decode direction:
    // parse the fixture's encoded bytes, extract the length prefix, decode snappy,
    // and confirm the result matches sszData. Then round-trip nlean's own encoding.
    private static void RunReqRespRequest(NetworkingCodecTest test)
    {
        var sszData = ParseHex(test.Input.GetProperty("sszData").GetString()!);
        var encoded = ParseHex(test.Output.GetProperty("encoded").GetString()!);

        var (length, payloadOffset) = ReadVarint(encoded, 0);
        Assert.That(length, Is.EqualTo((ulong)sszData.Length),
            "reqresp request: varint length prefix mismatch");

        var framed = encoded.AsSpan(payloadOffset).ToArray();
        var decoded = ReadSnappyFrame(framed);
        Assert.That(Convert.ToHexString(decoded), Is.EqualTo(Convert.ToHexString(sszData)),
            "reqresp request: snappy decode mismatch");

        // Round-trip nlean's own encoding to catch compressor regressions.
        var roundTrip = EncodeReqRespRequest(sszData);
        var (rtLen, rtOffset) = ReadVarint(roundTrip, 0);
        Assert.That(rtLen, Is.EqualTo((ulong)sszData.Length),
            "reqresp request round-trip: length prefix mismatch");
        var rtDecoded = ReadSnappyFrame(roundTrip.AsSpan(rtOffset).ToArray());
        Assert.That(Convert.ToHexString(rtDecoded), Is.EqualTo(Convert.ToHexString(sszData)),
            "reqresp request round-trip: snappy decode mismatch");
    }

    // libp2p reqresp response: response_code || varint(payload_len) || snappy-frame(payload).
    private static void RunReqRespResponse(NetworkingCodecTest test)
    {
        var sszData = ParseHex(test.Input.GetProperty("sszData").GetString()!);
        var responseCode = (byte)test.Input.GetProperty("responseCode").GetInt32();
        var encoded = ParseHex(test.Output.GetProperty("encoded").GetString()!);

        Assert.That(encoded[0], Is.EqualTo(responseCode), "reqresp response: responseCode mismatch");

        var (length, payloadOffset) = ReadVarint(encoded, 1);
        Assert.That(length, Is.EqualTo((ulong)sszData.Length),
            "reqresp response: varint length prefix mismatch");

        var framed = encoded.AsSpan(payloadOffset).ToArray();
        var decoded = ReadSnappyFrame(framed);
        Assert.That(Convert.ToHexString(decoded), Is.EqualTo(Convert.ToHexString(sszData)),
            "reqresp response: snappy decode mismatch");

        var roundTrip = EncodeReqRespResponse(responseCode, sszData);
        Assert.That(roundTrip[0], Is.EqualTo(responseCode),
            "reqresp response round-trip: responseCode mismatch");
        var (rtLen, rtOffset) = ReadVarint(roundTrip, 1);
        Assert.That(rtLen, Is.EqualTo((ulong)sszData.Length),
            "reqresp response round-trip: length prefix mismatch");
        var rtDecoded = ReadSnappyFrame(roundTrip.AsSpan(rtOffset).ToArray());
        Assert.That(Convert.ToHexString(rtDecoded), Is.EqualTo(Convert.ToHexString(sszData)),
            "reqresp response round-trip: snappy decode mismatch");
    }

    private static byte[] EncodeReqRespRequest(byte[] payload)
    {
        var buf = new List<byte>();
        WriteProtoVarint(buf, (ulong)payload.Length);
        buf.AddRange(SnappyFrameEncode(payload));
        return buf.ToArray();
    }

    private static byte[] EncodeReqRespResponse(byte responseCode, byte[] payload)
    {
        var buf = new List<byte> { responseCode };
        WriteProtoVarint(buf, (ulong)payload.Length);
        buf.AddRange(SnappyFrameEncode(payload));
        return buf.ToArray();
    }

    private static byte[] SnappyFrameEncode(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var stream = new SnappyStream(ms, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
        {
            stream.Write(data);
        }
        return ms.ToArray();
    }

    private static (ulong Value, int NextOffset) ReadVarint(byte[] bytes, int offset)
    {
        ulong value = 0;
        var shift = 0;
        while (true)
        {
            var b = bytes[offset++];
            value |= ((ulong)(b & 0x7F)) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return (value, offset);
    }

    // libp2p gossipsub RPC protobuf encoder.
    private static void RunGossipsubRpc(NetworkingCodecTest test)
    {
        var buf = new List<byte>();

        if (test.Input.TryGetProperty("subscriptions", out var subs))
        {
            foreach (var sub in subs.EnumerateArray())
            {
                var subBuf = new List<byte>();
                WriteProtoVarintField(subBuf, 1, sub.GetProperty("subscribe").GetBoolean() ? 1UL : 0UL);
                WriteProtoStringField(subBuf, 2, sub.GetProperty("topicId").GetString()!, skipIfEmpty: false);
                WriteProtoBytesField(buf, 1, subBuf.ToArray());
            }
        }

        if (test.Input.TryGetProperty("publish", out var pubs))
        {
            foreach (var msg in pubs.EnumerateArray())
            {
                var msgBuf = new List<byte>();
                if (msg.TryGetProperty("fromPeer", out var fromPeer))
                    WriteProtoBytesField(msgBuf, 1, ParseHex(fromPeer.GetString()!));
                if (msg.TryGetProperty("data", out var data))
                    WriteProtoBytesField(msgBuf, 2, ParseHex(data.GetString()!));
                if (msg.TryGetProperty("seqno", out var seqno))
                    WriteProtoBytesField(msgBuf, 3, ParseHex(seqno.GetString()!));
                if (msg.TryGetProperty("topic", out var topic))
                    WriteProtoStringField(msgBuf, 4, topic.GetString()!);
                if (msg.TryGetProperty("signature", out var sig))
                    WriteProtoBytesField(msgBuf, 5, ParseHex(sig.GetString()!));
                if (msg.TryGetProperty("key", out var key))
                    WriteProtoBytesField(msgBuf, 6, ParseHex(key.GetString()!));
                WriteProtoBytesField(buf, 2, msgBuf.ToArray());
            }
        }

        if (test.Input.TryGetProperty("control", out var ctrl) && ctrl.ValueKind == JsonValueKind.Object)
        {
            var ctrlBuf = new List<byte>();

            if (ctrl.TryGetProperty("ihave", out var ihaves))
            {
                foreach (var ihave in ihaves.EnumerateArray())
                {
                    var ihaveBuf = new List<byte>();
                    if (ihave.TryGetProperty("topicId", out var t))
                        WriteProtoStringField(ihaveBuf, 1, t.GetString()!);
                    foreach (var mid in ihave.GetProperty("messageIds").EnumerateArray())
                        WriteProtoBytesField(ihaveBuf, 2, ParseHex(mid.GetString()!));
                    WriteProtoBytesField(ctrlBuf, 1, ihaveBuf.ToArray());
                }
            }

            if (ctrl.TryGetProperty("iwant", out var iwants))
            {
                foreach (var iwant in iwants.EnumerateArray())
                {
                    var iwantBuf = new List<byte>();
                    foreach (var mid in iwant.GetProperty("messageIds").EnumerateArray())
                        WriteProtoBytesField(iwantBuf, 1, ParseHex(mid.GetString()!));
                    WriteProtoBytesField(ctrlBuf, 2, iwantBuf.ToArray());
                }
            }

            if (ctrl.TryGetProperty("graft", out var grafts))
            {
                foreach (var graft in grafts.EnumerateArray())
                {
                    var graftBuf = new List<byte>();
                    WriteProtoStringField(graftBuf, 1, graft.GetProperty("topicId").GetString()!);
                    WriteProtoBytesField(ctrlBuf, 3, graftBuf.ToArray());
                }
            }

            if (ctrl.TryGetProperty("prune", out var prunes))
            {
                foreach (var prune in prunes.EnumerateArray())
                {
                    var pruneBuf = new List<byte>();
                    WriteProtoStringField(pruneBuf, 1, prune.GetProperty("topicId").GetString()!);
                    if (prune.TryGetProperty("peers", out var peers))
                    {
                        foreach (var peer in peers.EnumerateArray())
                        {
                            var peerBuf = new List<byte>();
                            if (peer.TryGetProperty("peerId", out var pid))
                                WriteProtoBytesField(peerBuf, 1, ParseHex(pid.GetString()!));
                            if (peer.TryGetProperty("signedPeerRecord", out var spr))
                                WriteProtoBytesField(peerBuf, 2, ParseHex(spr.GetString()!));
                            WriteProtoBytesField(pruneBuf, 2, peerBuf.ToArray());
                        }
                    }
                    if (prune.TryGetProperty("backoff", out var backoff))
                        WriteProtoVarintField(pruneBuf, 3, backoff.GetUInt64());
                    WriteProtoBytesField(ctrlBuf, 4, pruneBuf.ToArray());
                }
            }

            if (ctrl.TryGetProperty("idontwant", out var idws))
            {
                foreach (var idw in idws.EnumerateArray())
                {
                    var idwBuf = new List<byte>();
                    foreach (var mid in idw.GetProperty("messageIds").EnumerateArray())
                        WriteProtoBytesField(idwBuf, 1, ParseHex(mid.GetString()!));
                    WriteProtoBytesField(ctrlBuf, 5, idwBuf.ToArray());
                }
            }

            if (ctrlBuf.Count > 0)
                WriteProtoBytesField(buf, 3, ctrlBuf.ToArray());
        }

        var expected = ParseHex(test.Output.GetProperty("encoded").GetString()!);
        Assert.That(Convert.ToHexString(buf.ToArray()), Is.EqualTo(Convert.ToHexString(expected)),
            "gossipsub_rpc encoding mismatch");
    }

    private static void WriteProtoVarintField(List<byte> dst, int fieldNumber, ulong value)
    {
        WriteProtoVarint(dst, (ulong)((fieldNumber << 3) | 0));
        WriteProtoVarint(dst, value);
    }

    private static void WriteProtoBytesField(List<byte> dst, int fieldNumber, byte[] bytes)
    {
        WriteProtoVarint(dst, (ulong)((fieldNumber << 3) | 2));
        WriteProtoVarint(dst, (ulong)bytes.Length);
        dst.AddRange(bytes);
    }

    private static void WriteProtoStringField(List<byte> dst, int fieldNumber, string value, bool skipIfEmpty = true)
    {
        // proto3 default rule: empty string is the wire-format default and is not emitted.
        // libp2p pubsub's SubOpts uses proto2 with `optional` fields, where explicitly-set
        // empties ARE emitted — callers that need that behaviour pass skipIfEmpty=false.
        if (skipIfEmpty && string.IsNullOrEmpty(value)) return;
        WriteProtoBytesField(dst, fieldNumber, Encoding.UTF8.GetBytes(value));
    }

    private static void RunDiscv5Message(NetworkingCodecTest test)
    {
        // discv5 message wire format: 1-byte type || RLP(args).
        // Integer-valued fields (requestId, enrSeq, port, distance, total) use minimal
        // big-endian representation with leading zeros stripped — zero becomes 0x80.
        var type = test.Input.GetProperty("type").GetString()!;
        var requestId = EnrDecoder.StripLeadingZeros(
            ParseHex(test.Input.GetProperty("requestId").GetString()!));

        byte typeByte;
        byte[] rlpPayload;
        switch (type)
        {
            case "ping":
                typeByte = 0x01;
                rlpPayload = EnrDecoder.RlpEncodeList(new[]
                {
                    requestId,
                    EnrDecoder.RlpIntBytes(test.Input.GetProperty("enrSeq").GetUInt64()),
                });
                break;
            case "pong":
                typeByte = 0x02;
                rlpPayload = EnrDecoder.RlpEncodeList(new[]
                {
                    requestId,
                    EnrDecoder.RlpIntBytes(test.Input.GetProperty("enrSeq").GetUInt64()),
                    ParseHex(test.Input.GetProperty("recipientIp").GetString()!),
                    EnrDecoder.RlpIntBytes(test.Input.GetProperty("recipientPort").GetUInt64()),
                });
                break;
            case "findnode":
            {
                typeByte = 0x03;
                var distances = test.Input.GetProperty("distances");
                var distanceItems = distances.EnumerateArray()
                    .Select(d => EnrDecoder.RlpIntBytes(d.GetUInt64()))
                    .ToList();
                var distancesList = EnrDecoder.RlpEncodeList(distanceItems);
                rlpPayload = EnrDecoder.RlpEncodeListOfLists(new[]
                {
                    EnrDecoder.RlpEncodeString(requestId),
                    distancesList,
                });
                break;
            }
            case "nodes":
            {
                typeByte = 0x04;
                var enrs = test.Input.GetProperty("enrs");
                var enrItems = enrs.EnumerateArray()
                    .Select(e => ParseHex(e.GetString()!))
                    .ToList();
                var enrsList = EnrDecoder.RlpEncodeList(enrItems);

                rlpPayload = EnrDecoder.RlpEncodeListOfLists(new[]
                {
                    EnrDecoder.RlpEncodeString(requestId),
                    EnrDecoder.RlpEncodeString(EnrDecoder.RlpIntBytes(test.Input.GetProperty("total").GetUInt64())),
                    enrsList,
                });
                break;
            }
            case "talkreq":
                typeByte = 0x05;
                rlpPayload = EnrDecoder.RlpEncodeList(new[]
                {
                    requestId,
                    ParseHex(test.Input.GetProperty("protocol").GetString()!),
                    ParseHex(test.Input.GetProperty("request").GetString()!),
                });
                break;
            case "talkresp":
                typeByte = 0x06;
                rlpPayload = EnrDecoder.RlpEncodeList(new[]
                {
                    requestId,
                    ParseHex(test.Input.GetProperty("response").GetString()!),
                });
                break;
            default:
                throw new InvalidOperationException($"Unknown discv5 message type: {type}");
        }

        var expected = ParseHex(test.Output.GetProperty("encoded").GetString()!);
        var actual = new byte[] { typeByte }.Concat(rlpPayload).ToArray();

        Assert.That(Convert.ToHexString(actual), Is.EqualTo(Convert.ToHexString(expected)),
            $"discv5 {type} encoding mismatch");
    }

    private static void RunEnr(NetworkingCodecTest test)
    {
        var enrString = test.Input.GetProperty("enrString").GetString()!;
        var decoded = EnrDecoder.Decode(enrString);

        var expectedRlp = ParseHex(test.Output.GetProperty("rlp").GetString()!);
        Assert.That(Convert.ToHexString(decoded.Rlp), Is.EqualTo(Convert.ToHexString(expectedRlp)),
            "enr rlp mismatch");

        if (test.Output.TryGetProperty("seq", out var seq))
            Assert.That(decoded.Seq, Is.EqualTo(seq.GetUInt64()), "enr seq mismatch");

        if (test.Output.TryGetProperty("identityScheme", out var scheme))
            Assert.That(decoded.IdentityScheme, Is.EqualTo(scheme.GetString()), "identity scheme mismatch");

        if (test.Output.TryGetProperty("publicKey", out var pk))
        {
            var actual = decoded.Secp256k1PublicKeyCompressed is null
                ? string.Empty
                : "0x" + Convert.ToHexString(decoded.Secp256k1PublicKeyCompressed).ToLowerInvariant();
            Assert.That(actual, Is.EqualTo(pk.GetString()), "enr publicKey mismatch");
        }

        if (test.Output.TryGetProperty("nodeId", out var nid))
        {
            var actual = decoded.NodeId is null
                ? string.Empty
                : "0x" + Convert.ToHexString(decoded.NodeId).ToLowerInvariant();
            Assert.That(actual, Is.EqualTo(nid.GetString()), "enr nodeId mismatch");
        }

        if (test.Output.TryGetProperty("ip4", out var ip4))
            Assert.That(decoded.Ip4?.ToString(), Is.EqualTo(ip4.GetString()), "ip4 mismatch");
        if (test.Output.TryGetProperty("ip6", out var ip6))
        {
            var actual = decoded.Ip6 is null ? null : EnrDecoder.ExpandIpv6(decoded.Ip6);
            Assert.That(actual, Is.EqualTo(ip6.GetString()), "ip6 mismatch");
        }
        if (test.Output.TryGetProperty("udpPort", out var udp))
            Assert.That(decoded.UdpPort, Is.EqualTo(udp.GetInt32()), "udpPort mismatch");
        if (test.Output.TryGetProperty("tcpPort", out var tcp))
            Assert.That(decoded.TcpPort, Is.EqualTo(tcp.GetInt32()), "tcpPort mismatch");
        if (test.Output.TryGetProperty("udp6Port", out var udp6))
            Assert.That(decoded.Udp6Port, Is.EqualTo(udp6.GetInt32()), "udp6Port mismatch");
        if (test.Output.TryGetProperty("quicPort", out var quic))
            Assert.That(decoded.QuicPort, Is.EqualTo(quic.GetInt32()), "quicPort mismatch");
        if (test.Output.TryGetProperty("quic6Port", out var quic6))
            Assert.That(decoded.Quic6Port, Is.EqualTo(quic6.GetInt32()), "quic6Port mismatch");

        if (test.Output.TryGetProperty("multiaddr", out var ma))
            Assert.That(EnrDecoder.BuildMultiaddr(decoded), Is.EqualTo(ma.GetString()), "multiaddr mismatch");

        if (test.Output.TryGetProperty("isAggregator", out var agg))
            Assert.That(decoded.IsAggregator, Is.EqualTo(agg.GetBoolean()), "isAggregator mismatch");

        if (test.Output.TryGetProperty("signatureValid", out var sv))
            Assert.That(decoded.SignatureValid, Is.EqualTo(sv.GetBoolean()), "signatureValid mismatch");

        if (test.Output.TryGetProperty("attestationSubnets", out var subnets))
            AssertSubnetIds(decoded.AttnetsRaw, subnets, 64, "attestationSubnets");
        if (test.Output.TryGetProperty("syncCommitteeSubnets", out var syncSubnets))
            AssertSubnetIds(decoded.SyncnetsRaw, syncSubnets, 4, "syncCommitteeSubnets");
        if (test.Output.TryGetProperty("eth2Data", out var eth2))
            AssertEth2Data(decoded.Eth2Raw, eth2);
    }

    private static void AssertSubnetIds(byte[]? raw, JsonElement expected, int bitCount, string label)
    {
        if (raw is null)
        {
            Assert.That(expected.GetArrayLength(), Is.EqualTo(0), $"{label}: expected no subnets but fixture has ids");
            return;
        }

        var ids = new List<int>();
        for (var i = 0; i < bitCount; i++)
        {
            var byteIdx = i / 8;
            var bit = i % 8;
            if (byteIdx < raw.Length && (raw[byteIdx] & (1 << bit)) != 0)
                ids.Add(i);
        }

        var expectedIds = new List<int>();
        foreach (var item in expected.EnumerateArray()) expectedIds.Add(item.GetInt32());

        Assert.That(ids, Is.EqualTo(expectedIds), $"{label} subnet ids mismatch");
    }

    private static void AssertEth2Data(byte[]? raw, JsonElement expected)
    {
        if (raw is null) Assert.Fail("eth2Data: decoded bytes missing");

        // Eth2Data SSZ Container: fork_digest (Bytes4) + next_fork_version (Bytes4) + next_fork_epoch (uint64)
        if (raw!.Length != 16) Assert.Fail($"eth2Data: expected 16 bytes, got {raw.Length}");

        var forkDigest = "0x" + Convert.ToHexString(raw.AsSpan(0, 4)).ToLowerInvariant();
        var nextForkVersion = "0x" + Convert.ToHexString(raw.AsSpan(4, 4)).ToLowerInvariant();
        ulong nextForkEpoch = 0;
        for (var i = 0; i < 8; i++) nextForkEpoch |= (ulong)raw[8 + i] << (8 * i);

        Assert.That(forkDigest, Is.EqualTo(expected.GetProperty("forkDigest").GetString()),
            "eth2Data.forkDigest mismatch");
        Assert.That(nextForkVersion, Is.EqualTo(expected.GetProperty("nextForkVersion").GetString()),
            "eth2Data.nextForkVersion mismatch");
        Assert.That(nextForkEpoch, Is.EqualTo(expected.GetProperty("nextForkEpoch").GetUInt64()),
            "eth2Data.nextForkEpoch mismatch");
    }

    private static void RunPeerId(NetworkingCodecTest test)
    {
        var keyType = test.Input.GetProperty("keyType").GetString()!;
        var publicKey = ParseHex(test.Input.GetProperty("publicKey").GetString()!);

        // libp2p PublicKey protobuf: { Type=1 (varint), Data=2 (bytes) }.
        var typeCode = keyType switch
        {
            "rsa" => 0,
            "ed25519" => 1,
            "secp256k1" => 2,
            "ecdsa" => 3,
            _ => throw new InvalidOperationException($"unknown libp2p key type: {keyType}"),
        };

        var protobuf = new List<byte> { 0x08, (byte)typeCode, 0x12 };
        WriteProtoVarint(protobuf, (ulong)publicKey.Length);
        protobuf.AddRange(publicKey);
        var encoded = protobuf.ToArray();

        var expectedEncoded = ParseHex(test.Output.GetProperty("protobufEncoded").GetString()!);
        Assert.That(Convert.ToHexString(encoded), Is.EqualTo(Convert.ToHexString(expectedEncoded)),
            "peer_id protobuf encoding mismatch");

        // Multihash rule: identity (0x00) if encoded ≤ 42 bytes, else sha256 (0x12 0x20 …).
        byte[] multihash;
        if (encoded.Length <= 42)
        {
            multihash = new byte[] { 0x00, (byte)encoded.Length }
                .Concat(encoded).ToArray();
        }
        else
        {
            multihash = new byte[] { 0x12, 0x20 }
                .Concat(SHA256.HashData(encoded)).ToArray();
        }

        var peerId = Base58BtcEncode(multihash);
        var expectedPeerId = test.Output.GetProperty("peerId").GetString()!;
        Assert.That(peerId, Is.EqualTo(expectedPeerId), "peer_id base58btc mismatch");
    }

    private static void WriteProtoVarint(List<byte> dst, ulong value)
    {
        while (value >= 0x80)
        {
            dst.Add((byte)(value | 0x80));
            value >>= 7;
        }
        dst.Add((byte)value);
    }

    private const string Base58Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    private static string Base58BtcEncode(byte[] bytes)
    {
        if (bytes.Length == 0) return string.Empty;

        // Count leading zero bytes — these become leading '1' characters.
        var zeros = 0;
        while (zeros < bytes.Length && bytes[zeros] == 0) zeros++;

        // Convert to a big-endian base-58 representation via repeated division.
        var buffer = (byte[])bytes.Clone();
        var result = new List<byte>();
        var start = zeros;
        while (start < buffer.Length)
        {
            var remainder = 0;
            for (var i = start; i < buffer.Length; i++)
            {
                var acc = (remainder << 8) + buffer[i];
                buffer[i] = (byte)(acc / 58);
                remainder = acc % 58;
            }
            if (buffer[start] == 0) start++;
            result.Add((byte)remainder);
        }

        var chars = new char[zeros + result.Count];
        for (var i = 0; i < zeros; i++) chars[i] = Base58Alphabet[0];
        for (var i = 0; i < result.Count; i++)
            chars[zeros + i] = Base58Alphabet[result[result.Count - 1 - i]];
        return new string(chars);
    }

    private static void RunVarint(NetworkingCodecTest test)
    {
        var value = ReadUInt(test.Input.GetProperty("value"));
        var expected = ParseHex(test.Output.GetProperty("encoded").GetString()!);

        // LEB128 / unsigned-varint (libp2p varint): 7 bits per byte, MSB set
        // means "more bytes follow".
        var buf = new List<byte>();
        var v = value;
        while (v >= 0x80)
        {
            buf.Add((byte)(v | 0x80));
            v >>= 7;
        }
        buf.Add((byte)v);
        var actual = buf.ToArray();

        Assert.That(Convert.ToHexString(actual), Is.EqualTo(Convert.ToHexString(expected)),
            "varint encoding mismatch");

        if (test.Output.TryGetProperty("byteLength", out var len))
        {
            Assert.That(actual.Length, Is.EqualTo(len.GetInt32()),
                "varint byteLength mismatch");
        }
    }

    private static void RunGossipTopic(NetworkingCodecTest test)
    {
        var kind = test.Input.GetProperty("kind").GetString()!;
        var forkDigest = test.Input.GetProperty("forkDigest").GetString()!;
        var expected = test.Output.GetProperty("topicString").GetString()!;

        var actual = kind switch
        {
            "block" => GossipTopics.Block(forkDigest),
            "aggregation" => GossipTopics.Aggregate(forkDigest),
            "attestation" => GossipTopics.AttestationSubnet(forkDigest, test.Input.GetProperty("subnetId").GetInt32()),
            _ => throw new InvalidOperationException($"unknown gossip topic kind: {kind}"),
        };

        Assert.That(actual, Is.EqualTo(expected), "gossip topic string mismatch");
    }

    private static void RunGossipMessageId(NetworkingCodecTest test)
    {
        var topicBytes = ParseHex(test.Input.GetProperty("topic").GetString()!);
        var data = ParseHex(test.Input.GetProperty("data").GetString()!);
        var domain = ParseHex(test.Input.GetProperty("domain").GetString()!);
        var expected = ParseHex(test.Output.GetProperty("messageId").GetString()!);

        // Replicate LeanPubsubMessageId.Compute, but with explicit domain.
        // The spec captures domain as an input (valid-snappy vs invalid-snappy)
        // while nlean's production path auto-detects it from whether the data
        // successfully snappy-decompresses. For the codec test we honour the
        // fixture's explicit domain choice; the auto-detect behaviour is
        // validated separately by Lean.Network.Tests.
        if (domain.Length != 4)
            throw new InvalidOperationException("expected 4-byte domain");

        var topic = Encoding.ASCII.GetString(topicBytes);
        var topicByteCount = Encoding.ASCII.GetByteCount(topic);
        var total = domain.Length + sizeof(ulong) + topicByteCount + data.Length;
        var input = new byte[total];
        var offset = 0;
        domain.CopyTo(input, offset);
        offset += domain.Length;
        BitConverter.GetBytes((ulong)topicByteCount).CopyTo(input, offset);
        offset += sizeof(ulong);
        Encoding.ASCII.GetBytes(topic, 0, topic.Length, input, offset);
        offset += topicByteCount;
        data.CopyTo(input, offset);

        var actual = SHA256.HashData(input)[..20];

        Assert.That(Convert.ToHexString(actual), Is.EqualTo(Convert.ToHexString(expected)),
            "gossip message id mismatch");
    }

    private static void RunSnappyBlock(NetworkingCodecTest test)
    {
        // Snappy compression is not bit-for-bit deterministic across
        // implementations: different encoders can emit different (but equally
        // valid) byte streams for the same input. We therefore test the
        // decompression direction — take the fixture's compressed payload and
        // confirm nlean's Snappier decodes it back to the original data.
        // Round-tripping nlean's own output is also checked so regressions in
        // the compressor are still caught.
        var data = ParseHex(test.Input.GetProperty("data").GetString()!);
        var compressed = ParseHex(test.Output.GetProperty("compressed").GetString()!);

        var decoded = Snappy.DecompressToArray(compressed);
        Assert.That(Convert.ToHexString(decoded), Is.EqualTo(Convert.ToHexString(data)),
            "snappy block decompression mismatch");

        var roundTrip = Snappy.DecompressToArray(Snappy.CompressToArray(data));
        Assert.That(Convert.ToHexString(roundTrip), Is.EqualTo(Convert.ToHexString(data)),
            "snappy block round-trip mismatch");

        if (test.Output.TryGetProperty("uncompressedLength", out var ul))
        {
            Assert.That(data.Length, Is.EqualTo(ul.GetInt32()),
                "uncompressedLength mismatch");
        }
    }

    private static void RunSnappyFrame(NetworkingCodecTest test)
    {
        // Same rationale as RunSnappyBlock: frame-level compression output
        // depends on encoder chunking. Validate decompression path against
        // the fixture's framed bytes, then round-trip nlean's output.
        var data = ParseHex(test.Input.GetProperty("data").GetString()!);
        var framed = ParseHex(test.Output.GetProperty("framed").GetString()!);

        var decoded = ReadSnappyFrame(framed);
        Assert.That(Convert.ToHexString(decoded), Is.EqualTo(Convert.ToHexString(data)),
            "snappy frame decompression mismatch");

        using var ms = new MemoryStream();
        using (var stream = new SnappyStream(ms, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
        {
            stream.Write(data);
        }
        var roundTrip = ReadSnappyFrame(ms.ToArray());
        Assert.That(Convert.ToHexString(roundTrip), Is.EqualTo(Convert.ToHexString(data)),
            "snappy frame round-trip mismatch");
    }

    private static void RunXorDistance(NetworkingCodecTest test)
    {
        var a = ParseHex(test.Input.GetProperty("nodeA").GetString()!);
        var b = ParseHex(test.Input.GetProperty("nodeB").GetString()!);

        if (a.Length != b.Length)
            throw new InvalidOperationException("xor_distance operands must be same length");

        var actual = new byte[a.Length];
        for (var i = 0; i < a.Length; i++) actual[i] = (byte)(a[i] ^ b[i]);

        // The fixture serialises `distance` as compact big-endian hex (leading
        // zero bytes trimmed) and may emit odd-length strings like "0x3" for
        // single-nibble values, so compare after normalising both sides to
        // shortest-big-endian form.
        var expected = StripLeadingZeroBytes(ParseFlexHex(test.Output.GetProperty("distance").GetString()!));
        var actualNormalised = StripLeadingZeroBytes(actual);

        Assert.That(Convert.ToHexString(actualNormalised), Is.EqualTo(Convert.ToHexString(expected)),
            "xor_distance mismatch");
    }

    private static byte[] ParseFlexHex(string hex)
    {
        var s = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
        if ((s.Length & 1) == 1) s = "0" + s;
        return s.Length == 0 ? Array.Empty<byte>() : Convert.FromHexString(s);
    }

    private static byte[] StripLeadingZeroBytes(byte[] bytes)
    {
        var i = 0;
        while (i < bytes.Length - 1 && bytes[i] == 0) i++;
        return bytes[i..];
    }

    private static void RunLog2Distance(NetworkingCodecTest test)
    {
        var a = ParseHex(test.Input.GetProperty("nodeA").GetString()!);
        var b = ParseHex(test.Input.GetProperty("nodeB").GetString()!);
        var expected = test.Output.GetProperty("distance").GetInt32();

        if (a.Length != b.Length)
            throw new InvalidOperationException("log2_distance operands must be same length");

        // log2_distance is the position of the most significant differing bit,
        // treating a and b as big-endian bit strings. 0 means identical; for
        // nodes differing only in the lowest bit, distance = 1.
        var actual = 0;
        for (var byteIdx = 0; byteIdx < a.Length; byteIdx++)
        {
            var diff = a[byteIdx] ^ b[byteIdx];
            if (diff == 0) continue;
            // Find position of the highest set bit in `diff`.
            var bitsRemaining = (a.Length - byteIdx) * 8;
            for (var bit = 7; bit >= 0; bit--)
            {
                if ((diff & (1 << bit)) != 0)
                {
                    actual = bitsRemaining - (7 - bit);
                    break;
                }
            }
            break;
        }

        Assert.That(actual, Is.EqualTo(expected), "log2_distance mismatch");
    }

    private static byte[] ReadSnappyFrame(byte[] framed)
    {
        using var ms = new MemoryStream(framed);
        using var stream = new SnappyStream(ms, System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static ulong ReadUInt(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetUInt64(),
            JsonValueKind.String => ulong.Parse(value.GetString()!),
            _ => throw new InvalidOperationException($"cannot read uint from {value.ValueKind}"),
        };
    }

    private static byte[] ParseHex(string hex) =>
        Convert.FromHexString(hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex);

    private sealed record NetworkingCodecTest(
        [property: JsonPropertyName("network")] string Network,
        [property: JsonPropertyName("leanEnv")] string LeanEnv,
        [property: JsonPropertyName("codecName")] string CodecName,
        [property: JsonPropertyName("input")] JsonElement Input,
        [property: JsonPropertyName("output")] JsonElement Output,
        [property: JsonPropertyName("_info")] TestInfo? Info);
}
