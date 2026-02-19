using Prometheus;

namespace Lean.Metrics;

public static class LeanMetrics
{
    private static readonly double[] ShortLatencyBuckets = { 0.005, 0.01, 0.025, 0.05, 0.1, 1 };
    private static readonly double[] StateTransitionBuckets = { 0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 2.5, 3, 4 };
    private static readonly double[] ReorgDepthBuckets = { 1, 2, 3, 5, 7, 10, 20, 30, 50, 100 };

    public static readonly Gauge NodeInfo = Prometheus.Metrics.CreateGauge(
        "lean_node_info",
        "Node information (always 1).",
        new GaugeConfiguration
        {
            LabelNames = new[] { "name", "version" }
        });

    public static readonly Gauge NodeStartTimeSeconds = Prometheus.Metrics.CreateGauge(
        "lean_node_start_time_seconds",
        "Node start timestamp in unix seconds.");

    public static readonly Histogram PqSigAttestationSigningTimeSeconds = Prometheus.Metrics.CreateHistogram(
        "lean_pq_sig_attestation_signing_time_seconds",
        "Time taken to sign an attestation.",
        new HistogramConfiguration
        {
            Buckets = ShortLatencyBuckets
        });

    public static readonly Histogram PqSigAttestationVerificationTimeSeconds = Prometheus.Metrics.CreateHistogram(
        "lean_pq_sig_attestation_verification_time_seconds",
        "Time taken to verify an attestation signature.",
        new HistogramConfiguration
        {
            Buckets = ShortLatencyBuckets
        });

    public static readonly Counter PqSigAggregatedSignaturesTotal = Prometheus.Metrics.CreateCounter(
        "lean_pq_sig_aggregated_signatures_total",
        "Total number of aggregated signatures.");

    public static readonly Counter PqSigAttestationsInAggregatedSignaturesTotal = Prometheus.Metrics.CreateCounter(
        "lean_pq_sig_attestations_in_aggregated_signatures_total",
        "Total number of attestations included into aggregated signatures.");

    public static readonly Histogram PqSigAttestationSignaturesBuildingTimeSeconds = Prometheus.Metrics.CreateHistogram(
        "lean_pq_sig_attestation_signatures_building_time_seconds",
        "Time taken to build an aggregated attestation signature.",
        new HistogramConfiguration
        {
            Buckets = ShortLatencyBuckets
        });

    public static readonly Histogram PqSigAggregatedSignaturesVerificationTimeSeconds = Prometheus.Metrics.CreateHistogram(
        "lean_pq_sig_aggregated_signatures_verification_time_seconds",
        "Time taken to verify an aggregated attestation signature.",
        new HistogramConfiguration
        {
            Buckets = ShortLatencyBuckets
        });

    public static readonly Counter PqSigAggregatedSignaturesValidTotal = Prometheus.Metrics.CreateCounter(
        "lean_pq_sig_aggregated_signatures_valid_total",
        "Total number of valid aggregated signatures.");

    public static readonly Counter PqSigAggregatedSignaturesInvalidTotal = Prometheus.Metrics.CreateCounter(
        "lean_pq_sig_aggregated_signatures_invalid_total",
        "Total number of invalid aggregated signatures.");

    public static readonly Gauge HeadSlot = Prometheus.Metrics.CreateGauge(
        "lean_head_slot",
        "Latest slot of the lean chain.");

    public static readonly Gauge CurrentSlot = Prometheus.Metrics.CreateGauge(
        "lean_current_slot",
        "Current slot of the lean chain.");

    public static readonly Gauge SafeTargetSlot = Prometheus.Metrics.CreateGauge(
        "lean_safe_target_slot",
        "Safe target slot.");

    public static readonly Histogram ForkChoiceBlockProcessingTimeSeconds = Prometheus.Metrics.CreateHistogram(
        "lean_fork_choice_block_processing_time_seconds",
        "Time taken to process block in fork choice.",
        new HistogramConfiguration
        {
            Buckets = ShortLatencyBuckets
        });

    public static readonly Counter AttestationsValidTotal = Prometheus.Metrics.CreateCounter(
        "lean_attestations_valid_total",
        "Total number of valid attestations.",
        new CounterConfiguration
        {
            LabelNames = new[] { "source" }
        });

    public static readonly Counter AttestationsInvalidTotal = Prometheus.Metrics.CreateCounter(
        "lean_attestations_invalid_total",
        "Total number of invalid attestations.",
        new CounterConfiguration
        {
            LabelNames = new[] { "source" }
        });

