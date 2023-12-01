using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.Payment;

namespace BTCPayServer.BIP78.Sender
{
    public class PayjoinClient
    {
        private readonly IPayjoinServerCommunicator _payjoinServerCommunicator;

        public const string BIP21EndpointKey = "pj";

        public PayjoinClient(IPayjoinServerCommunicator payjoinServerCommunicator)
        {
            _payjoinServerCommunicator = payjoinServerCommunicator;
        }

        public PayjoinClient()
        {
            _payjoinServerCommunicator = new HttpClientPayjoinServerCommunicator();
        }

        public Money MaxFeeBumpContribution { get; set; }
        public FeeRate MinimumFeeRate { get; set; }

        public async Task<PSBT> RequestPayjoin(BitcoinUrlBuilder bip21, IPayjoinWallet wallet,
            PSBT signedPSBT, CancellationToken cancellationToken)
        {
            if (bip21 == null)
                throw new ArgumentNullException(nameof(bip21));
            if (!bip21.TryGetPayjoinEndpoint(out var endpoint))
                throw new InvalidOperationException("This BIP21 does not support payjoin");
            if (wallet == null)
                throw new ArgumentNullException(nameof(wallet));
            if (signedPSBT == null)
                throw new ArgumentNullException(nameof(signedPSBT));
            if (signedPSBT.IsAllFinalized())
                throw new InvalidOperationException("The original PSBT should not be finalized.");
            var optionalParameters = new PayjoinClientParameters();
            var inputScriptType = wallet.ScriptPubKeyType;
            var paymentScriptPubKey = bip21.Address?.ScriptPubKey;
            var changeOutput = signedPSBT.Outputs
                .CoinsFor(wallet, wallet.AccountKey, wallet.RootedKeyPath)
                .FirstOrDefault(o => o.ScriptPubKey != paymentScriptPubKey);
            if (changeOutput is { } o)
                optionalParameters.AdditionalFeeOutputIndex = (int) o.Index;
            if (!signedPSBT.TryGetEstimatedFeeRate(out var originalFeeRate))
                throw new ArgumentException("signedPSBT should have utxo information", nameof(signedPSBT));
            var originalFee = signedPSBT.GetFee();
            if (changeOutput is not null)
                optionalParameters.MaxAdditionalFeeContribution = MaxFeeBumpContribution ?? originalFeeRate.GetFee(GetVirtualSize(inputScriptType));
            if (MinimumFeeRate is { } v)
                optionalParameters.MinFeeRate = v;

            var allowOutputSubstitution = optionalParameters.DisableOutputSubstitution is not true;
            if (bip21.UnknownParameters.TryGetValue("pjos", out var pjos) && pjos == "0")
                allowOutputSubstitution = false;
            var originalPSBT = CreateOriginalPSBT(signedPSBT);
            var originalGlobalTx = signedPSBT.GetGlobalTransaction();
            var feeOutput = changeOutput == null ? null : originalGlobalTx.Outputs[changeOutput.Index];
            var originalInputs = new Queue<(TxIn OriginalTxIn, PSBTInput SignedPSBTInput)>();
            for (var i = 0; i < originalGlobalTx.Inputs.Count; i++)
            {
                originalInputs.Enqueue((originalGlobalTx.Inputs[i], signedPSBT.Inputs[i]));
            }

            var originalOutputs = new Queue<(TxOut OriginalTxOut, PSBTOutput SignedPSBTOutput)>();
            for (int i = 0; i < originalGlobalTx.Outputs.Count; i++)
            {
                originalOutputs.Enqueue((originalGlobalTx.Outputs[i], signedPSBT.Outputs[i]));
            }

            var proposal = await SendOriginalTransaction(endpoint, originalPSBT, optionalParameters, cancellationToken);
            // Checking that the PSBT of the receiver is clean
            if (proposal.GlobalXPubs.Any())
            {
                throw new PayjoinSenderException("GlobalXPubs should not be included in the receiver's PSBT");
            }
            ////////////

            if (proposal.CheckSanity() is List<PSBTError> errors && errors.Count > 0)
                throw new PayjoinSenderException($"The proposal PSBT is not sane ({errors[0]})");

            var proposalGlobalTx = proposal.GetGlobalTransaction();
            // Verify that the transaction version, and nLockTime are unchanged.
            if (proposalGlobalTx.Version != originalGlobalTx.Version)
                throw new PayjoinSenderException($"The proposal PSBT changed the transaction version");
            if (proposalGlobalTx.LockTime != originalGlobalTx.LockTime)
                throw new PayjoinSenderException($"The proposal PSBT changed the nLocktime");

            HashSet<Sequence> sequences = new HashSet<Sequence>();
            // For each inputs in the proposal:
            foreach (var proposedPSBTInput in proposal.Inputs)
            {
                if (proposedPSBTInput.HDKeyPaths.Count != 0)
                    throw new PayjoinSenderException("The receiver added keypaths to an input");
                if (proposedPSBTInput.PartialSigs.Count != 0)
                    throw new PayjoinSenderException("The receiver added partial signatures to an input");
                var proposedTxIn = proposalGlobalTx.Inputs.FindIndexedInput(proposedPSBTInput.PrevOut).TxIn;
                var isOurInput = originalInputs.Count > 0 &&
                                 originalInputs.Peek().OriginalTxIn.PrevOut == proposedPSBTInput.PrevOut;
                // If it is one of our input
                if (isOurInput)
                {
                    var input = originalInputs.Dequeue();
                    // Verify that sequence is unchanged.
                    if (input.OriginalTxIn.Sequence != proposedTxIn.Sequence)
                        throw new PayjoinSenderException("The proposedTxIn modified the sequence of one of our inputs");
                    // Verify the PSBT input is not finalized
                    if (proposedPSBTInput.IsFinalized())
                        throw new PayjoinSenderException("The receiver finalized one of our inputs");
                    // Verify that <code>non_witness_utxo</code> and <code>witness_utxo</code> are not specified.
                    if (proposedPSBTInput.NonWitnessUtxo != null || proposedPSBTInput.WitnessUtxo != null)
                        throw new PayjoinSenderException(
                            "The receiver added non_witness_utxo or witness_utxo to one of our inputs");
                    sequences.Add(proposedTxIn.Sequence);

                    // Fill up the info from the original PSBT input so we can sign and get fees.
                    proposedPSBTInput.NonWitnessUtxo = input.SignedPSBTInput.NonWitnessUtxo;
                    proposedPSBTInput.WitnessUtxo = input.SignedPSBTInput.WitnessUtxo;
                    // We fill up information we had on the signed PSBT, so we can sign it.
                    foreach (var hdKey in input.SignedPSBTInput.HDKeyPaths)
                        proposedPSBTInput.HDKeyPaths.Add(hdKey.Key, hdKey.Value);
                    proposedPSBTInput.RedeemScript = input.SignedPSBTInput.RedeemScript;
                }
                else
                {
                    // Verify the PSBT input is finalized
                    if (!proposedPSBTInput.IsFinalized())
                        throw new PayjoinSenderException("The receiver did not finalized one of their input");
                    // Verify that non_witness_utxo or witness_utxo are filled in.
                    if (proposedPSBTInput.NonWitnessUtxo == null && proposedPSBTInput.WitnessUtxo == null)
                        throw new PayjoinSenderException(
                            "The receiver did not specify non_witness_utxo or witness_utxo for one of their inputs");
                    sequences.Add(proposedTxIn.Sequence);
                    // Verify that the payjoin proposal did not introduced mixed input's type.
                    if (inputScriptType != proposedPSBTInput.GetInputScriptPubKeyType())
                        throw new PayjoinSenderException("Mixed input type detected in the proposal");
                }
            }

            // Verify that all of sender's inputs from the original PSBT are in the proposal.
            if (originalInputs.Count != 0)
                throw new PayjoinSenderException("Some of our inputs are not included in the proposal");

            // Verify that the payjoin proposal did not introduced mixed input's sequence.
            if (sequences.Count != 1)
                throw new PayjoinSenderException("Mixed sequence detected in the proposal");

            if (!proposal.TryGetFee(out var newFee))
                throw new PayjoinSenderException(
                    "The payjoin receiver did not included UTXO information to calculate fee correctly");
            var additionalFee = newFee - originalFee;
            if (additionalFee < Money.Zero)
                throw new PayjoinSenderException("The receiver decreased absolute fee");

            // For each outputs in the proposal:
            foreach (var proposedPSBTOutput in proposal.Outputs)
            {
                // Verify that no keypaths is in the PSBT output
                if (proposedPSBTOutput.HDKeyPaths.Count != 0)
                    throw new PayjoinSenderException("The receiver added keypaths to an output");
                if (originalOutputs.Count == 0)
                    continue;
                var originalOutput = originalOutputs.Peek();
                var isOriginalOutput = originalOutput.OriginalTxOut.ScriptPubKey == proposedPSBTOutput.ScriptPubKey;
                var substitutedOutput = !isOriginalOutput &&
                                        allowOutputSubstitution &&
                                        originalOutput.OriginalTxOut.ScriptPubKey == paymentScriptPubKey;
                if (isOriginalOutput || substitutedOutput)
                {
                    originalOutputs.Dequeue();
                    if (originalOutput.OriginalTxOut == feeOutput)
                    {
                        var actualContribution = feeOutput.Value - proposedPSBTOutput.Value;
                        // The amount that was substracted from the output's value is less or equal to maxadditionalfeecontribution
                        if (actualContribution > optionalParameters.MaxAdditionalFeeContribution)
                            throw new PayjoinSenderException(
                                "The actual contribution is more than maxadditionalfeecontribution");
                        // Make sure the actual contribution is only paying fee
                        if (actualContribution > additionalFee)
                            throw new PayjoinSenderException("The actual contribution is not only paying fee");
                        // Make sure the actual contribution is only paying for fee incurred by additional inputs
                        var additionalInputsCount = proposalGlobalTx.Inputs.Count - originalGlobalTx.Inputs.Count;
                        if (actualContribution > originalFeeRate.GetFee(GetVirtualSize(inputScriptType)) *
                            additionalInputsCount)
                            throw new PayjoinSenderException(
                                "The actual contribution is not only paying for additional inputs");
                    }
                    else if (allowOutputSubstitution &&
                             originalOutput.OriginalTxOut.ScriptPubKey == paymentScriptPubKey)
                    {
                        // That's the payment output, the receiver may have changed it.
                    }
                    else
                    {
                        if (originalOutput.OriginalTxOut.Value > proposedPSBTOutput.Value)
                            throw new PayjoinSenderException("The receiver decreased the value of one of the outputs");
                    }
                    // We fill up information we had on the signed PSBT, so we can sign it.
                    foreach (var hdKey in originalOutput.SignedPSBTOutput.HDKeyPaths)
                        proposedPSBTOutput.HDKeyPaths.Add(hdKey.Key, hdKey.Value);
                    proposedPSBTOutput.RedeemScript = originalOutput.SignedPSBTOutput.RedeemScript;
                }
            }
            // Verify that all of sender's outputs from the original PSBT are in the proposal.
            if (originalOutputs.Count != 0)
            {
                if (!allowOutputSubstitution ||
                    originalOutputs.Count != 1 ||
                    originalOutputs.Dequeue().OriginalTxOut.ScriptPubKey != paymentScriptPubKey)
                {
                    throw new PayjoinSenderException("Some of our outputs are not included in the proposal");
                }
            }

            // If minfeerate was specified, check that the fee rate of the payjoin transaction is not less than this value.
            if (optionalParameters.MinFeeRate is FeeRate minFeeRate)
            {
                if (!proposal.TryGetEstimatedFeeRate(out var newFeeRate))
                    throw new PayjoinSenderException(
                        "The payjoin receiver did not included UTXO information to calculate fee correctly");
                if (newFeeRate < minFeeRate)
                    throw new PayjoinSenderException("The payjoin receiver created a payjoin with a too low fee rate");
            }
            return proposal;
        }

        private int GetVirtualSize(ScriptPubKeyType? scriptPubKeyType)
        {
            return scriptPubKeyType switch
            {
                ScriptPubKeyType.Legacy => 148,
                ScriptPubKeyType.Segwit => 68,
                ScriptPubKeyType.SegwitP2SH => 91,
                ScriptPubKeyType.TaprootBIP86 => 58,
                _ => 110
            };
        }

        private static PSBT CreateOriginalPSBT(PSBT signedPSBT)
        {
            var original = signedPSBT.Clone();
            original = original.Finalize();
            foreach (var input in original.Inputs)
            {
                input.HDKeyPaths.Clear();
                input.PartialSigs.Clear();
                input.Unknown.Clear();
            }

            foreach (var output in original.Outputs)
            {
                output.Unknown.Clear();
                output.HDKeyPaths.Clear();
            }

            original.GlobalXPubs.Clear();
            return original;
        }

        private async Task<PSBT> SendOriginalTransaction(Uri endpoint, PSBT originalTx,
            PayjoinClientParameters parameters,
            CancellationToken cancellationToken)
        {
            return await _payjoinServerCommunicator.RequestPayjoin(endpoint, originalTx, parameters, cancellationToken);
        }
    }
}