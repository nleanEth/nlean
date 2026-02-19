using Lean.Crypto;
using NUnit.Framework;

namespace Lean.Crypto.Tests;

public sealed class ObservedProofInteropDiagnosticsTests
{
    [Test]
    public void DumpedObservedProofsReportVerificationOutcome()
    {
        var runDir = Environment.GetEnvironmentVariable("NLEAN_QUICKSTART_RUN_DIR")
            ?? "/Users/grapebaba/conductor/workspaces/zeam/lima-v1/lean-quickstart/local-devnet-nlean";
        var proofsDir = Environment.GetEnvironmentVariable("NLEAN_OBSERVED_PROOFS_DIR")
            ?? Path.Combine(runDir, "data", "nlean_0", "observed-proofs");
        var keysDir = Environment.GetEnvironmentVariable("NLEAN_GENESIS_KEYS_DIR")
            ?? Path.Combine(runDir, "genesis", "hash-sig-keys");

        if (!Directory.Exists(proofsDir) || !Directory.Exists(keysDir))
        {
            Assert.Ignore("Observed proof diagnostics inputs are missing.");
            return;
        }

        var metadataFiles = Directory.GetFiles(proofsDir, "*.txt", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (metadataFiles.Length == 0)
        {
            Assert.Ignore("No observed proof metadata files found.");
            return;
        }

        var multisig = new RustLeanMultiSig();
        multisig.SetupVerifier();

        var checkedAny = false;
        var invalidCount = 0;
        foreach (var metadataPath in metadataFiles)
        {
            var data = ParseMetadata(metadataPath);
            var participants = ParseParticipants(data["participants"]);
            if (participants.Count == 0)
            {
                continue;
            }

            var dataRoot = Convert.FromHexString(data["data_root"]);
            var attestationSlot = uint.Parse(data["attestation_slot"]);
            var proofPath = Path.ChangeExtension(metadataPath, ".bin");
            var proofBytes = File.ReadAllBytes(proofPath);
            var publicKeys = participants
                .Select(id => Path.Combine(keysDir, $"validator_{id}_pk.ssz"))
                .Select(path => new ReadOnlyMemory<byte>(File.ReadAllBytes(path)))
                .ToList();

            var ok = multisig.VerifyAggregate(publicKeys, dataRoot, proofBytes, attestationSlot);
            var reversedDataRoot = dataRoot.Reverse().ToArray();
            var okReversed = multisig.VerifyAggregate(publicKeys, reversedDataRoot, proofBytes, attestationSlot);
            checkedAny = true;
            if (!ok)
            {
                invalidCount++;
            }

            TestContext.WriteLine(
                $"observed={Path.GetFileNameWithoutExtension(metadataPath)} slot={attestationSlot} participants=[{string.Join(",", participants)}] ok={ok} ok_reversed_root={okReversed}");
        }

        Assert.That(checkedAny, Is.True, "No observed proofs were checked.");
        TestContext.WriteLine($"invalid_count={invalidCount}");
    }

    private static Dictionary<string, string> ParseMetadata(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(path))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            map[key] = value;
        }

        return map;
    }

    private static List<ulong> ParseParticipants(string participantsValue)
    {
        var trimmed = participantsValue.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
        {
            trimmed = trimmed[1..^1];
        }

        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return new List<ulong>();
        }

        return trimmed
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ulong.Parse)
            .ToList();
    }
}