    public static readonly Histogram AttestationValidationTimeSeconds = Prometheus.Metrics.CreateHistogram(
        "lean_attestation_validation_time_seconds",
        "Time taken to validate attestation.",
        new HistogramConfiguration
        {
            Buckets = ShortLatencyBuckets
        });

    public static readonly Counter ForkChoiceReorgsTotal = Prometheus.Metrics.CreateCounter(
        "lean_fork_choice_reorgs_total",
        "Total number of fork choice reorgs.");

    public static readonly Histogram ForkChoiceReorgDepth = Prometheus.Metrics.CreateHistogram(
        "lean_fork_choice_reorg_depth",
        "Depth of fork choice reorgs (in blocks).",
        new HistogramConfiguration
        {
            Buckets = ReorgDepthBuckets
        });

    public static readonly Gauge LatestJustifiedSlot = Prometheus.Metrics.CreateGauge(
        "lean_latest_justified_slot",
        "Latest justified slot.");

    public static readonly Gauge LatestFinalizedSlot = Prometheus.Metrics.CreateGauge(
        "lean_latest_finalized_slot",
        "Latest finalized slot.");

    public static readonly Counter FinalizationsTotal = Prometheus.Metrics.CreateCounter(
        "lean_finalizations_total",
        "Total number of finalization attempts.",
        new CounterConfiguration
        {
            LabelNames = new[] { "result" }
        });

    public static readonly Histogram StateTransitionTimeSeconds = Prometheus.Metrics.CreateHistogram(
        "lean_state_transition_time_seconds",
        "Time to process state transition.",
        new HistogramConfiguration
        {
            Buckets = StateTransitionBuckets
        });

    public static readonly Counter StateTransitionSlotsProcessedTotal = Prometheus.Metrics.CreateCounter(
        "lean_state_transition_slots_processed_total",
        "Total number of processed slots.");

    public static readonly Histogram StateTransitionSlotsProcessingTimeSeconds = Prometheus.Metrics.CreateHistogram(
        "lean_state_transition_slots_processing_time_seconds",
        "Time taken to process slots.",
        new HistogramConfiguration
        {
            Buckets = ShortLatencyBuckets
        });

    public static readonly Histogram StateTransitionBlockProcessingTimeSeconds = Prometheus.Metrics.CreateHistogram(
        "lean_state_transition_block_processing_time_seconds",
        "Time taken to process block.",
        new HistogramConfiguration
        {
            Buckets = ShortLatencyBuckets
        });

    public static readonly Counter StateTransitionAttestationsProcessedTotal = Prometheus.Metrics.CreateCounter(
        "lean_state_transition_attestations_processed_total",
        "Total number of processed attestations.");

    public static readonly Histogram StateTransitionAttestationsProcessingTimeSeconds = Prometheus.Metrics.CreateHistogram(
        "lean_state_transition_attestations_processing_time_seconds",
        "Time taken to process attestations.",
        new HistogramConfiguration
        {
            Buckets = ShortLatencyBuckets
        });

    public static readonly Gauge ValidatorsCount = Prometheus.Metrics.CreateGauge(
        "lean_validators_count",
        "Number of validators managed by a node.");

    public static readonly Gauge ConnectedPeers = Prometheus.Metrics.CreateGauge(
        "lean_connected_peers",
        "Number of connected peers.");

    public static readonly Counter PeerConnectionEventsTotal = Prometheus.Metrics.CreateCounter(
        "lean_peer_connection_events_total",
        "Total number of peer connection events.",
        new CounterConfiguration
        {
            LabelNames = new[] { "direction", "result" }
        });

    public static readonly Counter PeerDisconnectionEventsTotal = Prometheus.Metrics.CreateCounter(
        "lean_peer_disconnection_events_total",
        "Total number of peer disconnection events.",
        new CounterConfiguration
        {
            LabelNames = new[] { "direction", "reason" }
        });

