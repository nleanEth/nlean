using NUnit.Framework;

namespace Lean.Network.Tests;

public sealed class LeanStatusWireFormatTests
{
    [Test]
    public void EncodeStatus_MatchesReamWireLayout()
    {
        var finalizedRoot = Enumerable.Range(0, LeanStatusMessage.RootLength).Select(i => (byte)i).ToArray();
        var headRoot = Enumerable.Range(0, LeanStatusMessage.RootLength).Select(i => (byte)(i + 32)).ToArray();

        var status = new LeanStatusMessage(
            finalizedRoot,
            finalizedSlot: 0x0807060504030201,
            headRoot,
            headSlot: 0x11100F0E0D0C0B0A);

        var encoded = LeanReqRespCodec.EncodeStatus(status);

        const string expectedHex =
            "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F" +
            "0102030405060708" +
            "202122232425262728292A2B2C2D2E2F303132333435363738393A3B3C3D3E3F" +
            "0A0B0C0D0E0F1011";

        Assert.That(Convert.ToHexString(encoded), Is.EqualTo(expectedHex));
    }
}
