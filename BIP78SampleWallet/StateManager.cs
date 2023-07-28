using System.Collections.Concurrent;
using BTCPayServer.BIP78.Nostr;
using BTCPayServer.BIP78.Receiver;
using BTCPayServer.BIP78.Sender;
using NBitcoin;
using NBitcoin.Crypto;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using NNostr.Client;
using NNostr.Client.Crypto;

namespace BIP78SampleWallet;

public class StateManager : NostrPayjoinReceiver<NostrPayjoinProposalContext>, IPayjoinWallet
{
    private static HashSet<OutPoint> seenInputs = new();
    public event EventHandler? StateUpdated;
    public string NBXplorerUrl { get; private set; } = "http://localhost:32838";
    public string NostrRelayUrl { get; private set; } = "ws://localhost:65467";
    public string NetworkType { get; private set; } = ChainName.Regtest.ToString();
    public ExplorerClient? ExplorerClient { get; private set; }
    private NBXplorerNetworkProvider? NBXplorerNetworkProvider { get; set; }


    public bool AllowCoordinationModeForStaticKey { get; set; }

    public UTXO[] CoinsResult { get; private set; }

    public GetBalanceResponse? BalanceResult { get; private set; }

    public GenerateWalletResponse? Wallet { get; private set; }

    public GetTransactionsResponse? TransactionsResult { get; private set; }

    public readonly ConcurrentDictionary<string, NostrPayjoinPaymentRequest> Requests = new();


    public StateManager() : base(Array.Empty<Uri>(), null)
    {
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _ = ManageNBXConnection(cancellationToken);
        Relays = new Uri[] {new(NostrRelayUrl)};
        return base.StartAsync(cancellationToken);
    }

    private async Task ManageNBXConnection(CancellationToken cancellationToken)
    {
        NBXplorerNetworkProvider = new NBXplorerNetworkProvider(new ChainName(NetworkType));
        var explorerClient = new ExplorerClient(NBXplorerNetworkProvider.GetBTC(), new Uri(NBXplorerUrl));
        await explorerClient.WaitServerStartedAsync(cancellationToken);
        ExplorerClient = explorerClient;
        StateUpdated?.Invoke(this, EventArgs.Empty);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var session =
                    await explorerClient.CreateWebsocketNotificationSessionAsync(cancellationToken);
                await session.Client.WaitServerStartedAsync(cancellationToken);
                await session.ListenAllDerivationSchemesAsync(false, cancellationToken);
                while (!cancellationToken.IsCancellationRequested)
                {
                    var evt = await session.NextEventAsync(cancellationToken);
                    if (evt is NewTransactionEvent transactionEvent &&
                        Wallet?.DerivationScheme == transactionEvent.DerivationStrategy)
                    {
                        var transactionHashStr = transactionEvent.TransactionData.TransactionHash.ToString();

// Create a lookup dictionary for easy lookup based on address.
                        var lookup = transactionEvent.Outputs.ToDictionary(output => output.Address.ToString(), output => output.Value);

                        var matchedRequests = Requests.Select(pair =>
                        {
                            bool isOriginalTxIdMatch = pair.Value.OriginalTxId == transactionHashStr;
                            bool isProposedTxIdMatch = pair.Value.ProposedTxId == transactionHashStr;

                            // Check if lookup contains the destination and if the amount is sufficient
                            bool hasSufficientAmount = lookup.TryGetValue(pair.Value.Destination.ToString(), out var outputMatch) 
                                                       && outputMatch is Money outputMatchMoney 
                                                       && outputMatchMoney >= pair.Value.Amount;

                            if (isOriginalTxIdMatch || isProposedTxIdMatch || hasSufficientAmount)
                            {
                                return pair.Value;
                            }
                            return null;

                        }).OfType<NostrPayjoinPaymentRequest>().ToArray();

                        foreach (var matchedRequest in matchedRequests)
                        {
                            matchedRequest.DetectedTxId = transactionHashStr;
                            _activeKeys.Remove(matchedRequest.NostrKey);
                        }

                        await UpdateBalance();
                        StateUpdated?.Invoke(this, EventArgs.Empty);
                    }else if (evt is NewBlockEvent)
                    {
                        await UpdateBalance();
                        StateUpdated?.Invoke(this, EventArgs.Empty);
                    }
                    
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }
    }

    public async Task UpdateBalance()
    {
        if (ExplorerClient is null || Wallet is null)
            return;
        BalanceResult = await ExplorerClient.GetBalanceAsync(Wallet.DerivationScheme);
        var utxoResult = await ExplorerClient.GetUTXOsAsync(Wallet.DerivationScheme);
        CoinsResult = utxoResult.GetUnspentUTXOs();
        TransactionsResult = await ExplorerClient.GetTransactionsAsync(Wallet.DerivationScheme);
        StateUpdated?.Invoke(this, EventArgs.Empty);
    }

