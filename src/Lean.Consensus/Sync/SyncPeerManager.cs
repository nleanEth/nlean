using Lean.Consensus.Types;

namespace Lean.Consensus.Sync;

public sealed class SyncPeerManager
{
    private const int InitialScore = 100;
    private const int MinScore = 10;
    private const int MaxScore = 200;
    private const int SuccessScoreDelta = 10;
    private const int FailureScoreDelta = -10;
    private const int MaxInflightPerPeer = 2;

    private readonly object _lock = new();
    private readonly Dictionary<string, SyncPeer> _peers = new(StringComparer.Ordinal);
    private readonly Random _random = new();

    public int PeerCount { get { lock (_lock) return _peers.Count; } }

    public void AddPeer(string peerId)
    {
        if (!TryNormalizePeerId(peerId, out var normalizedPeerId))
            return;

        lock (_lock) _peers.TryAdd(normalizedPeerId, new SyncPeer(normalizedPeerId));
    }

    public void RemovePeer(string peerId)
    {
        if (!TryNormalizePeerId(peerId, out var normalizedPeerId))
            return;

        lock (_lock) _peers.Remove(normalizedPeerId);
    }

    public void UpdatePeerStatus(string peerId, ulong headSlot, ulong finalizedSlot, Bytes32? headRoot = null)
    {
        if (!TryNormalizePeerId(peerId, out var normalizedPeerId))
            return;

        lock (_lock)
        {
            if (!_peers.TryGetValue(normalizedPeerId, out var peer))
            {
                peer = new SyncPeer(normalizedPeerId);
                _peers[normalizedPeerId] = peer;
            }

            peer.HeadSlot = headSlot;
            peer.FinalizedSlot = finalizedSlot;
            peer.StatusReceived = true;
            if (headRoot is not null)
                peer.HeadRoot = headRoot.Value;
        }
    }

    public void OnRequestSuccess(string peerId)
    {
        if (!TryNormalizePeerId(peerId, out var normalizedPeerId))
            return;

        lock (_lock)
        {
            if (_peers.TryGetValue(normalizedPeerId, out var peer))
                peer.Score = Math.Clamp(peer.Score + SuccessScoreDelta, MinScore, MaxScore);
        }
    }

    public void OnRequestFailure(string peerId)
    {
        if (!TryNormalizePeerId(peerId, out var normalizedPeerId))
            return;

        lock (_lock)
        {
            if (_peers.TryGetValue(normalizedPeerId, out var peer))
                peer.Score = Math.Clamp(peer.Score + FailureScoreDelta, MinScore, MaxScore);
        }
    }

    public int GetPeerScore(string peerId)
    {
        if (!TryNormalizePeerId(peerId, out var normalizedPeerId))
            return 0;

        lock (_lock) return _peers.TryGetValue(normalizedPeerId, out var peer) ? peer.Score : 0;
    }

    public void IncrementInflight(string peerId)
    {
        if (!TryNormalizePeerId(peerId, out var normalizedPeerId))
            return;

        lock (_lock)
        {
            if (_peers.TryGetValue(normalizedPeerId, out var peer))
                peer.RequestsInFlight++;
        }
    }

    public void DecrementInflight(string peerId)
    {
        if (!TryNormalizePeerId(peerId, out var normalizedPeerId))
            return;

        lock (_lock)
        {
            if (_peers.TryGetValue(normalizedPeerId, out var peer) && peer.RequestsInFlight > 0)
                peer.RequestsInFlight--;
        }
    }

    public string? SelectPeerForRequest(string? preferredPeerId = null)
    {
        var hasPreferredPeerId = TryNormalizePeerId(preferredPeerId, out var normalizedPreferredPeerId);

        lock (_lock)
        {
            var eligible = new List<SyncPeer>();
            var totalWeight = 0;

            foreach (var peer in _peers.Values)
            {
                if (peer.RequestsInFlight < MaxInflightPerPeer && peer.Score >= MinScore)
                {
                    eligible.Add(peer);
                    totalWeight += peer.Score;
                }
            }

            if (eligible.Count == 0)
                return null;

            if (hasPreferredPeerId)
            {
                foreach (var peer in eligible)
                {
                    if (peer.PeerId == normalizedPreferredPeerId)
                        return peer.PeerId;
                }
            }

            var roll = _random.Next(totalWeight);
            var cumulative = 0;
            foreach (var peer in eligible)
            {
                cumulative += peer.Score;
                if (roll < cumulative)
                    return peer.PeerId;
            }

            return eligible[^1].PeerId;
        }
    }

    public ulong GetNetworkHeadSlot()
    {
        lock (_lock)
        {
            ulong max = 0;
            foreach (var peer in _peers.Values)
            {
                if (peer.HeadSlot > max)
                    max = peer.HeadSlot;
            }

            return max;
        }
    }

    public (string PeerId, Bytes32 Root, ulong Slot)? GetBestPeerHead()
    {
        lock (_lock)
        {
            SyncPeer? best = null;
            foreach (var peer in _peers.Values)
            {
                if (peer.HeadRoot is null)
                    continue;
                if (best is null || peer.HeadSlot > best.HeadSlot)
                    best = peer;
            }

            return best?.HeadRoot is not null
                ? (best.PeerId, best.HeadRoot.Value, best.HeadSlot)
                : null;
        }
    }

    /// <summary>
    /// Partially recovers scores of peers that were penalized, allowing them
    /// to become eligible for requests again after retry delays.
    /// </summary>
    public void RecoverScores()
    {
        lock (_lock)
        {
            foreach (var peer in _peers.Values)
            {
                if (peer.Score < InitialScore)
                    peer.Score = Math.Min(peer.Score + SuccessScoreDelta, InitialScore);
            }
        }
    }

    private sealed class SyncPeer
    {
        public SyncPeer(string peerId) => PeerId = peerId;
        public string PeerId { get; }
        public ulong HeadSlot { get; set; }
        public ulong FinalizedSlot { get; set; }
        public Bytes32? HeadRoot { get; set; }
        public int RequestsInFlight { get; set; }
        public int Score { get; set; } = InitialScore;
        public bool StatusReceived { get; set; }
    }

    private static bool TryNormalizePeerId(string? peerIdOrKey, out string normalizedPeerId)
    {
        normalizedPeerId = string.Empty;
        if (string.IsNullOrWhiteSpace(peerIdOrKey))
            return false;

        var trimmed = peerIdOrKey.Trim();
        const string marker = "/p2p/";
        var markerIndex = trimmed.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex >= 0)
        {
            var peerIdStart = markerIndex + marker.Length;
            if (peerIdStart >= trimmed.Length)
                return false;

            normalizedPeerId = trimmed[peerIdStart..];
            return !string.IsNullOrWhiteSpace(normalizedPeerId);
        }

        normalizedPeerId = trimmed;
        return true;
    }
}
