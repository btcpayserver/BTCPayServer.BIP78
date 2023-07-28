using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.BIP78.Sender
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

        public JObject ToJson()
        {
            var o = new JObject();
            o.Add(new JProperty("errorCode", ErrorCode));
            o.Add(new JProperty("message", ErrorMessage));
            return o;
        }
    }
}