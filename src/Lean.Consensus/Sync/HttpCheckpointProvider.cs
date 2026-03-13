using Lean.Consensus.Types;

namespace Lean.Consensus.Sync;

public sealed class HttpCheckpointProvider : ICheckpointProvider
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task<State?> FetchFinalizedStateAsync(string url, CancellationToken ct)
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
        {
            return null;
        }

        byte[] body;
        try
        {
            body = await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch
        {
            return null;
        }

        if (body.Length == 0)
        {
            return null;
        }

        try
        {
            return SszDecoding.DecodeState(body);
        }
        catch
        {
            return null;
        }
    }
}
