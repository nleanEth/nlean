using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using Nethermind.Libp2p.Core;
using Snappier;

namespace Lean.Network;

public static class LeanReqRespCodec
{
    public const int RootLength = 32;
    public const int MaxBlocksByRootRequestRoots = 1024;

    /// <summary>Maximum uncompressed payload size (10 MiB), per consensus spec.</summary>
    public const int MaxPayloadSize = 10 * 1024 * 1024;

    /// <summary>
    /// Snappy worst-case compressed length for a payload of size n.
    /// See https://github.com/google/snappy/blob/32ded457/snappy.cc#L218
    /// </summary>
    public static int MaxCompressedLen(int n) => 32 + n + n / 6;

    /// <summary>
    /// Spec-derived maximum message size: snappy worst-case of MaxPayloadSize + 1024 bytes
    /// framing overhead, with a floor of 1 MiB.
    /// </summary>
    public static readonly int MaxMessageSize = Math.Max(MaxCompressedLen(MaxPayloadSize) + 1024, 1024 * 1024);
    private const int BlocksByRootRootsOffset = sizeof(uint);
    private const int SnappyStreamIdentifierLength = 10;
    private const int SnappyChunkHeaderLength = 4;
    private const int SnappyChunkChecksumLength = 4;
    private const int MaxSnappyChunkUncompressedBytes = 1 << 16;
    private const byte SnappyChunkTypeCompressed = 0x00;
    private const byte SnappyChunkTypeUncompressed = 0x01;
    private const byte SnappyChunkTypePadding = 0xFE;
    private const byte SnappyChunkTypeStreamIdentifier = 0xFF;
    private static readonly byte[] SnappyStreamIdentifier =
    [
        0xFF, 0x06, 0x00, 0x00, 0x73, 0x4E, 0x61, 0x50, 0x70, 0x59
    ];

    public static byte[] EncodeBlocksByRootRequest(IReadOnlyList<byte[]> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (roots.Count > MaxBlocksByRootRequestRoots)
        {
            throw new ArgumentOutOfRangeException(
                nameof(roots),
                roots.Count,
                $"A blocks_by_root request supports at most {MaxBlocksByRootRequestRoots} roots.");
        }

        var payload = new byte[BlocksByRootRootsOffset + (roots.Count * RootLength)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, BlocksByRootRootsOffset), BlocksByRootRootsOffset);
        var offset = BlocksByRootRootsOffset;
        foreach (var root in roots)
        {
            if (root is null || root.Length != RootLength)
            {
                throw new ArgumentException($"Each root must be {RootLength} bytes.", nameof(roots));
            }

            root.CopyTo(payload, offset);
            offset += RootLength;
        }

