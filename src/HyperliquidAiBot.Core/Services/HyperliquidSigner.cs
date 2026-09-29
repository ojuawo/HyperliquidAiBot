using System.Buffers.Binary;
using HyperliquidAiBot.Core.Models;
using MessagePack;
using MessagePack.Resolvers;
using Nethereum.ABI.EIP712;
using Nethereum.Signer;
using Nethereum.Signer.EIP712;
using Nethereum.Util;

namespace HyperliquidAiBot.Core.Services;

public interface IHyperliquidSigner
{
    byte[] ComputeActionHash(OrderAction action, ulong nonce, string? vaultAddress = null, ulong? expiresAfter = null);
    HyperliquidSignature SignAction(OrderAction action, ulong nonce, bool isMainnet, string? vaultAddress = null);
    string GetSignerAddress();
}

/// <summary>
/// Cryptographic EIP-712 order signer for Hyperliquid L1 Phantom Agent transactions.
/// </summary>
public class HyperliquidSigner : IHyperliquidSigner
{
    private readonly EthECKey _key;
    private readonly string _walletAddress;
    private readonly Eip712TypedDataSigner _eip712Signer;
    private readonly MessagePackSerializerOptions _msgPackOptions;

    public HyperliquidSigner(string privateKeyHex)
    {
        if (string.IsNullOrWhiteSpace(privateKeyHex))
        {
            throw new ArgumentException("Ethereum private key must be provided", nameof(privateKeyHex));
        }

        var cleanKey = privateKeyHex.Trim();
        if (cleanKey.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            cleanKey = cleanKey[2..];
        }

        _key = new EthECKey(cleanKey);
        _walletAddress = _key.GetPublicAddress();
        _eip712Signer = new Eip712TypedDataSigner();

        _msgPackOptions = MessagePackSerializerOptions.Standard
            .WithResolver(ContractlessStandardResolver.Instance)
            .WithOmitAssemblyVersion(true);
    }

    public string GetSignerAddress() => _walletAddress;

    /// <summary>
    /// Computes the Keccak-256 hash of (msgpack(action) + nonce(8 bytes) + vault(optional) + expiresAfter(optional)).
    /// </summary>
    public byte[] ComputeActionHash(OrderAction action, ulong nonce, string? vaultAddress = null, ulong? expiresAfter = null)
    {
        var actionBytes = MessagePackSerializer.Serialize(action, _msgPackOptions);

        using var ms = new MemoryStream();
        ms.Write(actionBytes, 0, actionBytes.Length);

        // 8 bytes big-endian nonce
        Span<byte> nonceBytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(nonceBytes, nonce);
        ms.Write(nonceBytes);

        // Vault address handling
        if (string.IsNullOrWhiteSpace(vaultAddress))
        {
            ms.WriteByte(0x00);
        }
        else
        {
            ms.WriteByte(0x01);
            var cleanAddress = vaultAddress.Trim();
            if (cleanAddress.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                cleanAddress = cleanAddress[2..];
            }
            var addressBytes = Convert.FromHexString(cleanAddress);
            ms.Write(addressBytes, 0, addressBytes.Length);
        }

        // Optional expiration
        if (expiresAfter.HasValue)
        {
            ms.WriteByte(0x00);
            Span<byte> expiresBytes = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(expiresBytes, expiresAfter.Value);
            ms.Write(expiresBytes);
        }

        var fullPayload = ms.ToArray();
        return new Sha3Keccack().CalculateHash(fullPayload);
    }

    /// <summary>
    /// Signs an action using the Hyperliquid Phantom Agent EIP-712 standard.
    /// </summary>
    public HyperliquidSignature SignAction(OrderAction action, ulong nonce, bool isMainnet, string? vaultAddress = null)
    {
        var actionHash = ComputeActionHash(action, nonce, vaultAddress);
        var source = isMainnet ? "a" : "b";

        var typedData = CreatePhantomAgentTypedData(source, actionHash);
        var signatureHex = _eip712Signer.SignTypedDataV4(typedData, _key);

        return ParseSignature(signatureHex);
    }

    public TypedData<Domain> CreatePhantomAgentTypedData(string source, byte[] connectionId)
    {
        return new TypedData<Domain>
        {
            Domain = new Domain
            {
                Name = "Exchange",
                Version = "1",
                ChainId = 1337,
                VerifyingContract = "0x0000000000000000000000000000000000000000"
            },
            Types = new Dictionary<string, MemberDescription[]>
            {
                ["EIP712Domain"] = new[]
                {
                    new MemberDescription { Name = "name", Type = "string" },
                    new MemberDescription { Name = "version", Type = "string" },
                    new MemberDescription { Name = "chainId", Type = "uint256" },
                    new MemberDescription { Name = "verifyingContract", Type = "address" }
                },
                ["Agent"] = new[]
                {
                    new MemberDescription { Name = "source", Type = "string" },
                    new MemberDescription { Name = "connectionId", Type = "bytes32" }
                }
            },
            PrimaryType = "Agent",
            Message = new[]
            {
                new MemberValue { TypeName = "string", Value = source },
                new MemberValue { TypeName = "bytes32", Value = connectionId }
            }
        };
    }

    private static HyperliquidSignature ParseSignature(string sigHex)
    {
        if (sigHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            sigHex = sigHex[2..];
        }

        if (sigHex.Length != 130)
        {
            throw new FormatException($"Invalid signature length: {sigHex.Length}. Expected 130 hex characters.");
        }

        var r = "0x" + sigHex.Substring(0, 64);
        var s = "0x" + sigHex.Substring(64, 64);
        var v = Convert.ToInt32(sigHex.Substring(128, 2), 16);

        if (v < 27)
        {
            v += 27;
        }

        return new HyperliquidSignature(r, s, v);
    }
}
