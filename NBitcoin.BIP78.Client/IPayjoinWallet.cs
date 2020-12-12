namespace NBitcoin.BIP78.Client
{
    public interface IPayjoinWallet: IHDScriptPubKey
    {
        public ScriptPubKeyType ScriptPubKeyType { get; }
        RootedKeyPath RootedKeyPath { get; }
        IHDKey AccountKey { get; }
    }
}