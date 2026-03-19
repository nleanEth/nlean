using System.Security.Cryptography;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;

namespace Lean.Network;

public static class Libp2pIdentityFactory
{
    private const int Secp256k1PrivateKeySize = 32;

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

        // Default to secp256k1 so generated peer IDs align with other lean clients.
        var generatedKeyBytes = RandomNumberGenerator.GetBytes(Secp256k1PrivateKeySize);
        return new Identity(EncodeSecpPrivateKeyForLibp2p(generatedKeyBytes), KeyType.Secp256K1);
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

        if (privateKeyBytes.Length != Secp256k1PrivateKeySize)
        {
            throw new InvalidOperationException(
                $"Invalid secp256k1 private key length in {source}: expected {Secp256k1PrivateKeySize} bytes, got {privateKeyBytes.Length} bytes.");
        }

        return new Identity(EncodeSecpPrivateKeyForLibp2p(privateKeyBytes), KeyType.Secp256K1);
    }

    private static byte[] EncodeSecpPrivateKeyForLibp2p(byte[] privateKeyBytes)
    {
        ArgumentNullException.ThrowIfNull(privateKeyBytes);
        if (privateKeyBytes.Length != Secp256k1PrivateKeySize)
        {
            throw new InvalidOperationException(
                $"Invalid secp256k1 private key length: expected {Secp256k1PrivateKeySize} bytes, got {privateKeyBytes.Length} bytes.");
        }

        // Nethermind.Libp2p currently parses secp256k1 private keys via a signed
        // BigInteger constructor. Prefix high-bit keys with 0x00 so they are
        // interpreted as positive scalars and match expected peer IDs.
        if ((privateKeyBytes[0] & 0x80) == 0)
        {
            return privateKeyBytes;
        }

        var encoded = new byte[Secp256k1PrivateKeySize + 1];
        Buffer.BlockCopy(privateKeyBytes, 0, encoded, 1, Secp256k1PrivateKeySize);
        return encoded;
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
