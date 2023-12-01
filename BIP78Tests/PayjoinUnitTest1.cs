using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.BIP78.Nostr;
using BTCPayServer.BIP78.Sender;
using NBitcoin;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using Xunit;

namespace BIP78Tests;

public class PayjoinUnitTest1
{
    [Fact]
    public async Task UseNostrPayjoin()
    {
        var relay = new[] {new Uri("ws://localhost:65467")};
        var nbxNetwork = new NBXplorerNetworkProvider(ChainName.Regtest);
        var explorerClient = new ExplorerClient(nbxNetwork.GetBTC(), new Uri("http://localhost:32838"));
        await explorerClient.WaitServerStartedAsync();
        
        var receiverWallet = await explorerClient.GenerateWalletAsync(new GenerateWalletRequest()
        {
            SavePrivateKeys = true,
            ScriptPubKeyType = ScriptPubKeyType.Segwit
        });
        var senderWallet = await explorerClient.GenerateWalletAsync(new GenerateWalletRequest()
        {
            SavePrivateKeys = true,
            ScriptPubKeyType = ScriptPubKeyType.Segwit
        });
        var senderPayjoinWallet = new PayjoinWallet(senderWallet);


        //create the payjoin receiver handler
        var receiver = new TestPayjoinReceiver(receiverWallet.DerivationScheme, explorerClient, relay);

        //start listening for nostr events
        var receiverTask = receiver.StartAsync(CancellationToken.None);


        var bip21 = await receiver.CreatePaymentRequest(new Money(0.1m, MoneyUnit.BTC),
            DateTimeOffset.UtcNow.AddMinutes(5));

        PayjoinClient payjoinClient;
        if (bip21.UnknownParameters.TryGetValue("pj", out var pje) && pje.StartsWith("nostr:"))
        {
            payjoinClient = new PayjoinClient(new NostrPayjoinServerCommunicator(relay));
        }
        else
        {
            payjoinClient = new PayjoinClient();
        }

        var senderAddress =
            await explorerClient.GetUnusedAsync(senderWallet.DerivationScheme, DerivationFeature.Deposit, 0, true);
        await explorerClient.RPCClient.SendToAddressAsync(senderAddress.Address, Money.FromUnit(1m, MoneyUnit.BTC));
        await explorerClient.RPCClient.GenerateToAddressAsync(2, senderAddress.Address);
        while (true)
        {
            var b = await explorerClient.GetBalanceAsync(senderWallet.DerivationScheme);
            if (b.Available is Money bm && bm.Satoshi != 0)
                break;
        }
        var senderPSBT = await explorerClient.CreatePSBTAsync(senderWallet.DerivationScheme, new CreatePSBTRequest()
        {
            MinConfirmations = 0,
            FeePreference = new FeePreference()
            {
                ExplicitFeeRate = new FeeRate(5m)
            },
            Destinations = new List<CreatePSBTDestination>()
            {
                new CreatePSBTDestination()
                {
                    Amount = bip21.Amount,
                    Destination = bip21.Address
                }
            },
        });
        var signedSenderPsbt = senderPSBT.PSBT.SignAll(senderWallet.DerivationScheme,
            senderWallet.AccountHDKey, senderWallet.AccountKeyPath);
        var payjoinResponse = await payjoinClient.RequestPayjoin(bip21, senderPayjoinWallet, signedSenderPsbt,
            CancellationToken.None);
        var finalPSBT = payjoinResponse.SignAll(senderWallet.DerivationScheme,
            senderWallet.AccountHDKey, senderWallet.AccountKeyPath).Finalize();

        var result = await explorerClient.BroadcastAsync(finalPSBT.ExtractTransaction(), false);
        
        Assert.True(result.Success);
    }
}