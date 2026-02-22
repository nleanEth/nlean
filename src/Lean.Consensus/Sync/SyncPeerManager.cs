namespace Lean.Consensus.Sync;

public sealed class SyncPeerManager
{
    private const int InitialScore = 100;
    private const int MinScore = 0;
    private const int MaxScore = 200;
    private const int SuccessScoreDelta = 10;
    private const int FailureScoreDelta = -20;
    private const int MaxInflightPerPeer = 2;

    private readonly Dictionary<string, SyncPeer> _peers = new(StringComparer.Ordinal);
    private readonly Random _random = new();

    public int PeerCount => _peers.Count;

    public void AddPeer(string peerId)
    {
        _peers.TryAdd(peerId, new SyncPeer(peerId));
    }

    public void RemovePeer(string peerId)
    {
        _peers.Remove(peerId);
    }

    public void UpdatePeerStatus(string peerId, ulong headSlot, ulong finalizedSlot)
    {
        if (_peers.TryGetValue(peerId, out var peer))
        {
            peer.HeadSlot = headSlot;
            peer.FinalizedSlot = finalizedSlot;
        }
    }

    public void OnRequestSuccess(string peerId)
    {
        if (_peers.TryGetValue(peerId, out var peer))
            peer.Score = Math.Clamp(peer.Score + SuccessScoreDelta, MinScore, MaxScore);
    }

    public void OnRequestFailure(string peerId)
    {
        if (_peers.TryGetValue(peerId, out var peer))
            peer.Score = Math.Clamp(peer.Score + FailureScoreDelta, MinScore, MaxScore);
    }

    public int GetPeerScore(string peerId) =>
        _peers.TryGetValue(peerId, out var peer) ? peer.Score : 0;

    public void IncrementInflight(string peerId)
    {
        if (_peers.TryGetValue(peerId, out var peer))
            peer.RequestsInFlight++;
    }

    public void DecrementInflight(string peerId)
    {
        if (_peers.TryGetValue(peerId, out var peer) && peer.RequestsInFlight > 0)
            peer.RequestsInFlight--;
    }

    public string? SelectPeerForRequest()
    {
        var eligible = new List<SyncPeer>();
        var totalWeight = 0;

        foreach (var peer in _peers.Values)
        {
            if (peer.RequestsInFlight < MaxInflightPerPeer && peer.Score > 0)
            {
                eligible.Add(peer);
                totalWeight += peer.Score;
            }
        }

        if (eligible.Count == 0)
            return null;

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

    public ulong GetNetworkHeadSlot()
    {
        ulong max = 0;
        foreach (var peer in _peers.Values)
        {
            if (peer.HeadSlot > max)
                max = peer.HeadSlot;
        }

        return max;
    }

    public ulong GetNetworkFinalizedSlot()
    {
        ulong max = 0;
        foreach (var peer in _peers.Values)
        {
            if (peer.FinalizedSlot > max)
                max = peer.FinalizedSlot;
        }

        return max;
    }

    private sealed class SyncPeer
    {
        public SyncPeer(string peerId) => PeerId = peerId;
        public string PeerId { get; }
        public ulong HeadSlot { get; set; }
        public ulong FinalizedSlot { get; set; }
        public int RequestsInFlight { get; set; }
        public int Score { get; set; } = InitialScore;
    }
}
