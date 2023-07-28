using NBitcoin;
using NBXplorer.DerivationStrategy;

namespace BIP78SampleWallet;

public static class Extensions
{
    public static ScriptPubKeyType ScriptPubKeyType(this DerivationStrategyBase derivationStrategyBase)
    {
        if (derivationStrategyBase is TaprootDerivationStrategy)
        {
            return NBitcoin.ScriptPubKeyType.TaprootBIP86;
        }
        if (derivationStrategyBase is P2WSHDerivationStrategy or DirectDerivationStrategy {Segwit: true})
        {
            return NBitcoin.ScriptPubKeyType.Segwit;
        }

        return derivationStrategyBase is P2SHDerivationStrategy {Inner: P2WSHDerivationStrategy or DirectDerivationStrategy {Segwit: true}}
            ? NBitcoin.ScriptPubKeyType.SegwitP2SH
            : NBitcoin.ScriptPubKeyType.Legacy;
    }
    
   
}