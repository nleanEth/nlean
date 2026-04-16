using System.Security.Cryptography;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;

namespace Lean.Network;

public static class Libp2pIdentityFactory
{
    private const int Ed25519SeedSize = 32;

    public static Identity Create(Libp2pConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!string.IsNullOrWhiteSpace(config.PrivateKeyHex))
        {
            return CreateFromHex(config.PrivateKeyHex, "libp2p.privateKeyHex");
        }

        if (!string.IsNullOrWhiteSpace(config.PrivateKeyPath))
        {
            var path = config.PrivateKeyPath.Trim();
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"libp2p private key file not found: {path}", path);
            }

            var privateKeyHex = File.ReadAllText(path);
            return CreateFromHex(privateKeyHex, $"libp2p.privateKeyPath ({path})");
        }

        var generatedKeyBytes = RandomNumberGenerator.GetBytes(Ed25519SeedSize);
        return new Identity(generatedKeyBytes, KeyType.Ed25519);
    }

    public static Identity CreateFromHex(string privateKeyHex, string source)
    {
        var normalizedHex = NormalizeHex(privateKeyHex, source);
        byte[] privateKeyBytes;
        try
        {
            privateKeyBytes = Convert.FromHexString(normalizedHex);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"Invalid hex in {source}.", ex);
        }

        if (privateKeyBytes.Length != Ed25519SeedSize)
        {
            throw new InvalidOperationException(
                $"Invalid ed25519 private key length in {source}: expected {Ed25519SeedSize} bytes, got {privateKeyBytes.Length} bytes.");
        }

        return new Identity(privateKeyBytes, KeyType.Ed25519);
    }

    private static string NormalizeHex(string input, string source)
    {
        var normalizedHex = input.Trim();
        if (normalizedHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalizedHex = normalizedHex[2..];
        }

        if (string.IsNullOrWhiteSpace(normalizedHex))
        {
            throw new InvalidOperationException($"Missing private key hex in {source}.");
        }

        return normalizedHex;
    }
}
