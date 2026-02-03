using Prometheus;

namespace Lean.Metrics;

public static class LeanMetrics
{
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
}
