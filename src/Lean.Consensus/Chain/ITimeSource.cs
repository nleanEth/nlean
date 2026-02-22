namespace Lean.Consensus.Chain;

public interface ITimeSource
{
    DateTimeOffset UtcNow { get; }
}
