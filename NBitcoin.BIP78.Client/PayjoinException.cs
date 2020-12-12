using System;

namespace NBitcoin.BIP78.Client
{
    public class PayjoinException : Exception
    {
        public PayjoinException(string message) : base(message)
        {
        }
    }
}