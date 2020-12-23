using System;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.BIP78.Sender;
using NBitcoin;

namespace BTCPayServer.BIP78.Receiver
{
    public class PayjoinPaymentRequest
    {
        public BitcoinAddress Destination { get; set; }
        public Money Amount { get; set; }
    }

    public abstract class PayjoinReceiverWallet<TContext> where TContext : PayjoinProposalContext
    {
        protected abstract Task<bool> SupportsType(ScriptPubKeyType scriptPubKeyType);
        protected abstract Task<bool> InputsSeenBefore(PSBTInputList inputList);
        protected abstract Task<string> IsMempoolEligible(PSBT psbt);
        protected abstract Task BroadcastOriginalTransaction(TContext context, TimeSpan scheduledTime);
        protected abstract Task ComputePayjoinModifications(TContext context);
        protected abstract Task<PayjoinPaymentRequest> FindMatchingPaymentRequests(TContext psbt);

        public virtual async Task Initiate(TContext ctx)
        {
            var paymentRequest = await FindMatchingPaymentRequests(ctx);
            if (paymentRequest is null)
            {
                throw new PayjoinReceiverException(
                    PayjoinReceiverHelper.GetErrorCode(PayjoinReceiverWellknownErrors.OriginalPSBTRejected),
                    $"Could not match PSBT to a payment request");
            }

            ctx.SetPaymentRequest(paymentRequest);

            if (ctx.PayjoinParameters.Version != 1)
            {
                throw new PayjoinReceiverException(
                    PayjoinReceiverHelper.GetErrorCode(PayjoinReceiverWellknownErrors.VersionUnsupported),
                    PayjoinReceiverHelper.GetMessage(PayjoinReceiverWellknownErrors.VersionUnsupported));
            }

            if (!ctx.OriginalPSBT.IsAllFinalized())
                throw new PayjoinReceiverException(
                    PayjoinReceiverHelper.GetErrorCode(PayjoinReceiverWellknownErrors.OriginalPSBTRejected),
                    "The PSBT should be finalized");

            var sendersInputType = ctx.OriginalPSBT.GetInputsScriptPubKeyType();
            if (sendersInputType is null || !await SupportsType(sendersInputType.Value))
            {
                //this should never happen, unless the store owner changed the wallet mid way through an invoice
                throw new PayjoinReceiverException(
                    PayjoinReceiverHelper.GetErrorCode(PayjoinReceiverWellknownErrors.Unavailable),
                    "Our wallet does not support this wallet format");
            }

            if (ctx.OriginalPSBT.CheckSanity() is var errors && errors.Count != 0)
            {
                throw new PayjoinReceiverException(
                    PayjoinReceiverHelper.GetErrorCode(PayjoinReceiverWellknownErrors.OriginalPSBTRejected),
                    $"This PSBT is insane ({errors[0]})");
            }

            FeeRate originalFeeRate;
            if (!ctx.OriginalPSBT.TryGetEstimatedFeeRate(out originalFeeRate))
            {
                throw new PayjoinReceiverException(
                    PayjoinReceiverHelper.GetErrorCode(PayjoinReceiverWellknownErrors.OriginalPSBTRejected),
                    "You need to provide Witness UTXO information to the PSBT.");
            }

            // This is actually not a mandatory check, but we don't want implementers
            // to leak global xpubs
            if (ctx.OriginalPSBT.GlobalXPubs.Any())
            {
                throw new PayjoinReceiverException(
                    PayjoinReceiverHelper.GetErrorCode(PayjoinReceiverWellknownErrors.OriginalPSBTRejected),
                    "GlobalXPubs should not be included in the PSBT");
            }

            if (ctx.OriginalPSBT.Outputs.Any(o => o.HDKeyPaths.Count != 0) ||
                ctx.OriginalPSBT.Inputs.Any(o => o.HDKeyPaths.Count != 0))
            {
                throw new PayjoinReceiverException(
                    PayjoinReceiverHelper.GetErrorCode(PayjoinReceiverWellknownErrors.OriginalPSBTRejected),
                    "Keypath information should not be included in the PSBT");
            }

            if (ctx.OriginalPSBT.Inputs.Any(o => !o.IsFinalized()))
            {
                throw new PayjoinReceiverException(
                    PayjoinReceiverHelper.GetErrorCode(PayjoinReceiverWellknownErrors.OriginalPSBTRejected),
                    "The PSBT Should be finalized");
            }

            var mempoolError = await IsMempoolEligible(ctx.OriginalPSBT);
            if (!string.IsNullOrEmpty(mempoolError))
            {
                throw new PayjoinReceiverException(
                    PayjoinReceiverHelper.GetErrorCode(PayjoinReceiverWellknownErrors.OriginalPSBTRejected),
                    $"Provided transaction isn't mempool eligible {mempoolError}");
            }

            if (await InputsSeenBefore(ctx.OriginalPSBT.Inputs))
            {
                throw new PayjoinReceiverException(
                    PayjoinReceiverHelper.GetErrorCode(PayjoinReceiverWellknownErrors.OriginalPSBTRejected),
                    "Some of those inputs have already been used to make another payjoin transaction");
            }

            var groupedOutputs = ctx.OriginalPSBT.Outputs.GroupBy(output => output.ScriptPubKey);
            var paymentOutputs =
                groupedOutputs.Where(outputs => outputs.Key == paymentRequest.Destination.ScriptPubKey);

            if (!paymentOutputs.Any())
            {
                throw new PayjoinReceiverException(
                    PayjoinReceiverHelper.GetErrorCode(PayjoinReceiverWellknownErrors.OriginalPSBTRejected),
                    "PSBT does not pay to the BIP21 destination");
            }

            if (paymentOutputs.Sum(outputs => outputs.Sum(output => output.Value)) < paymentRequest.Amount)
            {
                throw new PayjoinReceiverException("invoice-not-fully-paid",
                    "The transaction must pay the whole invoice");
            }

            if (ctx.PayjoinParameters.AdditionalFeeOutputIndex.HasValue && paymentOutputs.Any(outputs =>
                outputs.Any(output => output.Index == ctx.PayjoinParameters.AdditionalFeeOutputIndex)))
            {
                throw new PayjoinReceiverException(
                    PayjoinReceiverHelper.GetErrorCode(PayjoinReceiverWellknownErrors.OriginalPSBTRejected),
                    "AdditionalFeeOutputIndex specified index of payment output");
            }

            await ComputePayjoinModifications(ctx);

            if (ctx.PayjoinReceiverWalletProposal is null)
            {
                await BroadcastOriginalTransaction(ctx, TimeSpan.Zero);
                throw new PayjoinReceiverException(
                    PayjoinReceiverHelper.GetErrorCode(PayjoinReceiverWellknownErrors.Unavailable),
                    "We do not have any proposal for payjoin");
            }

            await BroadcastOriginalTransaction(ctx, TimeSpan.FromMinutes(2));


        }
    }
}