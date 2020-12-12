namespace NBitcoin.BIP78.Client
{
    public class PayjoinReceiverException : PayjoinException
    {
        public PayjoinReceiverException(string errorCode, string receiverMessage) : base(FormatMessage(errorCode, receiverMessage))
        {
            ErrorCode = errorCode;
            ReceiverMessage = receiverMessage;
            WellknownError = PayjoinReceiverHelper.GetWellknownError(errorCode);
            ErrorMessage = PayjoinReceiverHelper.GetMessage(errorCode);
        }
        public string ErrorCode { get; }
        public string ErrorMessage { get; }
        public string ReceiverMessage { get; }

        public PayjoinReceiverWellknownErrors? WellknownError
        {
            get;
        }

        private static string FormatMessage(string errorCode, string receiverMessage)
        {
            return $"{errorCode}: {PayjoinReceiverHelper.GetMessage(errorCode)}. (Receiver message: {receiverMessage})";
        }
    }
}