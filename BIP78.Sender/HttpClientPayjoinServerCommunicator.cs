using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.BIP78.Sender
{
    public class HttpClientPayjoinServerCommunicator : IPayjoinServerCommunicator
    {
        public virtual async Task<PSBT> RequestPayjoin(Uri endpoint, PSBT originalTx, CancellationToken cancellationToken)
        {
            using HttpClient client = CreateHttpClient(endpoint);
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
    }
}