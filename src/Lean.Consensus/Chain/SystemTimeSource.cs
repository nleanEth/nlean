namespace Lean.Consensus.Chain;

public sealed class SystemTimeSource : ITimeSource
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
