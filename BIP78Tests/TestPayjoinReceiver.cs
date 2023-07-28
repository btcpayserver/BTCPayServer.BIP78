using System.Collections.Concurrent;
using System.Net.WebSockets;
using BTCPayServer.BIP78.Nostr;
using BTCPayServer.BIP78.Receiver;
using NBitcoin;
using NBitcoin.Payment;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using NNostr.Client;

namespace BIP78Tests;

public class TestPayjoinReceiver : NostrPayjoinReceiver<NostrPayjoinProposalContext>
{
    private readonly DerivationStrategyBase _wallet;
    private readonly ExplorerClient _explorerClient;
    private static HashSet<OutPoint> seenInputs = new();
    private readonly ConcurrentDictionary<string,NostrPayjoinPaymentRequest> _requests = new();

    public TestPayjoinReceiver(
        DerivationStrategyBase wallet,
        ExplorerClient explorerClient,
        Uri[] relays,
        Action<WebSocket> websocketConfigure = null) : base(relays, websocketConfigure)
    {
        _wallet = wallet;
        _explorerClient = explorerClient;
            
    }

    public async Task<BitcoinUrlBuilder> CreatePaymentRequest(Money amount, DateTimeOffset expiry)
    {
        //reserve a bitcoin address
        var address = await _explorerClient.GetUnusedAsync(_wallet, DerivationFeature.Deposit, 0, true);

        var pjEndpoint = GenerateUniqueEndpoint(expiry, out var endpointKey);
        var bip21 = $"bitcoin:{address.Address}?amount={amount.ToDecimal(MoneyUnit.BTC)}&pj={pjEndpoint}";
        _requests.TryAdd(bip21,new NostrPayjoinPaymentRequest()
        {
            Amount = amount,
            Destination = address.Address,
            NostrKey = endpointKey,
            Expiry = expiry
        });
        return new BitcoinUrlBuilder(bip21, _explorerClient.Network.NBitcoinNetwork);
    }

    protected override Task<bool> SupportsType(ScriptPubKeyType scriptPubKeyType)
    {
        return Task.FromResult(scriptPubKeyType == _wallet.ScriptPubKeyType());
    }

    protected override Task<bool> InputsSeenBefore(PSBTInputList inputList)
    {
        return Task.FromResult(inputList.Any(i => !seenInputs.Add(i.PrevOut)));
    }

    protected override async Task<string> IsMempoolEligible(PSBT psbt)
    {
        try
        {
            var result = await _explorerClient.BroadcastAsync(psbt.ExtractTransaction(), true);
            return result.Success ? null : result.RPCCodeMessage;
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }

    protected override Task BroadcastOriginalTransaction(NostrPayjoinProposalContext context,
        TimeSpan scheduledTime)
    {
        if (context.PaymentRequest is NostrPayjoinPaymentRequest request)
        {
            request.ProposedTxId = context.PayjoinReceiverWalletProposal.PayjoinTransactionHash.ToString();
            request.OriginalTxId = context.OriginalTransaction.GetHash().ToString();
        }
        _ = Task.Delay(scheduledTime).ContinueWith(async task =>
        {

            var res = await _explorerClient.BroadcastAsync(context.OriginalTransaction, false);
            if (!res.Success)
            {
                var pjtx = await _explorerClient.GetTransactionAsync(_wallet,
                    context.PayjoinReceiverWalletProposal.PayjoinTransactionHash);
                var nostrPayjoinPaymentRequest = context.PaymentRequest as NostrPayjoinPaymentRequest;
                   
                if (pjtx is not null)
                {
                    _requests.TryRemove(_requests.Single(pair => pair.Value == context.PaymentRequest));
                    if (nostrPayjoinPaymentRequest is not null)
                    {
                        _activeKeys.Remove(nostrPayjoinPaymentRequest.NostrKey);
                    }
                }
                else if(nostrPayjoinPaymentRequest is not null)
                {
                    nostrPayjoinPaymentRequest.OriginalTxId = null;
                    nostrPayjoinPaymentRequest.ProposedTxId = null;
                }
            }
        });
        return Task.CompletedTask;
    }

    protected override async Task ComputePayjoinModifications(NostrPayjoinProposalContext context)
    {
        //let's just do something funky without adding inputs, say we split the payment into 2 equal outputs
        if (context.PaymentRequest.Amount.ToDecimal(MoneyUnit.BTC) == 0.1m)
        {
            var newTX = (context.OriginalPSBT).ExtractTransaction();
            var newAddr = await _explorerClient.GetUnusedAsync(_wallet, DerivationFeature.Deposit, 0, true);
            var paymentOutput = newTX.Outputs[context.OriginalPaymentRequestOutput.Index];
                
            paymentOutput.Value /= 2;
            var newPaymentOutput =  paymentOutput.Clone();
            newPaymentOutput.ScriptPubKey = newAddr.Address.ScriptPubKey;
            newTX.Outputs.Add(newPaymentOutput);
            var psbt = PSBT.FromTransaction(newTX, _explorerClient.Network.NBitcoinNetwork);
            psbt.Clone().UpdateFrom(context.OriginalPSBT).TryGetFinalizedHash(out var payjoinTxId);
            context.PayjoinReceiverWalletProposal = new PayjoinReceiverWalletProposal()
            {
                ContributedInputs = Array.Empty<ICoin>(),
                ModifiedPaymentRequest = paymentOutput,
                ContributedOutputs = new[] {newPaymentOutput},
                PayjoinTransactionHash = payjoinTxId,
                PayjoinPSBT = psbt
            };
        }
    }

    protected override Task<PayjoinPaymentRequest> FindMatchingPaymentRequests(NostrPayjoinProposalContext context)
    {
        var endpointPubKey = context.SenderEvent.GetTaggedData("p").First();
        var potentialPaymentRequests = _requests.Where(request => request.Value.Expiry > DateTimeOffset.UtcNow &&
                                                                  request.Value.NostrPubKey == endpointPubKey);
        //go through original psbt outputs and see if there is a match to the payment request address and amount
        return Task.FromResult<PayjoinPaymentRequest>(potentialPaymentRequests.FirstOrDefault(request =>
            context.OriginalTransaction.Outputs.Any(output =>
                output.ScriptPubKey == request.Value.Destination.ScriptPubKey &&
                output.Value == request.Value.Amount)).Value);
    }

    protected override async Task<NostrPayjoinProposalContext> CreateContext(NostrPayjoinRequest request,
        NostrEvent nostrEvent)
    {
        if (!PSBT.TryParse(
                request.PSBT, _explorerClient.Network.NBitcoinNetwork, out var psbt))
        {
            return null;
        }

        return new NostrPayjoinProposalContext(nostrEvent, psbt, request.Parameters);
    }
}