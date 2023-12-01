
function aes_cbc_encrypt(data, rawKey) {

    const iv = window.crypto.getRandomValues(new Uint8Array(16));

    return aes_cbc_key(rawKey).then(function(key) {
        return window.crypto.subtle.encrypt(
            {
                name: "AES-CBC",
                //Don't re-use initialization vectors!
                //Always generate a new iv every time your encrypt!
                iv,
            },
            key, //from generateKey or importKey above
            data //ArrayBuffer of data you want to encrypt
        )
            .then(function(encrypted) {
                //returns an ArrayBuffer containing the encrypted data
                console.log(new Uint8Array(encrypted));
                return {
                    iv,
                    cipherText: new Uint8Array(encrypted)
                };
            })
            .catch(function(err) {
                console.error(err);
            })
    });
}


function aes_cbc_decrypt(data, iv, rawKey) {
    return aes_cbc_key(rawKey).then(function(key) {
        return window.crypto.subtle.decrypt(
            {
                name: "AES-CBC",
                iv, //The initialization vector you used to encrypt
            },
            key, //from generateKey or importKey above
            data //ArrayBuffer of the data
        )
            .then(function(decrypted) {
                //returns an ArrayBuffer containing the decrypted data
                const decryptedData = new Uint8Array(decrypted);
                console.log(decryptedData);
                return decryptedData;
            })
            .catch(function(err) {
                console.error(err);
            });

    })
        .catch(function(err) {
            console.error(err);
        });
}

function aes_cbc_key(rawKey) {
    return window.crypto.subtle.importKey(
        "raw", //can be "jwk" or "raw"
        rawKey,
        { //this is the algorithm options
            name: "AES-CBC",
        },
        false, //whether the key is extractable (i.e. can be used in exportKey)
        ["encrypt", "decrypt"] //can be "encrypt", "decrypt", "wrapKey", or "unwrapKey"
    )
        .then(function(key) {
            //returns the symmetric key
            console.log(key);
            return key;
        })
        .catch(function(err) {
            console.error(err);
        });
}