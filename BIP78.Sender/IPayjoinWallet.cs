using NBitcoin;

namespace BIP78.Sender
{
    public interface IPayjoinWallet: IHDScriptPubKey
    {
        public ScriptPubKeyType ScriptPubKeyType { get; }
        RootedKeyPath RootedKeyPath { get; }
        IHDKey AccountKey { get; }
    }
}