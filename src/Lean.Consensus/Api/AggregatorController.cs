namespace Lean.Consensus.Api;

/// <summary>
/// Runtime control over the node's aggregator role. Operators rotate the
/// role across nodes via the <c>/lean/v0/admin/aggregator</c> admin API when
/// an active aggregator becomes unhealthy — without restarting the node.
///
/// Toggles are serialized so concurrent admin requests cannot leave
/// subscribers disagreeing on the current role. Subscribers are invoked
/// under the lock with the new value whenever it actually changes.
/// </summary>
public sealed class AggregatorController
{
    private readonly object _lock = new();
    private readonly List<Action<bool>> _subscribers = new();
    private bool _enabled;

    public AggregatorController(bool initial)
    {
        _enabled = initial;
    }

    public bool IsEnabled
    {
        get { lock (_lock) return _enabled; }
    }

    /// <summary>
    /// Register a handler invoked on every genuine toggle. The handler fires
    /// under the internal lock, so subscribers see a consistent view across
    /// concurrent POST requests.
    /// </summary>
    public void Subscribe(Action<bool> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock)
        {
            _subscribers.Add(handler);
        }
    }

    /// <summary>
    /// Atomically update the role. Returns the previous value.
    /// Subscribers fire only when the new value differs from the previous one.
    /// </summary>
    public bool SetEnabled(bool enabled)
    {
        lock (_lock)
        {
            var previous = _enabled;
            _enabled = enabled;
            if (previous != enabled)
            {
                foreach (var subscriber in _subscribers)
                {
                    subscriber(enabled);
                }
            }
            return previous;
        }
    }
}
