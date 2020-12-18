using System.Collections.Generic;
using System.Linq;

namespace BIP78.Sender
{
    public class PayjoinReceiverHelper
    {
        static IEnumerable<(PayjoinReceiverWellknownErrors EnumValue, string ErrorCode, string Message)> Get()
        {
            yield return (PayjoinReceiverWellknownErrors.Unavailable, "unavailable", "The payjoin endpoint is not available for now.");
            yield return (PayjoinReceiverWellknownErrors.NotEnoughMoney, "not-enough-money", "The receiver added some inputs but could not bump the fee of the payjoin proposal.");
            yield return (PayjoinReceiverWellknownErrors.VersionUnsupported, "version-unsupported", "This version of payjoin is not supported.");
            yield return (PayjoinReceiverWellknownErrors.OriginalPSBTRejected, "original-psbt-rejected", "The receiver rejected the original PSBT.");
        }
        public static string GetErrorCode(PayjoinReceiverWellknownErrors err)
        {
            return Get().Single(o => o.EnumValue == err).ErrorCode;
        }
        public static PayjoinReceiverWellknownErrors? GetWellknownError(string errorCode)
        {
            var t = Get().FirstOrDefault(o => o.ErrorCode == errorCode);
            if (t == default)
                return null;
            return t.EnumValue;
        }
        static readonly string UnknownError = "Unknown error from the receiver";
        public static string GetMessage(string errorCode)
        {
            return Get().FirstOrDefault(o => o.ErrorCode == errorCode).Message ?? UnknownError;
        }
        public static string GetMessage(PayjoinReceiverWellknownErrors err)
        {
            return Get().Single(o => o.EnumValue == err).Message;
        }
    }
}