using BTCPayServer.BIP78.Sender;
using NBitcoin;
using NBXplorer.Models;

namespace BIP78Tests; 

public class PayjoinWallet : IPayjoinWallet
{
    private readonly GenerateWalletResponse _generateWalletResponse;

    public PayjoinWallet(GenerateWalletResponse generateWalletResponse)
    {
        _generateWalletResponse = generateWalletResponse;
    }

    public IHDScriptPubKey Derive(KeyPath keyPath)
    {
        return ((IHDScriptPubKey) _generateWalletResponse.DerivationScheme).Derive(keyPath);
    }

    public bool CanDeriveHardenedPath()
    {
        return _generateWalletResponse.DerivationScheme.CanDeriveHardenedPath();
    }

    public Script ScriptPubKey => ((IHDScriptPubKey) _generateWalletResponse.DerivationScheme).ScriptPubKey;
    public ScriptPubKeyType ScriptPubKeyType => _generateWalletResponse.DerivationScheme.ScriptPubKeyType();

    public RootedKeyPath RootedKeyPath => _generateWalletResponse.AccountKeyPath;

    public IHDKey AccountKey => _generateWalletResponse.AccountHDKey;
}