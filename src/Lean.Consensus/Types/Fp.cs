namespace Lean.Consensus.Types;

public readonly struct Fp : IEquatable<Fp>
{
    public const int ByteLength = 4;

    public Fp(uint value)
    {
        Value = value;
    }

    public uint Value { get; }

    public static Fp Zero => new(0);

    public bool Equals(Fp other) => Value == other.Value;

    public override bool Equals(object? obj) => obj is Fp other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode();
}
