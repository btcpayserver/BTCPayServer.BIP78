using NBitcoin;
using NBitcoin.JsonConverters;
using Newtonsoft.Json;

namespace BTCPayServer.BIP78.Sender
{
    public class PayjoinClientParameters
    {
        [JsonConverter(typeof(MoneyJsonConverter))]
        [JsonProperty("maxAdditionalFeeContribution", NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Ignore)]
        public Money MaxAdditionalFeeContribution { get; set; }

        [JsonProperty("minFeeRate", NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Ignore)]
        [JsonConverter(typeof(FeeRateJsonConverter))]
        public FeeRate MinFeeRate { get; set; }

        [JsonProperty("additionalFeeOutputIndex", NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Ignore)]

        public int? AdditionalFeeOutputIndex { get; set; }

        [JsonProperty("disableOutputSubstitution", NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool? DisableOutputSubstitution { get; set; }

        [JsonProperty("version", NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Populate)]
        public int Version { get; set; } = 1;
    }
}