    public async Task GenerateWallet()
    {
        if (ExplorerClient is null)
            return;
        Wallet ??= await ExplorerClient.GenerateWalletAsync(new GenerateWalletRequest()
        {
            SavePrivateKeys = true,
            ScriptPubKeyType = ScriptPubKeyType.Segwit
        });
        StateUpdated?.Invoke(this, EventArgs.Empty);
    }

    public async Task MineBlock()
    {
        if (ExplorerClient is null)
            return;

        await ExplorerClient.RPCClient.GenerateAsync(1);

        StateUpdated?.Invoke(this, EventArgs.Empty);
    }

    protected override async Task<bool> SupportsType(ScriptPubKeyType scriptPubKeyType)
    {
        return scriptPubKeyType == Wallet?.DerivationScheme.ScriptPubKeyType();
    }

    protected override Task<bool> InputsSeenBefore(PSBTInputList inputList)
    {
        return Task.FromResult(inputList.Any(i => !seenInputs.Add(i.PrevOut)));
    }

    protected override async Task<string> IsMempoolEligible(PSBT psbt)
    {
        try
        {
            var result = await ExplorerClient.BroadcastAsync(psbt.ExtractTransaction(), true);
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
            var res = await ExplorerClient.BroadcastAsync(context.OriginalTransaction, false);
            if (!res.Success)
            {
                var pjtx = await ExplorerClient.GetTransactionAsync(Wallet.DerivationScheme,
                    context.PayjoinReceiverWalletProposal.PayjoinTransactionHash);
                var nostrPayjoinPaymentRequest = context.PaymentRequest as NostrPayjoinPaymentRequest;

                if (pjtx is not null)
                {
                    if (nostrPayjoinPaymentRequest != null)
                    {
                        nostrPayjoinPaymentRequest.DetectedTxId =
                            context.PayjoinReceiverWalletProposal.PayjoinTransactionHash.ToString();
                        _activeKeys.Remove(nostrPayjoinPaymentRequest.NostrKey);
                    }
                }
                else if (nostrPayjoinPaymentRequest is not null)
                {
                    nostrPayjoinPaymentRequest.OriginalTxId = null;
                    nostrPayjoinPaymentRequest.ProposedTxId = null;
                }
                StateUpdated?.Invoke(this, EventArgs.Empty);
            }
        });
        return Task.CompletedTask;
    }

    public static decimal IdentifyLikelyPayment(decimal[] outputs)
    {
        if (outputs == null || !outputs.Any()) throw new ArgumentException("The list cannot be empty or null.");

        // If there's only one output, it's clearly the payment.
        if (outputs.Length == 1) return outputs[0];

        // Order by number of decimal places (or non-zero digits after decimal point)
        var leastDecimalPlaces = outputs.OrderBy(n => CountDecimalPlaces(n)).First();

        // This is considered as the likely payment based on its "roundedness".
        return leastDecimalPlaces;
    }

    private static int CountDecimalPlaces(decimal d)
    {
        int count = 0;
        decimal multiplier = 1m;
        while (Math.Round(d * multiplier) != d * multiplier)
        {
            multiplier *= 10m;
            count++;
        }
        return count;
    }

    protected override async Task ComputePayjoinModifications(NostrPayjoinProposalContext context)
    {
        if (!context.OriginalPSBT.TryGetEstimatedFeeRate(out var originalFeeRate))
        {
            throw new PayjoinReceiverException("original-psbt-rejected",
                "You need to provide Witness UTXO information to the PSBT.");
        }

        if (context.PaymentRequest is NostrPayjoinPaymentRequest nostrPayjoinPaymentRequest)
        {
            //this is a maker request.
            if (nostrPayjoinPaymentRequest.NostrKey == StaticKey.Value && nostrPayjoinPaymentRequest.Amount is null && nostrPayjoinPaymentRequest.Destination is null)
            {
                await MakerPayjoinproposal(context, nostrPayjoinPaymentRequest, originalFeeRate);
                return;
            }
        }

        //let's just do something funky without adding inputs, say we split the payment into 2 equal outputs
        var newTX = (context.OriginalPSBT).ExtractTransaction();
        var newAddr = await ExplorerClient.GetUnusedAsync(Wallet.DerivationScheme, DerivationFeature.Deposit, 0, true);
        var paymentOutput = newTX.Outputs[context.OriginalPaymentRequestOutput.Index];

        paymentOutput.Value /= 2;
        var newPaymentOutput = paymentOutput.Clone();
        newPaymentOutput.ScriptPubKey = newAddr.Address.ScriptPubKey;
        newTX.Outputs.Add(newPaymentOutput);
        var psbt = PSBT.FromTransaction(newTX, ExplorerClient.Network.NBitcoinNetwork);
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

    private async Task MakerPayjoinproposal(NostrPayjoinProposalContext context,
        NostrPayjoinPaymentRequest nostrPayjoinPaymentRequest, FeeRate originalFeeRate)
    {
        var totalAvailable = (CoinsResult.Sum(utxo => utxo.Value as Money));
        var coinsLeft = CoinsResult.ToList();
        var amountToMatch = IdentifyLikelyPayment(context.OriginalPSBT.Outputs
            .Select(output => output.Value.ToDecimal(MoneyUnit.BTC)).ToArray());
        if (totalAvailable < amountToMatch)
        {
            return;
        }

        var newTx = context.OriginalTransaction.Clone();

        foreach (var input in newTx.Inputs)
        {
            input.WitScript = WitScript.Empty;
        }

        var txBuilder = ExplorerClient.Network.NBitcoinNetwork.CreateTransactionBuilder();
        txBuilder.AddCoins(context.OriginalPSBT.Inputs.Select(input => input.GetSignableCoin()));
        var paymentAddress =
            await ExplorerClient.GetUnusedAsync(Wallet.DerivationScheme, DerivationFeature.Deposit, 0, true);
        var changeAddress =
            await ExplorerClient.GetUnusedAsync(Wallet.DerivationScheme, DerivationFeature.Change, 0, true);

        var paymentOutput = newTx.Outputs.Add(new Money(amountToMatch, MoneyUnit.BTC), paymentAddress.Address);
        TxOut changeOutput = null; // newTx.Outputs.Add(new Money(0, MoneyUnit.BTC), changeAddress.Address);
        
        var selectedUtxos = new List<UTXO>();

        var senderInputCount = newTx.Inputs.Count;

        var expectedFee = txBuilder.EstimateFees(newTx, originalFeeRate).ToDecimal(MoneyUnit.BTC);
        var actualFee = newTx.GetFee(txBuilder.FindSpentCoins(newTx)).ToDecimal(MoneyUnit.BTC);
        while (CoinsResult.Any() && actualFee < expectedFee)
        {
            var coinIndex = Random.Shared.Next(0, coinsLeft.Count());
            var coin = coinsLeft.ElementAt(coinIndex);
            coinsLeft.RemoveAt(coinIndex);
            selectedUtxos.Add(coin);
            var newInput = newTx.Inputs.Add(coin.Outpoint);
            newInput.Sequence = newTx.Inputs[(int) (RandomUtils.GetUInt32() % senderInputCount)].Sequence;
            txBuilder.AddCoins(coin.AsCoin(Wallet.DerivationScheme));

            expectedFee = txBuilder.EstimateFees(newTx, originalFeeRate).ToDecimal(MoneyUnit.BTC);
            actualFee = newTx.GetFee(txBuilder.FindSpentCoins(newTx)).ToDecimal(MoneyUnit.BTC);
            if (actualFee > expectedFee && changeOutput is null)
            {
                changeOutput = newTx.Outputs.Add(new Money(actualFee - expectedFee, MoneyUnit.BTC),
                    changeAddress.Address); 

                var newExpectedFee = txBuilder.EstimateFees(newTx, originalFeeRate).ToDecimal(MoneyUnit.BTC);
                var diff = newExpectedFee - expectedFee;
                changeOutput.Value -= new Money(diff, MoneyUnit.BTC);

                expectedFee = txBuilder.EstimateFees(newTx, originalFeeRate).ToDecimal(MoneyUnit.BTC);
                actualFee = newTx.GetFee(txBuilder.FindSpentCoins(newTx)).ToDecimal(MoneyUnit.BTC);
            }
            else if (actualFee > expectedFee && changeOutput is not null)
            {
                changeOutput.Value += new Money(actualFee - expectedFee, MoneyUnit.BTC);
            }

        }

        if (actualFee < expectedFee)
        {
            return;
        }
        
        var enforcedLowR = context.OriginalTransaction.Inputs.All(IsLowR);
        var newPsbt = PSBT.FromTransaction(newTx, ExplorerClient.Network.NBitcoinNetwork);
        foreach (var selectedUtxo in selectedUtxos)
        {
            var signedInput = newPsbt.Inputs.FindIndexedInput(selectedUtxo.Outpoint);
            var coin = selectedUtxo.AsCoin(Wallet.DerivationScheme);
            if (signedInput is not null)
            {
                signedInput.UpdateFromCoin(coin);
                var privateKey = Wallet.AccountHDKey.Derive(selectedUtxo.KeyPath).PrivateKey;
                signedInput.PSBT.Settings.SigningOptions = new SigningOptions()
                {
                    EnforceLowR = enforcedLowR
                };
                signedInput.Sign(privateKey);
                signedInput.FinalizeInput();
                newTx.Inputs[signedInput.Index].WitScript = newPsbt.Inputs[(int)signedInput.Index].FinalScriptWitness;
            }
        }

        context.PayjoinReceiverWalletProposal = new PayjoinReceiverWalletProposal()
        {
            ContributedInputs = selectedUtxos.Select(utxo => utxo.AsCoin(Wallet.DerivationScheme)).ToArray(),
            ContributedOutputs = changeOutput is null ? new[] {paymentOutput} : new[] {paymentOutput, changeOutput},
            ModifiedPaymentRequest = null,
            PayjoinTransactionHash = newTx.GetHash(),
            PayjoinPSBT = newPsbt,
            ExtraFeeFromAdditionalFeeOutput = Money.Zero,
        };
        context.PayjoinReceiverWalletProposal.ExtraFeeFromReceiverInputs = context.PayjoinReceiverWalletProposal.ContributedInputs.Sum(utxo => (Money) utxo.Amount)- context.PayjoinReceiverWalletProposal.ContributedOutputs.Sum(output => output.Value);
    }
    private static bool IsLowR(TxIn txin)
    {
        var pushes = txin.WitScript.PushCount > 0 ? txin.WitScript.Pushes :
            txin.ScriptSig.IsPushOnly ? txin.ScriptSig.ToOps().Select(o => o.PushData) :
            Array.Empty<byte[]>();
        return pushes.Where(ECDSASignature.IsValidDER).All(p => p.Length <= 71);
    }

    protected override Task<INostrClient> CreateNostrClient()
    {
        return Task.FromResult<INostrClient>(new NostrClient(new Uri(NostrRelayUrl)));
    }

    protected override async Task<PayjoinPaymentRequest> FindMatchingPaymentRequests(
        NostrPayjoinProposalContext context)
    {
        var endpointPubKey = context.SenderEvent.GetTaggedData("p").First();

        var potentialPaymentRequests = Requests.Where(request =>
            (request.Value.Expiry is null || request.Value.Expiry > DateTimeOffset.UtcNow) &&
            request.Value.NostrPubKey == endpointPubKey && request.Value is {DetectedTxId: null, ProposedTxId: null});
        //go through original psbt outputs and see if there is a match to the payment request address and amount
        var matched = potentialPaymentRequests.FirstOrDefault(request =>
            context.OriginalTransaction.Outputs.Any(output =>
                output.ScriptPubKey == request.Value.Destination.ScriptPubKey &&
                (request.Value.Amount is null || output.Value >= request.Value.Amount))).Value;

        if (matched is not null)
        {
            return matched;
        }

        if (AllowCoordinationModeForStaticKey &&
            StaticKey.Value.CreateXOnlyPubKey().ToHex() == endpointPubKey)
        {
            return new NostrPayjoinPaymentRequest()
            {
                NostrKey = StaticKey.Value,
                Amount = null,
                Destination = null
            };
        }

        return null;
    }

    public override IAesEncryption GetAesEncryption()
    {
        return Encryptor;
    }

    protected override async Task<NostrPayjoinProposalContext> CreateContext(NostrPayjoinRequest request,
        NostrEvent nostrEvent)
    {
        var endpointPubKey = nostrEvent.GetTaggedData("p").First();
        var maker =
            !(AllowCoordinationModeForStaticKey && endpointPubKey == StaticKey.Value.CreateXOnlyPubKey().ToHex());
        return !PSBT.TryParse(
            request.PSBT, ExplorerClient.Network.NBitcoinNetwork, out var psbt)
            ? null
            : new NostrPayjoinProposalContext(nostrEvent, psbt, request.Parameters);
    }

    public IHDScriptPubKey Derive(KeyPath keyPath)
    {
        return ((IHDScriptPubKey) Wallet.DerivationScheme).Derive(keyPath);
    }

    public bool CanDeriveHardenedPath()
    {
        return Wallet.DerivationScheme.CanDeriveHardenedPath();
    }

    public Script ScriptPubKey => ((IHDScriptPubKey) Wallet.DerivationScheme).ScriptPubKey;
    public ScriptPubKeyType ScriptPubKeyType => Wallet.DerivationScheme.ScriptPubKeyType();

    public RootedKeyPath RootedKeyPath => Wallet.AccountKeyPath;

    public IHDKey AccountKey => Wallet.AccountHDKey;
    public static WasmAES.WasmAESEncryptor Encryptor { get; set; }
}