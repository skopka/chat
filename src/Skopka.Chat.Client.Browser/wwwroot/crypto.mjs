import sodium from './vendor/libsodium-wrappers.mjs';

export async function ready() { await sodium.ready; return true; }
export function randomKey() { return sodium.randombytes_buf(32); }
export function publicKey(algorithm, secret) {
    try {
        if (algorithm === 1) return sodium.crypto_scalarmult_base(secret);
        if (algorithm !== 2) return null;
        const pair = sodium.crypto_sign_seed_keypair(secret);
        try { return pair.publicKey; } finally { sodium.memzero(pair.privateKey); }
    } catch { return null; } finally { sodium.memzero(secret); }
}
export function agreement(secret, peer) {
    try { return sodium.crypto_scalarmult(secret, peer); }
    catch { return null; } finally { sodium.memzero(secret); }
}
export function sign(secret, message) {
    let pair;
    try { pair = sodium.crypto_sign_seed_keypair(secret); return sodium.crypto_sign_detached(message, pair.privateKey); }
    catch { return null; }
    finally { sodium.memzero(secret); if (pair) sodium.memzero(pair.privateKey); sodium.memzero(message); }
}
export function verify(peer, message, signature) {
    try { return sodium.crypto_sign_verify_detached(signature, message, peer); }
    catch { return false; }
}
export function encrypt(key, nonce, aad, plaintext) {
    try { return sodium.crypto_aead_xchacha20poly1305_ietf_encrypt(plaintext, aad, null, nonce, key); }
    catch { return null; } finally { sodium.memzero(key); sodium.memzero(plaintext); }
}
export function decrypt(key, nonce, aad, ciphertext) {
    try { return sodium.crypto_aead_xchacha20poly1305_ietf_decrypt(null, ciphertext, aad, nonce, key); }
    catch { return null; } finally { sodium.memzero(key); }
}
