using System;

namespace BTCPayServer.BIP78.Sender
{
    public class PayjoinException : Exception
    {
        public PayjoinException(string message) : base(message)
        {
        }
    }
}