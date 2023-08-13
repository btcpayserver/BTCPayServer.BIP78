# BIP78 Sample wallet

This is a sample wallet that runs in the browser using WASM, demonstrating experimental additions and advanced usage of BIP78. 
It uses NBXplorer to track wallets, and a nostr relay to allow wallet<->wallet communication for payjoin coordination.

Note: this is a prototype and does not have any form of polish. It is intended to only run on regtest and the payjoin proposal are simply for demonstration and not the recommended way to implement payjoin.

## Features
* BIP78 payjoin over Nostr (see [BIP78 addendum document](https://github.com/Kukks/BTCPayServer.BIP78/blob/nostr/BTCPayServer.BIP78.Nostr/README.md))
* Payjoin Maker
  * Making a payment to someone who does not support Payjoin? No problem! This wallet can act as a payjoin maker: simply use its endpoint as the payjoin endpoint for your transaction, and it will enhance the tx, even though it is not the receiver of the transaction.
  * In combination with [NIP-15750 (Nostr Coinjoin Discovery)](https://github.com/nostr-protocol/nips/pull/384), users are able to advertise their endpoints as payjoin makers, and wallets can automatically use them as payjoin endpoints, similar in spirit to JoinMarket. When using with Payjoin over Nostr communication, all wallets can easily be a maker and taker, without a server, with an easily interoperable protocol.
  * Note: an incentive/anti-spam mechanism is still needed for makers. 
* Radically different payjoin proposal:
  * Instead of adding inputs to the payjoin, this wallet splits the payment output into 2 separate outputs, with equal amounts, to mimic a small coinjoin. Just for fun and showcasing capabilities of flexibility, not meant for production.

## Running instructions
* Install Git, Docker (and docker compose)
* Open Terminal and run `git clone https://github.com/Kukks/BTCPayServer.BIP78`
* SAwitch to `nostr` branch: `git checkout nostr`
* Go to `BIP78Tests` folder
* Run `docker-compose up`
* Open https://kukks.github.io/BTCPayServer.BIP78/ in your browser (this is a pre-built version of the code)
* Wait a bit as it connects and generates a wallet
* Use the `Fund Wallet` button to fund the wallet with random amounts of Bitcoin.
* You can open multiple tabs to simulate multiple wallets. Try creating a payment request in one tab, and paying it in another tab.
* You can try Payjoin maker by using 3 tabs: 1 for the payee, 1 for the payjoin maker, and 1 for the payer. The payee creates a payment request, the payer pays it, and the payjoin maker enhances the transaction.
* If you refresh the tab, all memory is wiped and lost.
