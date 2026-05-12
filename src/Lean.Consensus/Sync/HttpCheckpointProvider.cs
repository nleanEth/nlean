using Lean.Consensus.Types;

namespace Lean.Consensus.Sync;

public sealed class HttpCheckpointProvider : ICheckpointProvider
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly SignedBlockGossipDecoder BlockDecoder = new();

    public async Task<State?> FetchFinalizedStateAsync(string url, CancellationToken ct)
    {
        var body = await FetchOctetStreamAsync(url, ct);
        if (body is null || body.Length == 0)
            return null;

        try
        {
            return SszDecoding.DecodeState(body);
        }
        catch
        {
            return null;
        }
    }

    public async Task<SignedBlock?> FetchFinalizedSignedBlockAsync(string url, CancellationToken ct)
    {
        var body = await FetchOctetStreamAsync(url, ct);
        if (body is null || body.Length == 0)
            return null;

        // Reuse the gossip decoder — /lean/v0/blocks/finalized serves the
        // same SSZ layout as gossip (raw SignedBlock, no snappy frame).
        var decoded = BlockDecoder.DecodeAndValidate(body);
        return decoded.IsSuccess ? decoded.SignedBlock : null;
    }

    private static async Task<byte[]?> FetchOctetStreamAsync(string url, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = Timeout };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/octet-stream"));

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct);
        }
        catch
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
            return null;

        try
        {
            return await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch
        {
            return null;
        }
    }
}
