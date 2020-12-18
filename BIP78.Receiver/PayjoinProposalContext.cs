using System;
using System.Linq;
using System.Threading.Tasks;
using BIP78.Sender;
using NBitcoin;

namespace BIP78.Receiver
{
    public class PayjoinProposalContext
    {
        private PayjoinReceiverWalletProposal _payjoinReceiverWalletProposal;

        public PayjoinProposalContext(PSBT originalPSBT,
            PayjoinClientParameters payjoinClientParameters = null)
        {
            OriginalPSBT = originalPSBT;
            OriginalTransaction = originalPSBT.ExtractTransaction();
            PayjoinParameters = payjoinClientParameters ?? new PayjoinClientParameters()
            {
                MaxAdditionalFeeContribution = Money.Zero,
                Version = 1,
                DisableOutputSubstitution = false,
                MinFeeRate = null,
                AdditionalFeeOutputIndex = null
            };
        }

        public Transaction OriginalTransaction { get; }

        public virtual void SetPaymentRequest(PayjoinPaymentRequest paymentRequest)
        {
            PaymentRequest = paymentRequest;
            OriginalPaymentRequestOutput = OriginalPSBT.Outputs.Single(output =>
                output.ScriptPubKey == paymentRequest.Destination.ScriptPubKey &&
                output.Value.Equals(paymentRequest.Amount));
        }

        public PSBTOutput OriginalPaymentRequestOutput { get; protected set; }

        public PayjoinPaymentRequest PaymentRequest { get; protected set; }
        public PSBT OriginalPSBT { get; }
        public PayjoinClientParameters PayjoinParameters { get; }

        public PayjoinReceiverWalletProposal PayjoinReceiverWalletProposal
        {
            get => _payjoinReceiverWalletProposal;
            set => _payjoinReceiverWalletProposal ??= value;
        }

        public virtual Task DisposeAsync()
        {
            return Task.CompletedTask;
        }
    }
}