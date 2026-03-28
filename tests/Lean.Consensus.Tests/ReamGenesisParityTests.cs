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

        Assert.That(ToHex(state1.HashTreeRoot()), Is.EqualTo("245E775429742F7B7223017C56D68FAC1EBD91ACD56DD2D0C65237D07B91566B"));
        Assert.That(ToHex(block1.HashTreeRoot()), Is.EqualTo("F84D547A47CA863FAC7CDA4619D3A93A2D3E7F2AFDEEB5E4571B393554E19C0D"));
        Assert.That(ToHex(block2.HashTreeRoot()), Is.EqualTo("7B90004279C32942009320F284A92C8EC5914E9C4DEB7A9C50E17DC22A7C6CE9"));
        Assert.That(ToHex(block3.HashTreeRoot()), Is.EqualTo("B66CB6371BDE0209FFD63063F89D216FEEB1F03328400CB083429D8AEAD481FF"));
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
                return new Validator(new Bytes52(Enumerable.Repeat(value, SszEncoding.Bytes52Length).ToArray()), new Bytes52(Enumerable.Repeat(value, SszEncoding.Bytes52Length).ToArray()), (ulong)index);
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
