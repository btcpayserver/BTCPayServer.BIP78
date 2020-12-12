using System;
using System.Threading;
using System.Threading.Tasks;

namespace NBitcoin.BIP78.Client
{
    public interface IPayjoinServerCommunicator
    {
        Task<PSBT> RequestPayjoin(Uri endpoint, PSBT originalTx, CancellationToken cancellationToken);
    }
}