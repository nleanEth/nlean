namespace Lean.Consensus.Types;

public readonly struct Slot : IComparable<Slot>, IEquatable<Slot>
{
    public Slot(ulong value)
    {
        Value = value;
    }

    public ulong Value { get; }

    public int? JustifiedIndexAfter(Slot finalizedSlot)
    {
        if (Value <= finalizedSlot.Value)
        {
            return null;
        }

        return checked((int)(Value - finalizedSlot.Value - 1));
    }

    public bool IsJustifiableAfter(Slot finalizedSlot)
    {
        if (Value < finalizedSlot.Value)
        {
            throw new InvalidOperationException("Candidate slot must not be before finalized slot.");
        }

        var delta = (long)(Value - finalizedSlot.Value);
        if (delta <= 5)
        {
            return true;
        }

        var root = (long)Math.Sqrt(delta);
        if (root * root == delta)
        {
            return true;
        }

        var fourDeltaPlusOne = 4 * delta + 1;
        var sqrt = (long)Math.Sqrt(fourDeltaPlusOne);
        return sqrt * sqrt == fourDeltaPlusOne && (sqrt % 2 == 1);
    }

    public int CompareTo(Slot other) => Value.CompareTo(other.Value);

    public bool Equals(Slot other) => Value == other.Value;

    public override bool Equals(object? obj) => obj is Slot other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode();

    public static bool operator <(Slot left, Slot right) => left.Value < right.Value;
    public static bool operator >(Slot left, Slot right) => left.Value > right.Value;
    public static bool operator <=(Slot left, Slot right) => left.Value <= right.Value;
    public static bool operator >=(Slot left, Slot right) => left.Value >= right.Value;
}
