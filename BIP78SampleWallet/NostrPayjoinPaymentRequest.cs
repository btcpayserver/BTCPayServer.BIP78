using BTCPayServer.BIP78.Receiver;
using NBitcoin.Secp256k1;
using NNostr.Client;

namespace BIP78SampleWallet;

public class NostrPayjoinPaymentRequest : PayjoinPaymentRequest
{
    public required ECPrivKey NostrKey { get; set; }
    public string NostrPubKey => NostrKey.CreateXOnlyPubKey().ToHex();
    public DateTimeOffset? Expiry { get; set; }

    public string? OriginalTxId { get; set; }
    public string? ProposedTxId { get; set; }

    public string? DetectedTxId { get; set; }
}