    public static void SetNodeInfo(string nodeName, string nodeVersion)
    {
        var normalizedName = string.IsNullOrWhiteSpace(nodeName) ? "nlean" : nodeName.Trim();
        var normalizedVersion = string.IsNullOrWhiteSpace(nodeVersion) ? "unknown" : nodeVersion.Trim();
        NodeInfo.WithLabels(normalizedName, normalizedVersion).Set(1);
        NodeStartTimeSeconds.Set(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    public static void SetCurrentSlot(double slot)
    {
        CurrentSlot.Set(slot);
    }

    public static void SetHeadSlot(double slot)
    {
        HeadSlot.Set(slot);
    }

    public static void SetJustifiedSlot(double slot)
    {
        LatestJustifiedSlot.Set(slot);
    }

    public static void SetFinalizedSlot(double slot)
    {
        LatestFinalizedSlot.Set(slot);
    }

    public static void SetSafeTargetSlot(double slot)
    {
        SafeTargetSlot.Set(slot);
    }

    public static void RecordForkChoiceBlockProcessing(TimeSpan elapsed)
    {
        ForkChoiceBlockProcessingTimeSeconds.Observe(Math.Max(0d, elapsed.TotalSeconds));
    }

    public static void RecordAttestationValidation(string source, bool valid, TimeSpan elapsed)
    {
        var normalizedSource = string.Equals(source, "block", StringComparison.OrdinalIgnoreCase)
            ? "block"
            : "gossip";
        AttestationValidationTimeSeconds.Observe(Math.Max(0d, elapsed.TotalSeconds));
        if (valid)
        {
            AttestationsValidTotal.WithLabels(normalizedSource).Inc();
        }
        else
        {
            AttestationsInvalidTotal.WithLabels(normalizedSource).Inc();
        }
    }

    public static void RecordForkChoiceReorg(ulong depth)
    {
        if (depth == 0)
        {
            return;
        }

        ForkChoiceReorgsTotal.Inc();
        ForkChoiceReorgDepth.Observe(depth);
    }

    public static void RecordPqAttestationSigning(TimeSpan elapsed)
    {
        PqSigAttestationSigningTimeSeconds.Observe(Math.Max(0d, elapsed.TotalSeconds));
    }

    public static void RecordPqAttestationVerification(TimeSpan elapsed)
    {
        PqSigAttestationVerificationTimeSeconds.Observe(Math.Max(0d, elapsed.TotalSeconds));
    }

    public static void RecordPqAggregatedSignatureBuilt(int attestationsIncluded, TimeSpan elapsed)
    {
        PqSigAggregatedSignaturesTotal.Inc();
        if (attestationsIncluded > 0)
        {
            PqSigAttestationsInAggregatedSignaturesTotal.Inc(attestationsIncluded);
        }

        PqSigAttestationSignaturesBuildingTimeSeconds.Observe(Math.Max(0d, elapsed.TotalSeconds));
    }

    public static void RecordPqAggregatedSignatureVerification(bool valid, TimeSpan elapsed)
    {
        PqSigAggregatedSignaturesVerificationTimeSeconds.Observe(Math.Max(0d, elapsed.TotalSeconds));
        if (valid)
        {
            PqSigAggregatedSignaturesValidTotal.Inc();
        }
        else
        {
            PqSigAggregatedSignaturesInvalidTotal.Inc();
        }
    }

    public static void RecordStateTransition(
        TimeSpan totalElapsed,
        ulong slotsProcessed,
        TimeSpan slotsProcessingElapsed,
        TimeSpan blockProcessingElapsed,
        ulong attestationsProcessed,
        TimeSpan attestationsProcessingElapsed)
    {
        StateTransitionTimeSeconds.Observe(Math.Max(0d, totalElapsed.TotalSeconds));
        StateTransitionSlotsProcessingTimeSeconds.Observe(Math.Max(0d, slotsProcessingElapsed.TotalSeconds));
        StateTransitionBlockProcessingTimeSeconds.Observe(Math.Max(0d, blockProcessingElapsed.TotalSeconds));
        StateTransitionAttestationsProcessingTimeSeconds.Observe(Math.Max(0d, attestationsProcessingElapsed.TotalSeconds));

        if (slotsProcessed > 0)
        {
            StateTransitionSlotsProcessedTotal.Inc(slotsProcessed);
        }

        if (attestationsProcessed > 0)
        {
            StateTransitionAttestationsProcessedTotal.Inc(attestationsProcessed);
        }
    }

    public static void RecordFinalizationResult(bool success)
    {
        FinalizationsTotal.WithLabels(success ? "success" : "error").Inc();
    }

    public static void SetValidatorsCount(ulong count)
    {
        ValidatorsCount.Set(count);
    }

    public static void SetConnectedPeers(int peerCount)
    {
        ConnectedPeers.Set(Math.Max(0, peerCount));
    }
}
