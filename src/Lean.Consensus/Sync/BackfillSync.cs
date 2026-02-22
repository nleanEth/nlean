using Lean.Consensus.Types;

namespace Lean.Consensus.Sync;

public sealed class BackfillSync : IBackfillTrigger
{
    public const int DefaultMaxBackfillDepth = 512;
    public const int MaxBlocksPerRequest = 10;

    private readonly INetworkRequester _network;
    private readonly IBlockProcessor _processor;
    private readonly SyncPeerManager _peerManager;
    private readonly int _maxDepth;

    public BackfillSync(INetworkRequester network, IBlockProcessor processor,
        SyncPeerManager peerManager, int maxDepth = DefaultMaxBackfillDepth)
    {
        _network = network;
        _processor = processor;
        _peerManager = peerManager;
        _maxDepth = maxDepth;
    }

    public void RequestBackfill(Bytes32 parentRoot)
    {
        // Fire-and-forget; SyncService will manage the async lifecycle
        _ = RequestParentsAsync(new List<Bytes32> { parentRoot }, CancellationToken.None);
    }

    public async Task RequestParentsAsync(List<Bytes32> roots, CancellationToken ct)
    {
        var pending = new Queue<Bytes32>(roots);
        var depth = 0;

        while (pending.Count > 0 && depth < _maxDepth)
        {
            // Filter out already-known roots
            var batch = new List<Bytes32>();
            while (pending.Count > 0 && batch.Count < MaxBlocksPerRequest)
            {
                var root = pending.Dequeue();
                if (!_processor.IsBlockKnown(root))
                    batch.Add(root);
            }

            if (batch.Count == 0)
                break;

            var peerId = _peerManager.SelectPeerForRequest();
            if (peerId is null)
                break;

            _peerManager.IncrementInflight(peerId);
            try
            {
                var fetched = await _network.RequestBlocksByRootAsync(peerId, batch, ct);

                if (fetched.Count == 0)
                {
                    _peerManager.OnRequestFailure(peerId);
                    break;
                }

                _peerManager.OnRequestSuccess(peerId);

                foreach (var block in fetched)
                {
                    var result = _processor.ProcessBlock(block);
                    if (result.Accepted)
                    {
                        // If the parent of this fetched block is still unknown, enqueue it
                        var parentRoot = block.Message.Block.ParentRoot;
                        if (!_processor.IsBlockKnown(parentRoot))
                            pending.Enqueue(parentRoot);
                    }
                }
            }
            finally
            {
                _peerManager.DecrementInflight(peerId);
            }

            depth++;
        }
    }
}
