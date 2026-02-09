using Prometheus;

namespace Lean.Metrics;

public static class LeanMetrics
{
    public static readonly Gauge ConsensusCurrentSlot = Prometheus.Metrics.CreateGauge(
        "lean_consensus_current_slot",
        "Current local slot tracked by the consensus service.");

    public static readonly Gauge ConsensusHeadSlot = Prometheus.Metrics.CreateGauge(
        "lean_consensus_head_slot",
        "Current head slot tracked from block gossip.");

    public static readonly Gauge ConsensusJustifiedSlot = Prometheus.Metrics.CreateGauge(
        "lean_consensus_justified_slot",
        "Current justified slot tracked by fork choice.");

    public static readonly Gauge ConsensusFinalizedSlot = Prometheus.Metrics.CreateGauge(
        "lean_consensus_finalized_slot",
        "Current finalized slot tracked by fork choice.");

    public static readonly Gauge ConsensusSafeTargetSlot = Prometheus.Metrics.CreateGauge(
        "lean_consensus_safe_target_slot",
        "Current safe target slot tracked by fork choice.");

    public static readonly Counter ConsensusBlocksTotal = Prometheus.Metrics.CreateCounter(
        "lean_consensus_blocks_total",
        "Total number of block gossip messages processed.");

    public static readonly Counter ConsensusOrphanBlocksQueuedTotal = Prometheus.Metrics.CreateCounter(
        "lean_consensus_orphan_blocks_queued_total",
        "Total number of orphan blocks queued due to unknown parent.");

    public static readonly Counter ConsensusOrphanBlocksRecoveredTotal = Prometheus.Metrics.CreateCounter(
        "lean_consensus_orphan_blocks_recovered_total",
        "Total number of orphan blocks later recovered after parent arrival.");

    public static readonly Gauge ConsensusOrphanBlocksPending = Prometheus.Metrics.CreateGauge(
        "lean_consensus_orphan_blocks_pending",
        "Current number of orphan blocks waiting for parents.");

    public static readonly Counter ConsensusAttestationsTotal = Prometheus.Metrics.CreateCounter(
        "lean_consensus_attestations_total",
        "Total number of attestation gossip messages processed.");

    public static readonly Counter ConsensusAggregatesTotal = Prometheus.Metrics.CreateCounter(
        "lean_consensus_aggregates_total",
        "Total number of aggregate gossip messages processed.");

    public static readonly Counter AggregationTotal = Prometheus.Metrics.CreateCounter(
        "lean_pq_aggregation_total",
        "Total number of PQ aggregation operations.");

    public static readonly Histogram AggregationLatency = Prometheus.Metrics.CreateHistogram(
        "lean_pq_aggregation_latency_seconds",
        "Latency of PQ aggregation operations.");

    public static readonly Counter GossipMessagesTotal = Prometheus.Metrics.CreateCounter(
        "lean_gossip_messages_total",
        "Total number of gossip messages processed.",
        new CounterConfiguration
        {
            LabelNames = new[] { "topic" }
        });

    public static readonly Counter SyncBlocksByRootRequestsTotal = Prometheus.Metrics.CreateCounter(
        "lean_sync_blocks_by_root_requests_total",
        "Total blocks-by-root sync requests labeled by hit or miss.",
        new CounterConfiguration
        {
            LabelNames = new[] { "result" }
        });

    public static readonly Counter SyncBlocksByRootAttemptsTotal = Prometheus.Metrics.CreateCounter(
        "lean_sync_blocks_by_root_attempts_total",
        "Total outbound blocks-by-root RPC attempts labeled by outcome.",
        new CounterConfiguration
        {
            LabelNames = new[] { "result" }
        });

    public static readonly Counter SyncBlocksByRootFailuresTotal = Prometheus.Metrics.CreateCounter(
        "lean_sync_blocks_by_root_failures_total",
        "Total blocks-by-root sync failures labeled by reason.",
        new CounterConfiguration
        {
            LabelNames = new[] { "reason" }
        });

    public static readonly Histogram SyncBlocksByRootAttemptLatencySeconds = Prometheus.Metrics.CreateHistogram(
        "lean_sync_blocks_by_root_attempt_latency_seconds",
        "Latency of outbound blocks-by-root RPC attempts.",
        new HistogramConfiguration
        {
            LabelNames = new[] { "result" }
        });
}
