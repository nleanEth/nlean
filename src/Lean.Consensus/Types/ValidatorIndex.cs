namespace Lean.Consensus.Types;

public readonly struct ValidatorIndex : IEquatable<ValidatorIndex>, IComparable<ValidatorIndex>
{
    public ulong Value { get; }

    public ValidatorIndex(ulong value) => Value = value;

    public bool IsProposerFor(ulong slot, int numValidators)
        => (int)(slot % (ulong)numValidators) == (int)Value;

    public bool IsValid(int numValidators) => (int)Value < numValidators;

    public int ComputeSubnetId(int numCommittees) => (int)(Value % (ulong)numCommittees);

    public int CompareTo(ValidatorIndex other) => Value.CompareTo(other.Value);
    public bool Equals(ValidatorIndex other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is ValidatorIndex other && Equals(other);
    public override int GetHashCode() => Value.GetHashCode();
    public override string ToString() => Value.ToString();

    public static implicit operator ulong(ValidatorIndex v) => v.Value;
    public static implicit operator ValidatorIndex(ulong v) => new(v);
    public static bool operator ==(ValidatorIndex left, ValidatorIndex right) => left.Equals(right);
    public static bool operator !=(ValidatorIndex left, ValidatorIndex right) => !left.Equals(right);
}
