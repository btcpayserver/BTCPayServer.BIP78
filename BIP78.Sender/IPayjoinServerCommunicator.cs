using System;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;

namespace BTCPayServer.BIP78.Sender
{
    public interface IPayjoinServerCommunicator
    {
        Task<PSBT> RequestPayjoin(Uri endpoint, PSBT originalTx, CancellationToken cancellationToken);
    }
}