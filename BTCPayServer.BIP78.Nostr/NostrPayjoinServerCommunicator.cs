using System;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.BIP78.Sender;
using NBitcoin;
using NBitcoin.Secp256k1;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NNostr.Client;
using NNostr.Client.Crypto;
using NNostr.Client.Protocols;

namespace BTCPayServer.BIP78.Nostr;

public class CompositePayjoinServerCommunicator : IPayjoinServerCommunicator
{
    private readonly Func<Uri, IPayjoinServerCommunicator> _communicatorResolver;

    public CompositePayjoinServerCommunicator()
    {
        _communicatorResolver = uri =>
        {
            switch (uri.Scheme)
            {
                case "nostr": return new NostrPayjoinServerCommunicator();
                default: return new HttpClientPayjoinServerCommunicator();
            }
        };
    }

    public CompositePayjoinServerCommunicator(Func<Uri, IPayjoinServerCommunicator> communicatorResolver)
    {
        _communicatorResolver = communicatorResolver;
    }

    public Task<PSBT> RequestPayjoin(Uri endpoint, PSBT originalTx, PayjoinClientParameters parameters,
        CancellationToken cancellationToken)
    {
        var communicator = _communicatorResolver(endpoint);
        if (communicator is null)
        {
            throw new PayjoinSenderException(
                "A payjoin endpoint was provided but no communicator was found that supports it");
        }

        return _communicatorResolver(endpoint).RequestPayjoin(endpoint, originalTx, parameters, cancellationToken);
    }
}

public class NostrPayjoinServerCommunicator : IPayjoinServerCommunicator
{
    public IAesEncryption NostrAesEncryption { get; set; }
    private readonly Uri[] _relays;

    private readonly Action<WebSocket> _websocketConfigure;

    public NostrPayjoinServerCommunicator(Uri[] relays = null, Action<WebSocket> websocketConfigure = null)
    {
        _relays = relays ?? Array.Empty<Uri>();
        _websocketConfigure = websocketConfigure;
    }
    

    public async Task<PSBT> RequestPayjoin(Uri endpoint, PSBT originalTx, PayjoinClientParameters parameters,
        CancellationToken cancellationToken)
    {
        if (endpoint.Scheme != "nostr")
            throw new ArgumentException("Invalid scheme", nameof(endpoint));
        var relays = _relays;
        string destination = null;

        if (endpoint.AbsolutePath.StartsWith("npub"))
        {
            destination = endpoint.AbsolutePath.FromNIP19Npub().ToHex();
        }
        else if (endpoint.AbsolutePath.StartsWith("nprofile"))
        {
            var note = endpoint.AbsolutePath.FromNIP19Note();

            switch (note)
            {
                case NIP19.NosteProfileNote nostrProfileNote:
                    destination = nostrProfileNote.PubKey;
                    relays = relays
                        .Concat(nostrProfileNote.Relays.Select(s =>
                            Uri.TryCreate(s, UriKind.Absolute, out var r) ? r : null))
                        .Where(uri => uri is not null).ToArray();
                    break;
                default:
                    throw new ArgumentException(
                        "the payjoin endpoint was not a valid nostr endpoint (npub/nprofile/public key hex)",
                        nameof(endpoint));
            }
        }
        else
        {
            try
            {
                Convert.FromHexString(endpoint.AbsolutePath);

                destination = endpoint.AbsolutePath;
            }
            catch (Exception e)
            {
                throw new ArgumentException(
                    "the payjoin endpoint was not a valid nostr endpoint (npub/nprofile/public key hex)",
                    nameof(endpoint));
            }
        }

        var newKey = ECPrivKey.Create(RandomUtils.GetBytes(32));
        var nostrClient = new CompositeNostrClient(relays, _websocketConfigure);
        await nostrClient.ConnectAndWaitUntilConnected(cancellationToken);
        var evt = new NostrEvent()
        {
            Kind = 4,
            Content = JObject.FromObject(new NostrPayjoinRequest()
            {
                PSBT = originalTx.ToBase64(),
                Parameters = parameters
            }).ToString(Formatting.None),
        };
        evt.SetTag("p", destination);
        await evt.EncryptNip04EventAsync(newKey, NostrAesEncryption);
        await evt.ComputeIdAndSignAsync(newKey, false);

        var eventCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var replies = nostrClient.SubscribeForEvents(new[]
        {
            new NostrSubscriptionFilter()
            {
                Kinds = new[] {4},
                Authors = new[] {destination},
                ReferencedEventIds = new[] {evt.Id},
            }
        }, false, eventCancellationTokenSource.Token);
        await nostrClient.SendEventsAndWaitUntilReceived(new[] {evt}, cancellationToken);
        var reply = await replies.FirstAsync(cancellationToken);
        eventCancellationTokenSource.Cancel();
        var response = await reply.DecryptNip04EventAsync(newKey, NostrAesEncryption);
        try
        {
            return PSBT.Parse(response, originalTx.Network);
        }
        catch (Exception)
        {
            var resp = JObject.Parse(response);
            throw new PayjoinReceiverException(resp["errorCode"].Value<string>(), resp["message"].Value<string>());
        }
    }
}