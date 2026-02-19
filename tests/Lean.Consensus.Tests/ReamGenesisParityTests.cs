using Lean.Consensus.Types;
using NUnit.Framework;

namespace Lean.Consensus.Tests;

[TestFixture]
public sealed class ReamGenesisParityTests
{
    [Test]
    public void HashBytes32_ReturnsInputForSingleChunk()
    {
        var input = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        Assert.That(new Bytes32(input).HashTreeRoot(), Is.EqualTo(input));
    }

    [Test]
    public void GenesisBlockRoot_MatchesReamReferenceVectors()
    {
        var (block1, state1) = BuildGenesisArtifacts(1000, 1);
        var block2 = BuildGenesisBlock(1000, 10);
        var block3 = BuildGenesisBlock(2000, 1);

        Assert.That(ToHex(state1.HashTreeRoot()), Is.EqualTo("7306FFD0788192406EAE762E52AF3AC2E132E8B2D61F5DC092E26B1D94B9E46C"));
        Assert.That(ToHex(block1.HashTreeRoot()), Is.EqualTo("CC03F11DD80DD79A4ADD86265FAD0A141D0A553812D43B8F2C03AA43E4B002E3"));
        Assert.That(ToHex(block2.HashTreeRoot()), Is.EqualTo("6BD5347AA1397C63ED8558079FDD3042112A5F4258066E3A659A659FF75BA14F"));
        Assert.That(ToHex(block3.HashTreeRoot()), Is.EqualTo("CE48A709189AA2B23B6858800996176DC13EB49C0C95D717C39E60042DE1AC91"));
    }

    private static Block BuildGenesisBlock(ulong genesisTime, byte keySeedStart, bool includeGenesisMarker = false)
    {
        var (block, _) = BuildGenesisArtifacts(genesisTime, keySeedStart, includeGenesisMarker);
        return block;
    }

    private static (Block Block, State State) BuildGenesisArtifacts(
        ulong genesisTime,
        byte keySeedStart,
        bool includeGenesisMarker = false)
    {
        var validators = Enumerable.Range(0, 3)
            .Select(index =>
            {
                var value = (byte)(keySeedStart + index);
                return new Validator(new Bytes52(Enumerable.Repeat(value, SszEncoding.Bytes52Length).ToArray()), (ulong)index);
            })
            .ToArray();

        var emptyBody = new BlockBody(Array.Empty<AggregatedAttestation>());
        var state = new State(
            new Config(genesisTime),
            new Slot(0),
            new BlockHeader(new Slot(0), 0, Bytes32.Zero(), Bytes32.Zero(), new Bytes32(emptyBody.HashTreeRoot())),
            Checkpoint.Default(),
            Checkpoint.Default(),
            includeGenesisMarker ? new[] { Bytes32.Zero() } : Array.Empty<Bytes32>(),
            includeGenesisMarker ? new[] { true } : Array.Empty<bool>(),
            validators,
            Array.Empty<Bytes32>(),
            Array.Empty<bool>());

        return (
            new Block(new Slot(0), 0, Bytes32.Zero(), new Bytes32(state.HashTreeRoot()), emptyBody),
            state);
    }

    private static string ToHex(byte[] bytes) => Convert.ToHexString(bytes);
}
