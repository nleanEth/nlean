namespace Lean.Network;

internal enum BlocksByRootPeerAttemptResult
{
    Success,
    EmptyResponse,
    Failure
}

internal sealed class BlocksByRootPeerSelector
{
    private readonly object _lock = new();
    private readonly Dictionary<string, PeerHealth> _peers = new(StringComparer.Ordinal);
    private long _nextOrder;

    public void RegisterCandidates(IEnumerable<string> peerKeys)
    {
        ArgumentNullException.ThrowIfNull(peerKeys);

        lock (_lock)
        {
            foreach (var peerKey in peerKeys)
            {
                if (!TryNormalizePeerKey(peerKey, out var normalized))
                {
                    continue;
                }

                RegisterCandidateLocked(normalized);
            }
        }
    }

    public void MarkConnected(string peerKey)
    {
        if (!TryNormalizePeerKey(peerKey, out var normalized))
        {
            return;
        }

        lock (_lock)
        {
            var peer = RegisterCandidateLocked(normalized);
            peer.IsConnected = true;
        }
    }

    public void MarkDisconnected(string peerKey)
    {
        if (!TryNormalizePeerKey(peerKey, out var normalized))
        {
            return;
        }

        lock (_lock)
        {
            if (_peers.TryGetValue(normalized, out var peer))
            {
                peer.IsConnected = false;
            }
        }
    }

    public void RecordAttempt(string peerKey, BlocksByRootPeerAttemptResult result, TimeSpan latency)
    {
        if (!TryNormalizePeerKey(peerKey, out var normalized))
        {
            return;
        }

        lock (_lock)
        {
            var peer = RegisterCandidateLocked(normalized);
            switch (result)
            {
                case BlocksByRootPeerAttemptResult.Success:
                    peer.SuccessCount++;
                    peer.ConsecutiveFailures = 0;
                    peer.TotalSuccessLatencyMs += Math.Max(0d, latency.TotalMilliseconds);
                    peer.LastSuccessUtc = DateTimeOffset.UtcNow;
                    break;
                case BlocksByRootPeerAttemptResult.EmptyResponse:
                case BlocksByRootPeerAttemptResult.Failure:
                    peer.FailureCount++;
                    peer.ConsecutiveFailures++;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(result), result, "Unknown blocks-by-root result.");
            }
        }
    }

    public IReadOnlyList<string> GetRequestOrder()
    {
        lock (_lock)
        {
            if (_peers.Count == 0)
            {
                return Array.Empty<string>();
            }

            var now = DateTimeOffset.UtcNow;
            return _peers.Values
                .OrderByDescending(static peer => peer.IsConnected)
                .ThenByDescending(peer => Score(peer, now))
                .ThenBy(static peer => peer.RegistrationOrder)
                .Select(static peer => peer.PeerKey)
                .ToArray();
        }
    }

    private static bool TryNormalizePeerKey(string? peerKey, out string normalized)
    {
        if (string.IsNullOrWhiteSpace(peerKey))
        {
            normalized = string.Empty;
            return false;
        }

        normalized = peerKey.Trim();
        return true;
    }

    private PeerHealth RegisterCandidateLocked(string peerKey)
    {
        if (!TryNormalizePeerKey(peerKey, out var normalized))
        {
            throw new ArgumentException("Peer key must be non-empty.", nameof(peerKey));
        }

        if (_peers.TryGetValue(normalized, out var existing))
        {
            return existing;
        }

        var created = new PeerHealth(normalized, _nextOrder++);
        _peers[normalized] = created;
        return created;
    }

    private static double Score(PeerHealth peer, DateTimeOffset now)
    {
        var attempts = peer.SuccessCount + peer.FailureCount;
        var successRate = (peer.SuccessCount + 1d) / (attempts + 2d);
        var averageLatencyMs = peer.SuccessCount == 0
            ? 500d
            : peer.TotalSuccessLatencyMs / peer.SuccessCount;
        var latencyScore = Math.Clamp(1d - (averageLatencyMs / 5_000d), -1d, 1d);
        var failurePenalty = Math.Min(0.9d, peer.ConsecutiveFailures * 0.15d);
        var recencyBonus = peer.LastSuccessUtc == DateTimeOffset.MinValue
            ? 0d
            : Math.Max(0d, 0.2d - (now - peer.LastSuccessUtc).TotalMinutes * 0.02d);

        return successRate + latencyScore - failurePenalty + recencyBonus;
    }

    private sealed class PeerHealth
    {
        public PeerHealth(string peerKey, long registrationOrder)
        {
            PeerKey = peerKey;
            RegistrationOrder = registrationOrder;
        }

        public string PeerKey { get; }

        public long RegistrationOrder { get; }

        public bool IsConnected { get; set; }

        public int SuccessCount { get; set; }

        public int FailureCount { get; set; }

        public int ConsecutiveFailures { get; set; }

        public double TotalSuccessLatencyMs { get; set; }

        public DateTimeOffset LastSuccessUtc { get; set; } = DateTimeOffset.MinValue;
    }
}
