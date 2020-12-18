using NBitcoin;

namespace BIP78.Receiver
{
    public class PayjoinReceiverWalletProposal 
    {
        public ICoin[] ContributedInputs { get; set; }
        public TxOut[] ContributedOutputs { get; set; }
        public TxOut ModifiedPaymentRequest { get; set; }
        public Money ExtraFeeFromAdditionalFeeOutput { get; set; }
        public PSBT PayjoinPSBT { get; set; }
        public uint256 PayjoinTransactionHash { get; set; }
        public Money ExtraFeeFromReceiverInputs { get; set; }
    }
}