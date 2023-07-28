using BTCPayServer.BIP78.Sender;

namespace BTCPayServer.BIP78.Nostr;

public class NostrPayjoinRequest
{
    public string PSBT { get; set; }
    public PayjoinClientParameters Parameters { get; set; }
}