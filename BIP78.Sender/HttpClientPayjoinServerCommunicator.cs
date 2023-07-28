using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.BIP78.Sender
{
    public class HttpClientPayjoinServerCommunicator : IPayjoinServerCommunicator
    {
        public virtual async Task<PSBT> RequestPayjoin(Uri endpoint, PSBT originalTx,
            PayjoinClientParameters parameters, CancellationToken cancellationToken)
        {
            var uriBuilder = new UriBuilder(endpoint);
            var query = HttpUtility.ParseQueryString(uriBuilder.Query);
            var uriParams = GetParameters(endpoint, parameters);
            foreach (var parameter in uriParams)
            {
                query.Add(parameter.Key, parameter.Value);
            }
            uriBuilder.Query = query.ToString();
            endpoint = uriBuilder.Uri;
            using var client = CreateHttpClient(endpoint);
            var bpuresponse = await client.PostAsync(endpoint,
                new StringContent(originalTx.ToBase64(), Encoding.UTF8, "text/plain"), cancellationToken);
            if (!bpuresponse.IsSuccessStatusCode)
            {
                var errorStr = await bpuresponse.Content.ReadAsStringAsync();
                try
                {
                    var error = JObject.Parse(errorStr);
                    throw new PayjoinReceiverException(error["errorCode"].Value<string>(),
                        error["message"].Value<string>());
                }
                catch (JsonReaderException)
                {
                    // will throw
                    bpuresponse.EnsureSuccessStatusCode();
                    throw;
                }
            }

            var hex = await bpuresponse.Content.ReadAsStringAsync();
            return PSBT.Parse(hex, originalTx.Network);
        }

        protected virtual HttpClient CreateHttpClient(Uri uri)
        {
            return new HttpClient();
        }
        
        

        private static Dictionary<string, string> GetParameters(Uri endpoint, PayjoinClientParameters clientParameters)
        {
            Dictionary<string, string> parameters = new();
            parameters.Add($"v", clientParameters.Version.ToString());
            if (clientParameters.AdditionalFeeOutputIndex is { } additionalFeeOutputIndex)
                parameters.Add(
                    $"additionalfeeoutputindex", additionalFeeOutputIndex.ToString(CultureInfo.InvariantCulture));
            if (clientParameters.DisableOutputSubstitution is { } disableoutputsubstitution)
                parameters.Add($"disableoutputsubstitution",disableoutputsubstitution.ToString().ToLowerInvariant());
            if (clientParameters.MaxAdditionalFeeContribution is { } maxAdditionalFeeContribution)
                parameters.Add(
                    $"maxadditionalfeecontribution",maxAdditionalFeeContribution.Satoshi.ToString(CultureInfo.InvariantCulture));
            if (clientParameters.MinFeeRate is FeeRate minFeeRate)
                parameters.Add($"minfeerate",minFeeRate.SatoshiPerByte.ToString(CultureInfo.InvariantCulture));
            return parameters;
        }
    }
}