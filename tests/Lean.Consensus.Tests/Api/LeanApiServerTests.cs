using Lean.Consensus.Api;
using NUnit.Framework;

namespace Lean.Consensus.Tests.Api;

[TestFixture]
public sealed class LeanApiServerTests
{
    private const string Prefix = "http://localhost:19876/";
    private LeanApiServer _server = null!;
    private HttpClient _client = null!;
    private CancellationTokenSource _cts = null!;

    [SetUp]
    public async Task SetUp()
    {
        _cts = new CancellationTokenSource();
        _client = new HttpClient { BaseAddress = new Uri(Prefix) };
        _server = new LeanApiServer(
            Prefix,
            () => new ApiSnapshot(10, "0xaabb", 5, "0xccdd"),
            () => new byte[] { 0x01, 0x02, 0x03 });
        await _server.StartAsync(_cts.Token);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _server.StopAsync();
        _client.Dispose();
        _cts.Dispose();
    }

    [Test]
    public async Task Health_Returns200()
    {
        var resp = await _client.GetAsync("lean/v0/health");
        Assert.That((int)resp.StatusCode, Is.EqualTo(200));
        var body = await resp.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("ok"));
    }

    [Test]
    public async Task JustifiedCheckpoint_ReturnsSnapshot()
    {
        var resp = await _client.GetAsync("lean/v0/checkpoints/justified");
        Assert.That((int)resp.StatusCode, Is.EqualTo(200));
        var body = await resp.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("\"slot\":10"));
        Assert.That(body, Does.Contain("0xaabb"));
    }

    [Test]
    public async Task FinalizedCheckpoint_ReturnsSnapshot()
    {
        var resp = await _client.GetAsync("lean/v0/checkpoints/finalized");
        Assert.That((int)resp.StatusCode, Is.EqualTo(200));
        var body = await resp.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("\"slot\":5"));
        Assert.That(body, Does.Contain("0xccdd"));
    }

    [Test]
    public async Task FinalizedState_WithoutAcceptHeader_Returns406()
    {
        var resp = await _client.GetAsync("lean/v0/states/finalized");
        Assert.That((int)resp.StatusCode, Is.EqualTo(406));
    }

    [Test]
    public async Task FinalizedState_WithAcceptHeader_ReturnsSsz()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "lean/v0/states/finalized");
        request.Headers.Add("Accept", "application/octet-stream");
        var resp = await _client.SendAsync(request);

        Assert.That((int)resp.StatusCode, Is.EqualTo(200));
        Assert.That(resp.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/octet-stream"));
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Assert.That(bytes, Is.EqualTo(new byte[] { 0x01, 0x02, 0x03 }));
    }

    [Test]
    public async Task FinalizedState_WhenNull_Returns404()
    {
        await _server.StopAsync();
        _server = new LeanApiServer(Prefix, () => new ApiSnapshot(0, "", 0, ""), () => null);
        await _server.StartAsync(_cts.Token);

        var request = new HttpRequestMessage(HttpMethod.Get, "lean/v0/states/finalized");
        request.Headers.Add("Accept", "application/octet-stream");
        var resp = await _client.SendAsync(request);

        Assert.That((int)resp.StatusCode, Is.EqualTo(404));
    }

    [Test]
    public async Task UnknownPath_Returns404()
    {
        var resp = await _client.GetAsync("lean/v0/unknown");
        Assert.That((int)resp.StatusCode, Is.EqualTo(404));
    }
}
