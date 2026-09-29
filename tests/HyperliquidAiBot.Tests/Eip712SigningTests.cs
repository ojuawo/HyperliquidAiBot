using HyperliquidAiBot.Core.Models;
using HyperliquidAiBot.Core.Services;
using Nethereum.Signer.EIP712;
using Nethereum.Util;
using Xunit;

namespace HyperliquidAiBot.Tests;

public class Eip712SigningTests
{
    private const string TestPrivateKey = "0x4c0883a69102937d6231471b5dbb6204fe5129617082792ae468d01a3f36088a";

    [Fact]
    public void Signer_InitializesWithCorrectSignerAddress()
    {
        var signer = new HyperliquidSigner(TestPrivateKey);
        var address = signer.GetSignerAddress();

        Assert.NotNull(address);
        Assert.StartsWith("0x", address);
        Assert.Equal(42, address.Length);
    }

    [Fact]
    public void ComputeActionHash_ProducesValid32ByteKeccakHash()
    {
        var signer = new HyperliquidSigner(TestPrivateKey);

        var action = new OrderAction(
            Type: "order",
            Orders: new List<OrderWire>
            {
                new(
                    Asset: 0,
                    IsBuy: true,
                    LimitPx: "50000.0",
                    Sz: "0.01",
                    ReduceOnly: false,
                    OrderType: new OrderTypeWire(Limit: new LimitOrderTypeWire("Gtc"))
                )
            },
            Grouping: "na"
        );

        var hash = signer.ComputeActionHash(action, 1700000000000UL);

        Assert.NotNull(hash);
        Assert.Equal(32, hash.Length);
    }

    [Theory]
    [InlineData(true, "a")]   // Mainnet -> 'a'
    [InlineData(false, "b")]  // Testnet -> 'b'
    public void SignAction_GeneratesValidEip712SignatureAndRecoversExpectedAddress(bool isMainnet, string expectedSource)
    {
        var signer = new HyperliquidSigner(TestPrivateKey);
        var expectedAddress = signer.GetSignerAddress();

        var action = new OrderAction(
            Type: "order",
            Orders: new List<OrderWire>
            {
                new(
                    Asset: 0, // BTC
                    IsBuy: true,
                    LimitPx: "65000.0",
                    Sz: "0.02",
                    ReduceOnly: false,
                    OrderType: new OrderTypeWire(Limit: new LimitOrderTypeWire("Gtc"))
                )
            },
            Grouping: "na"
        );

        var nonce = 1715000000000UL;
        var signature = signer.SignAction(action, nonce, isMainnet);

        // Assert signature structure
        Assert.NotNull(signature);
        Assert.StartsWith("0x", signature.R);
        Assert.StartsWith("0x", signature.S);
        Assert.Equal(66, signature.R.Length); // 0x + 64 hex chars
        Assert.Equal(66, signature.S.Length); // 0x + 64 hex chars
        Assert.True(signature.V is 27 or 28, $"Expected v to be 27 or 28, got {signature.V}");

        // Reconstruct TypedData and verify recovery using Nethereum
        var actionHash = signer.ComputeActionHash(action, nonce);
        var typedData = signer.CreatePhantomAgentTypedData(expectedSource, actionHash);

        var fullSigHex = signature.R + signature.S[2..] + signature.V.ToString("x2");
        var eip712Verifier = new Eip712TypedDataSigner();
        var recoveredAddress = eip712Verifier.RecoverFromSignatureV4(typedData, fullSigHex);

        Assert.True(expectedAddress.IsTheSameAddress(recoveredAddress),
            $"Recovered address {recoveredAddress} did not match expected signer {expectedAddress}");
    }
}
