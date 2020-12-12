using System;
using System.Linq;
using NBitcoin.Payment;

namespace NBitcoin.BIP78.Client
{
    public static class PSBTExtensions
    {
        public static bool TryGetPayjoinEndpoint(this BitcoinUrlBuilder bip21, out Uri endpoint)
        {
            endpoint = bip21.UnknowParameters.TryGetValue($"{PayjoinClient.BIP21EndpointKey}", out var uri) ? new Uri(uri, UriKind.Absolute) : null;
            return endpoint != null;
        }
        public static ScriptPubKeyType? GetInputsScriptPubKeyType(this PSBT psbt)
        {
            if (!psbt.IsAllFinalized())
                throw new InvalidOperationException("The psbt should be finalized with witness information");
            var coinsPerTypes = psbt.Inputs.Select(i =>
            {
                return ((PSBTCoin)i, GetInputScriptPubKeyType(i));
            }).GroupBy(o => o.Item2, o => o.Item1).ToArray();
            if (coinsPerTypes.Length != 1)
                return default;
            return coinsPerTypes[0].Key;
        }

        public static ScriptPubKeyType? GetInputScriptPubKeyType(this PSBTInput i)
        {
            var scriptPubKey = i.GetTxOut().ScriptPubKey;
            if (scriptPubKey.IsScriptType(ScriptType.P2PKH))
                return ScriptPubKeyType.Legacy;
            if (scriptPubKey.IsScriptType(ScriptType.P2WPKH))
                return ScriptPubKeyType.Segwit;
            if (scriptPubKey.IsScriptType(ScriptType.P2SH) &&
                i.FinalScriptWitness is WitScript &&
                PayToWitPubKeyHashTemplate.Instance.ExtractWitScriptParameters(i.FinalScriptWitness) is { })
                return ScriptPubKeyType.SegwitP2SH;
            if (scriptPubKey.IsScriptType(ScriptType.P2SH) &&
                i.RedeemScript is Script &&
                PayToWitPubKeyHashTemplate.Instance.CheckScriptPubKey(i.RedeemScript))
                return ScriptPubKeyType.SegwitP2SH;
            return null;
        }
    }
}