using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.BIP78.Receiver;
using BTCPayServer.BIP78.Sender;
using NBitcoin;
using NBitcoin.Secp256k1;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NNostr.Client;
using NNostr.Client.Crypto;
using NNostr.Client.Protocols;

namespace BTCPayServer.BIP78.Nostr;

public class PayjoinActiveKey
{
    public PayjoinActiveKey(string publicKey, DateTimeOffset? expiration)
    {
        this.PublicKey = publicKey;
        this.Expiration = expiration;
    }

    public string PublicKey { get; }
    public DateTimeOffset? Expiration { get; }
}

public class NostrPayjoinProposalContext: PayjoinProposalContext
{
    public NostrEvent SenderEvent { get; }

    public NostrPayjoinProposalContext(NostrEvent senderEvent,PSBT originalPSBT, PayjoinClientParameters payjoinClientParameters = null) : base(originalPSBT, payjoinClientParameters)
    {
        SenderEvent = senderEvent;
    }
}

public abstract class NostrPayjoinReceiver<T> : PayjoinReceiverWallet<T> where T : NostrPayjoinProposalContext
{
    protected Uri[] Relays;
    protected readonly Action<WebSocket> WebsocketConfigure;
    public readonly Lazy<ECPrivKey> StaticKey;
    protected readonly Dictionary<ECPrivKey, PayjoinActiveKey> _activeKeys = new();

    public virtual IAesEncryption GetAesEncryption()
    {
        return null;
    }
    public ReadOnlyDictionary<ECPrivKey, PayjoinActiveKey> ActiveKeys => new(_activeKeys);
    protected ConcurrentDictionary<string, byte> EventsSeen { get; set; } = new();
    private readonly SemaphoreSlim _keyLock = new(1, 1);
    public INostrClient NostrClient { get; private set; }
    private CancellationTokenSource _subscriptionCts;

    public DateTimeOffset? HandleEventsSince { get; private set; }


    public NostrPayjoinReceiver(Uri[] relays,
        Action<WebSocket> websocketConfigure = null)
    {
        Relays = relays;
        WebsocketConfigure = websocketConfigure;
        StaticKey = new Lazy<ECPrivKey>(() => ECPrivKey.Create(RandomUtils.GetBytes(32)));
    }


    public virtual Task StartAsync(CancellationToken cancellationToken)
    {
        _ = ManageNostrConnection(cancellationToken);
        _ = ExpireKeys(cancellationToken);
        return Task.CompletedTask;
    }

    private NostrSubscriptionFilter Filter => new()
    {
        Kinds = new[] {4},
        Since = HandleEventsSince,
        ReferencedPublicKeys = _activeKeys.Values.Select(k => k.PublicKey).ToArray()
            .Concat(new[] {StaticKey.Value.CreateXOnlyPubKey().ToHex()}).ToArray()
    };

    protected virtual Task<INostrClient> CreateNostrClient()
    {
        return  Task.FromResult<INostrClient>(new CompositeNostrClient(Relays, WebsocketConfigure));
    }

    private async Task ManageNostrConnection(CancellationToken cancellationToken)
    {
        NostrClient = await CreateNostrClient();
        while (!cancellationToken.IsCancellationRequested)
        {

            _subscriptionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            while (!_subscriptionCts.IsCancellationRequested)
            {
                try
                {
                    await NostrClient.ConnectAndWaitUntilConnected(cancellationToken);
                }
                catch (OperationCanceledException e)
                {
                    return;
                }
                catch (Exception e)
                {
                    break;
                }
                HandleEventsSince ??= DateTimeOffset.UtcNow - TimeSpan.FromMinutes(30);
                await foreach (var evt in NostrClient.SubscribeForEvents(new[] {Filter}, false, _subscriptionCts.Token))
                {
                    try
                    {

                        await HandleEvent(evt);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                    }
                }
                HandleEventsSince = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(30);
            }
        }

        await NostrClient.Disconnect();
    }

    private async Task HandleEvent(NostrEvent nostrEvent)
    {
        if (!EventsSeen.TryAdd(nostrEvent.Id, 0))
            return;
        if (nostrEvent.Kind != 4)
            return;
        var destinationKey = nostrEvent.GetTaggedData("p").FirstOrDefault();
        if (destinationKey is null)
            return;
        var k = _activeKeys.SingleOrDefault(pair => pair.Value.PublicKey == destinationKey);
        if (k.Key is null)
        {
            if (StaticKey.Value.CreateXOnlyPubKey().ToHex() == destinationKey)
            {
                k = new KeyValuePair<ECPrivKey, PayjoinActiveKey>(StaticKey.Value,
                    new PayjoinActiveKey(destinationKey, null));
            }
            else
            {
                return;
            }
        }
        if (k.Value.Expiration  is not null && k.Value.Expiration < DateTimeOffset.UtcNow)
            return;
        var decryptedRequest = await nostrEvent.DecryptNip04EventAsync(k.Key, GetAesEncryption());

        var request = JObject.Parse(decryptedRequest).ToObject<NostrPayjoinRequest>();
        var contentReply = string.Empty;
        try
        {
            var context = await CreateContext(request, nostrEvent);
            await Initiate(context);
            contentReply = context.PayjoinReceiverWalletProposal.PayjoinPSBT.ToBase64();
        }
        catch (PayjoinReceiverException e)
        {
            contentReply = e.ToJson().ToString(Formatting.None);
        }
        catch(Exception e)
        {
            contentReply = new PayjoinReceiverException("unknown-error", e.Message).ToJson().ToString(Formatting.None);
        }

        var reply = new NostrEvent()
        {
            Kind = 4,
            Content = contentReply
        };
        reply.SetTag("e", nostrEvent.Id);
        reply.SetTag("p", nostrEvent.PublicKey);
        
        await reply.EncryptNip04EventAsync(k.Key, GetAesEncryption());
        await reply.ComputeIdAndSignAsync(k.Key, false);
        
        await NostrClient.PublishEvent(reply);
    }

    protected abstract Task<T> CreateContext(NostrPayjoinRequest request, NostrEvent nostrEvent);

    private async Task ExpireKeys(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _keyLock.WaitAsync(cancellationToken);
                var keysToRemove = _activeKeys.Where(pair => pair.Value.Expiration < DateTimeOffset.UtcNow).ToList();
                keysToRemove.ForEach(pair => { _activeKeys.Remove(pair.Key); });
                if (keysToRemove.Any())
                {
                    _subscriptionCts?.Cancel();
                }
            }
            finally
            {
                _keyLock.Release();
                await Task.Delay(100);
            }
        }
    }
    public Uri GenerateEndpoint()
    {
        var npub = new NIP19.NosteProfileNote()
        {
            Relays = Relays.Select(u => u.ToString()).ToArray(),
            PubKey = StaticKey.Value.CreateXOnlyPubKey().ToHex()
        };
        return new Uri($"nostr:{npub.ToNIP19()}");
    }

    public Uri GenerateUniqueEndpoint(DateTimeOffset? expiry, out ECPrivKey key)
    {
        key = ECPrivKey.Create(RandomUtils.GetBytes(32));
        var npub = new NIP19.NosteProfileNote()
        {
            Relays = Relays.Select(u => u.ToString()).ToArray(),
            PubKey = key.CreateXOnlyPubKey().ToHex()
        };
        _activeKeys.Add(key, new PayjoinActiveKey(npub.PubKey, expiry));
        
        _subscriptionCts?.Cancel();
        return new Uri(
            $"nostr:{npub.ToNIP19()}{(expiry is null ? string.Empty : $"?expiry={expiry.Value.ToUnixTimeSeconds()}")}");
    }
} 