        return payload;
    }

    public static byte[][] DecodeBlocksByRootRequest(ReadOnlySpan<byte> payload)
    {
        if (TryDecodeBlocksByRootContainer(payload, out var roots))
        {
            return roots;
        }

        throw new InvalidOperationException(
            $"blocks_by_root payload is not a recognized encoding (expected container offset {BlocksByRootRootsOffset}).");
    }

    private static bool TryDecodeBlocksByRootContainer(ReadOnlySpan<byte> payload, out byte[][] roots)
    {
        roots = [];
        if (payload.Length < BlocksByRootRootsOffset)
        {
            return false;
        }

        var rootsOffset = BinaryPrimitives.ReadUInt32LittleEndian(payload[..BlocksByRootRootsOffset]);
        if (rootsOffset != BlocksByRootRootsOffset)
        {
            return false;
        }

        return TryParseRoots(payload[BlocksByRootRootsOffset..], out roots);
    }

    private static bool TryParseRoots(ReadOnlySpan<byte> payload, out byte[][] roots)
    {
        roots = [];
        if (payload.Length % RootLength != 0)
        {
            return false;
        }

        var count = payload.Length / RootLength;
        if (count > MaxBlocksByRootRequestRoots)
        {
            throw new InvalidOperationException(
                $"blocks_by_root request exceeds max roots ({MaxBlocksByRootRequestRoots}).");
        }

        roots = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            var root = new byte[RootLength];
            payload.Slice(i * RootLength, RootLength).CopyTo(root);
            roots[i] = root;
        }

        return true;
    }

    public static byte[] EncodeStatus(LeanStatusMessage status)
    {
        ArgumentNullException.ThrowIfNull(status);

        var payload = new byte[LeanStatusMessage.Length];
        status.FinalizedRoot.CopyTo(payload, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(LeanStatusMessage.RootLength, sizeof(ulong)), status.FinalizedSlot);
        status.HeadRoot.CopyTo(payload, LeanStatusMessage.CheckpointLength);
        BinaryPrimitives.WriteUInt64LittleEndian(
            payload.AsSpan(LeanStatusMessage.CheckpointLength + LeanStatusMessage.RootLength, sizeof(ulong)),
            status.HeadSlot);
        return payload;
    }

    public static LeanStatusMessage DecodeStatus(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != LeanStatusMessage.Length)
        {
            throw new InvalidOperationException($"status payload must be exactly {LeanStatusMessage.Length} bytes.");
        }

        var finalizedRoot = payload[..LeanStatusMessage.RootLength];
        var finalizedSlot = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(LeanStatusMessage.RootLength, sizeof(ulong)));
        var headRoot = payload.Slice(LeanStatusMessage.CheckpointLength, LeanStatusMessage.RootLength);
        var headSlot = BinaryPrimitives.ReadUInt64LittleEndian(
            payload.Slice(LeanStatusMessage.CheckpointLength + LeanStatusMessage.RootLength, sizeof(ulong)));
        return new LeanStatusMessage(finalizedRoot, finalizedSlot, headRoot, headSlot);
    }

    public static async Task WriteRequestAsync(IChannel channel, ReadOnlyMemory<byte> payload, CancellationToken token = default)
    {
        ValidatePayloadLength(payload.Length);

        await channel.WriteVarintAsync(payload.Length).OrThrow();
        var snappyPayload = EncodeSnappyFramedPayload(payload.Span);
        await channel.WriteAsync(new ReadOnlySequence<byte>(snappyPayload), token).OrThrow();
    }

    public static async Task<byte[]> ReadRequestPayloadAsync(IChannel channel, CancellationToken token = default)
    {
        var declaredLength = await channel.ReadVarintAsync(token);
        ValidatePayloadLength(declaredLength);
        return await ReadSnappyPayloadAsync(channel, declaredLength, token);
    }

    public static async Task WriteResponseAsync(
        IChannel channel,
        byte responseCode,
        ReadOnlyMemory<byte> payload,
        CancellationToken token = default)
    {
        ValidatePayloadLength(payload.Length);

        await channel.WriteAsync(new ReadOnlySequence<byte>([responseCode]), token).OrThrow();
        await channel.WriteVarintAsync(payload.Length).OrThrow();
        var snappyPayload = EncodeSnappyFramedPayload(payload.Span);
        await channel.WriteAsync(new ReadOnlySequence<byte>(snappyPayload), token).OrThrow();
    }

    public static async Task<LeanRpcResponse?> TryReadResponseAsync(IChannel channel, CancellationToken token = default)
    {
        var codeRead = await channel.ReadAsync(1, ReadBlockingMode.WaitAny, token);
        if (codeRead.Result != IOResult.Ok)
        {
            return null;
        }

        var code = codeRead.Data.First.Span[0];
        var declaredLength = await channel.ReadVarintAsync(token);
        ValidatePayloadLength(declaredLength);

        var payload = await ReadSnappyPayloadAsync(channel, declaredLength, token);
        return new LeanRpcResponse(code, payload);
    }

    private static async Task<byte[]> ReadSnappyPayloadAsync(IChannel channel, int declaredLength, CancellationToken token)
    {
        var framedPayload = await ReadSnappyFrameAsync(channel, declaredLength, token);
        return DecodeSnappyFramedPayload(framedPayload, declaredLength);
    }

    private static async Task<byte[]> ReadSnappyFrameAsync(IChannel channel, int declaredLength, CancellationToken token)
    {
        var frameBuffer = new ArrayBufferWriter<byte>(Math.Max(64, declaredLength));
        int? expectedFrameSize = null;
        var readUntilStreamEnd = false;

        while (expectedFrameSize is null || frameBuffer.WrittenCount < expectedFrameSize.Value)
        {
            var bytesToRead = expectedFrameSize is int frameSize
                ? Math.Max(1, frameSize - frameBuffer.WrittenCount)
                : 1024;

            var read = await channel.ReadAsync(bytesToRead, ReadBlockingMode.WaitAny, token);
            if (read.Result != IOResult.Ok)
            {
                if (declaredLength == 0 && frameBuffer.WrittenCount == 0)
                {
                    return [];
                }

                // Some peers close the write-side immediately after sending request bytes.
                // If we already buffered data, let payload decoding attempt compatibility
                // fallbacks (non-canonical framed/raw snappy variants).
                if (frameBuffer.WrittenCount > 0)
                {
                    return frameBuffer.WrittenSpan.ToArray();
                }

                throw new InvalidOperationException(
                    $"Unexpected end of snappy frame. Expected {declaredLength} decompressed bytes, received {frameBuffer.WrittenCount} framed bytes.");
            }

            AppendSequence(frameBuffer, read.Data);

            if (expectedFrameSize is null && !readUntilStreamEnd)
            {
                try
                {
                    if (TryGetSnappyFrameSize(frameBuffer.WrittenSpan, declaredLength, out var parsedFrameSize))
                    {
                        expectedFrameSize = parsedFrameSize;
                    }
                }
                catch (InvalidOperationException ex) when (IsDeclaredLengthMismatch(ex))
                {
                    // Some clients send a valid framed-snappy payload with an incorrect declared length varint.
                    // In that case we cannot trust frame-size detection by declared length, so drain to EOF
                    // to avoid leaving unread request bytes on the stream.
                    readUntilStreamEnd = true;
                }
            }
        }

        if (expectedFrameSize is null)
        {
            throw new InvalidOperationException("Unable to determine snappy frame size.");
        }

        if (frameBuffer.WrittenCount != expectedFrameSize.Value)
        {
            throw new InvalidOperationException(
                $"Snappy frame length mismatch. Expected {expectedFrameSize.Value} bytes, got {frameBuffer.WrittenCount} bytes.");
        }

        return frameBuffer.WrittenSpan.ToArray();
    }

    private static bool IsDeclaredLengthMismatch(InvalidOperationException ex)
    {
        const string mismatchMarker = "exceeds declared payload length";
        return ex.Message.Contains(mismatchMarker, StringComparison.Ordinal);
    }

    internal static byte[] EncodeSnappyFramedPayload(ReadOnlySpan<byte> payload)
    {
        using var memoryStream = new MemoryStream();
        using (var snappyStream = new SnappyStream(memoryStream, CompressionMode.Compress, leaveOpen: true))
        {
            snappyStream.Write(payload);
            snappyStream.Flush();
        }

        return memoryStream.ToArray();
    }

    internal static byte[] DecodeSnappyFramedPayload(ReadOnlySpan<byte> snappyPayload, int declaredLength)
    {
        ValidatePayloadLength(declaredLength);

        if (TryDecodeSnappyFramePayload(
                snappyPayload,
                declaredLength,
                requireStreamIdentifier: true,
                out var payload,
                out var frameSize))
        {
            if (frameSize != snappyPayload.Length)
            {
                throw new InvalidOperationException(
                    $"Snappy frame has trailing or truncated bytes. Expected frame size {frameSize}, got {snappyPayload.Length}.");
            }

            return payload;
        }

        if (TryDecodeSnappyFramePayload(
                snappyPayload,
                declaredLength,
                requireStreamIdentifier: false,
                out payload,
                out frameSize))
        {
            if (frameSize != snappyPayload.Length)
            {
                throw new InvalidOperationException(
                    $"Snappy frame has trailing or truncated bytes. Expected frame size {frameSize}, got {snappyPayload.Length}.");
            }

            return payload;
        }

        try
        {
            var decompressed = Snappy.DecompressToArray(snappyPayload.ToArray());
            if (decompressed.Length == declaredLength)
            {
                return decompressed;
            }
        }
        catch
        {
            // Fall through to uncompressed fallback.
        }

        if (snappyPayload.Length == declaredLength)
        {
            return snappyPayload.ToArray();
        }

        throw new InvalidOperationException(
            $"Unexpected end of snappy payload. Could not determine frame size for declared payload length {declaredLength}.");
    }

    private static bool TryGetSnappyFrameSize(ReadOnlySpan<byte> framedBytes, int expectedUncompressedLength, out int frameSize)
    {
        frameSize = 0;
        if (TryDecodeSnappyFramePayload(
                framedBytes,
                expectedUncompressedLength,
                requireStreamIdentifier: true,
                out _,
                out frameSize))
        {
            return true;
        }

        return TryDecodeSnappyFramePayload(
            framedBytes,
            expectedUncompressedLength,
            requireStreamIdentifier: false,
            out _,
            out frameSize);
    }

    private static bool TryDecodeSnappyFramePayload(
        ReadOnlySpan<byte> framedBytes,
        int expectedUncompressedLength,
        bool requireStreamIdentifier,
        out byte[] payload,
        out int frameSize)
    {
        payload = [];
        frameSize = 0;

        if (expectedUncompressedLength == 0 && framedBytes.Length == 0)
        {
            return true;
        }

        var readOffset = 0;
        if (requireStreamIdentifier)
        {
            if (framedBytes.Length < SnappyStreamIdentifierLength)
            {
                return false;
            }

            if (!framedBytes[..SnappyStreamIdentifierLength].SequenceEqual(SnappyStreamIdentifier))
            {
                return false;
            }

            if (expectedUncompressedLength == 0)
            {
                payload = [];
                frameSize = SnappyStreamIdentifierLength;
                return true;
            }

            readOffset = SnappyStreamIdentifierLength;
        }

        var output = expectedUncompressedLength == 0
            ? Array.Empty<byte>()
            : new byte[expectedUncompressedLength];
        var totalUncompressedLength = 0;
        while (readOffset < framedBytes.Length)
        {
            if (framedBytes.Length - readOffset < SnappyChunkHeaderLength)
            {
                return false;
            }

            var chunkType = framedBytes[readOffset];
            var chunkLength = ReadUInt24LittleEndian(framedBytes.Slice(readOffset + 1, 3));
            if (chunkLength < 0)
            {
                throw new InvalidOperationException("Invalid snappy chunk length.");
            }

            var chunkPayloadOffset = checked(readOffset + SnappyChunkHeaderLength);
            var chunkEnd = checked(chunkPayloadOffset + chunkLength);
            if (chunkEnd > framedBytes.Length)
            {
                return false;
            }

            switch (chunkType)
            {
                case SnappyChunkTypeCompressed:
                case SnappyChunkTypeUncompressed:
                    {
                        if (chunkLength < SnappyChunkChecksumLength)
                        {
                            throw new InvalidOperationException("Snappy chunk is too short to contain checksum.");
                        }

                        byte[] chunkUncompressed;
                        if (chunkType == SnappyChunkTypeCompressed)
                        {
                            var compressed = framedBytes.Slice(
                                chunkPayloadOffset + SnappyChunkChecksumLength,
                                chunkLength - SnappyChunkChecksumLength);
                            try
                            {
                                chunkUncompressed = Snappy.DecompressToArray(compressed.ToArray());
                            }
                            catch (Exception ex)
                            {
                                throw new InvalidOperationException(
                                    $"Invalid snappy compressed chunk: {ex.Message}",
                                    ex);
                            }
                        }
                        else
                        {
                            chunkUncompressed = framedBytes.Slice(
                                    chunkPayloadOffset + SnappyChunkChecksumLength,
                                    chunkLength - SnappyChunkChecksumLength)
                                .ToArray();
                        }

                        if (chunkUncompressed.Length > MaxSnappyChunkUncompressedBytes)
                        {
                            throw new InvalidOperationException(
                                $"Snappy chunk exceeds maximum uncompressed size ({MaxSnappyChunkUncompressedBytes} bytes).");
                        }

                        var nextOffset = checked(totalUncompressedLength + chunkUncompressed.Length);
                        if (nextOffset > expectedUncompressedLength)
                        {
                            throw new InvalidOperationException(
                                $"Snappy frame exceeds declared payload length ({expectedUncompressedLength} bytes).");
                        }

                        chunkUncompressed.CopyTo(output, totalUncompressedLength);
                        totalUncompressedLength = nextOffset;
                        if (totalUncompressedLength == expectedUncompressedLength)
                        {
                            payload = output;
                            frameSize = chunkEnd;
                            return true;
                        }

                        readOffset = chunkEnd;
                        break;
                    }
                case SnappyChunkTypePadding:
                    {
                        readOffset = chunkEnd;
                        break;
                    }
                case SnappyChunkTypeStreamIdentifier:
                    {
                        if (chunkLength != 6 ||
                            !framedBytes.Slice(chunkPayloadOffset, 6).SequenceEqual(SnappyStreamIdentifier.AsSpan(4, 6)))
                        {
                            throw new InvalidOperationException("Invalid nested snappy stream identifier chunk.");
                        }

                        readOffset = chunkEnd;
                        break;
                    }
                default:
                    {
                        if (chunkType is >= 0x02 and <= 0x7F)
                        {
                            throw new InvalidOperationException($"Unknown unskippable snappy chunk type: 0x{chunkType:X2}.");
                        }

                        // Skippable reserved chunk.
                        readOffset = chunkEnd;
                        break;
                    }
            }
        }

        return false;
    }

    private static void AppendSequence(ArrayBufferWriter<byte> writer, ReadOnlySequence<byte> sequence)
    {
        foreach (var segment in sequence)
        {
            var destination = writer.GetSpan(segment.Length);
            segment.Span.CopyTo(destination);
            writer.Advance(segment.Length);
        }
    }

    private static int ReadUInt24LittleEndian(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 3)
        {
            throw new ArgumentException("Expected exactly 3 bytes for uint24.", nameof(bytes));
        }

        return bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
    }

    private static void ValidatePayloadLength(int payloadLength)
    {
        if (payloadLength is < 0 or > MaxPayloadSize)
        {
            throw new InvalidOperationException($"RPC payload length {payloadLength} is outside allowed range [0, {MaxPayloadSize}].");
        }
    }

    public readonly record struct LeanRpcResponse(byte Code, byte[] Payload);
}
