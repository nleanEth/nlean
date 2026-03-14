using Lean.Consensus.Types;
using NUnit.Framework;

namespace Lean.Consensus.Tests.Types;

[TestFixture]
public sealed class ValidatorIndexTests
{
    [Test]
    public void ImplicitConversion_FromUlong()
    {
        ValidatorIndex idx = 42UL;
        Assert.That(idx.Value, Is.EqualTo(42UL));
    }

    [Test]
    public void ImplicitConversion_ToUlong()
    {
        var idx = new ValidatorIndex(42);
        ulong val = idx;
        Assert.That(val, Is.EqualTo(42UL));
    }

    [Test]
    public void IsProposerFor_CorrectSlot()
    {
        var idx = new ValidatorIndex(2);
        Assert.That(idx.IsProposerFor(2, 4), Is.True);
        Assert.That(idx.IsProposerFor(6, 4), Is.True);  // 6 % 4 = 2
        Assert.That(idx.IsProposerFor(0, 4), Is.False);  // 0 % 4 = 0
    }

    [Test]
    public void IsValid_WithinRange()
    {
        var idx = new ValidatorIndex(3);
        Assert.That(idx.IsValid(4), Is.True);
        Assert.That(idx.IsValid(3), Is.False);
    }

    [Test]
    public void ComputeSubnetId_ModuloCommittees()
    {
        var idx = new ValidatorIndex(5);
        Assert.That(idx.ComputeSubnetId(3), Is.EqualTo(2));  // 5 % 3 = 2
        Assert.That(idx.ComputeSubnetId(1), Is.EqualTo(0));  // 5 % 1 = 0
    }

    [Test]
    public void Equality_Works()
    {
        var a = new ValidatorIndex(1);
        var b = new ValidatorIndex(1);
        var c = new ValidatorIndex(2);
        Assert.That(a, Is.EqualTo(b));
        Assert.That(a, Is.Not.EqualTo(c));
        Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
    }
}
