import sodium from './vendor/libsodium-wrappers.mjs';

const databaseName = 'Skopka.Chat.Browser.v1';
const maxRecord = 16 * 1024 * 1024;
const handles = new Map();
const leases = new Map();
const encoder = new TextEncoder();
let opened;
class VaultFault { constructor(status) { this.status = status; } }
const failure = error => ({ status: error instanceof VaultFault ? error.status : 'unavailable' });
const platformFault = error => new VaultFault(error?.name === 'QuotaExceededError' ? 'quota' : 'unavailable');
const request = operation => new Promise((resolve, reject) => {
    operation.onsuccess = () => resolve(operation.result);
    operation.onerror = () => reject(platformFault(operation.error));
});
async function database() {
    if (!globalThis.isSecureContext || !globalThis.indexedDB || !navigator.locks || !crypto.subtle) throw new VaultFault('unavailable');
    if (!opened) opened = new Promise((resolve, reject) => {
        const operation = indexedDB.open(databaseName, 1);
        operation.onupgradeneeded = () => {
            const db = operation.result;
            db.createObjectStore('configuration');
            const records = db.createObjectStore('records', { keyPath: 'sequence', autoIncrement: true });
            records.createIndex('slot', ['scope', 'kind', 'key'], { unique: true });
            records.createIndex('ordered', ['scope', 'kind', 'sequence']);
            records.createIndex('partition', ['scope', 'kind', 'partition', 'sequence']);
        };
        operation.onsuccess = () => {
            const db = operation.result;
            db.onversionchange = () => { db.close(); opened = undefined; };
            resolve(db);
        };
        operation.onerror = operation.onblocked = () => { opened = undefined; reject(new VaultFault('unavailable')); };
    });
    return opened;
}
async function transaction(stores, mode, action) {
    const db = await database();
    const tx = db.transaction(stores, mode, { durability: 'strict' });
    const completed = new Promise((resolve, reject) => {
        tx.oncomplete = resolve;
        tx.onabort = tx.onerror = () => reject(platformFault(tx.error));
    });
    try {
        const result = await action(tx);
        await completed;
        return result;
    } catch (error) {
        try { tx.abort(); } catch { /* already completed */ }
        await completed.catch(() => {});
        throw error instanceof VaultFault ? error : platformFault(error);
    }
}
function getHandle(id) {
    const value = handles.get(id);
    if (!value) throw new VaultFault('locked');
    return value;
}
function aad(row) { return encoder.encode(JSON.stringify(['Skopka.Chat.Browser.Record', 1, row.scope, row.kind, row.key, row.partition, row.revision])); }
function validateSlot(kind, key, partition) {
    if (!['identity', 'keys', 'events', 'plans', 'jobs', 'backup', 'backupkeys'].includes(kind) || !/^[a-zA-Z0-9-]{1,128}$/.test(key) || !/^[a-zA-Z0-9-]{0,128}$/.test(partition)) throw new VaultFault('corrupt');
}
async function seal(key, row, plaintext) {
    if (!(plaintext instanceof Uint8Array) || plaintext.length < 1 || plaintext.length > maxRecord) throw new VaultFault('corrupt');
    row.nonce = crypto.getRandomValues(new Uint8Array(12));
    row.ciphertext = new Uint8Array(await crypto.subtle.encrypt({ name: 'AES-GCM', iv: row.nonce, additionalData: aad(row), tagLength: 128 }, key, plaintext));
    return row;
}
async function unseal(key, row) {
    if (!(row.nonce instanceof Uint8Array) || row.nonce.length !== 12 || !(row.ciphertext instanceof Uint8Array) || row.ciphertext.length > maxRecord + 16) throw new VaultFault('corrupt');
    try {
        return new Uint8Array(await crypto.subtle.decrypt({ name: 'AES-GCM', iv: row.nonce, additionalData: aad(row), tagLength: 128 }, key, row.ciphertext));
    } catch { throw new VaultFault('corrupt'); }
}
async function derive(passphrase, salt) {
    await sodium.ready;
    if (!(passphrase instanceof Uint8Array) || passphrase.length < 12 || passphrase.length > 1024 || !(salt instanceof Uint8Array) || salt.length !== 16) throw new VaultFault('corrupt');
    const raw = sodium.crypto_pwhash(32, passphrase, salt, 3, 64 * 1024 * 1024, sodium.crypto_pwhash_ALG_ARGON2ID13);
    try { return await crypto.subtle.importKey('raw', raw, { name: 'AES-GCM' }, false, ['encrypt', 'decrypt']); }
    finally { sodium.memzero(raw); }
}

// Non-secret random origin installation ID. No identity creation during load.
export async function installation(create) {
    try {
        const value = await transaction(['configuration', 'records'], create ? 'readwrite' : 'readonly', async tx => {
            const store = tx.objectStore('configuration');
            const existing = await request(store.get('installation'));
            if (existing !== undefined) {
                if (typeof existing !== 'string' || !/^[0-9a-f-]{36}$/.test(existing)) throw new VaultFault('corrupt');
                return existing;
            }
            if (await request(store.count()) || await request(tx.objectStore('records').count())) throw new VaultFault('corrupt');
            if (!create) return null;
            const id = crypto.randomUUID();
            await request(store.add(id, 'installation'));
            return id;
        });
        return { status: value ? 'ok' : 'absent', value };
    } catch (error) { return failure(error); }
}

// Passphrase is a separate local-vault secret, never an account password or a network credential.
export async function unlock(scope, installationId, passphrase, create) {
    try {
        if (!/^[0-9a-f]{64}$/.test(scope)) throw new VaultFault('corrupt');
        const root = await installation(false);
        if (root.status !== 'ok' || root.value !== installationId) throw new VaultFault('recovery');
        const existing = await transaction('configuration', 'readonly', tx => request(tx.objectStore('configuration').get(scope)));
        if (!existing) {
            const count = await transaction('records', 'readonly', tx => request(tx.objectStore('records').index('ordered')
                .count(IDBKeyRange.bound([scope], [scope, []]))));
            if (count) throw new VaultFault('recovery');
        }
        if (!existing && !create) return { status: 'absent' };
        if (existing && create) return { status: 'exists' };
        const salt = existing?.salt ?? crypto.getRandomValues(new Uint8Array(16));
        if (existing && existing.version !== 1) throw new VaultFault('corrupt');
        const key = await derive(passphrase, salt);
        const check = { scope, kind: 'vault', key: 'check', partition: installationId, revision: '1' };
        if (existing) {
            let plaintext;
            try { plaintext = await unseal(key, { ...check, nonce: existing.nonce, ciphertext: existing.ciphertext }); }
            catch { return { status: 'unlock-failed' }; }
            try { if (!sodium.memcmp(plaintext, encoder.encode('Skopka.Chat.Browser.Vault.v1'))) throw new VaultFault('corrupt'); }
            finally { sodium.memzero(plaintext); }
        } else {
            const sealed = await seal(key, check, encoder.encode('Skopka.Chat.Browser.Vault.v1'));
            await transaction('configuration', 'readwrite', tx => request(tx.objectStore('configuration').add(
                { version: 1, salt, nonce: sealed.nonce, ciphertext: sealed.ciphertext }, scope)));
        }
        const handle = crypto.randomUUID();
        handles.set(handle, { scope, key });
        return { status: 'ok', value: handle };
    } catch (error) { return failure(error); }
    finally { if (passphrase instanceof Uint8Array) passphrase.fill(0); }
}
export async function lock(handle, name) {
    try {
        const value = getHandle(handle);
        if (!/^[a-zA-Z0-9-]{1,128}$/.test(name)) throw new VaultFault('corrupt');
        const controller = new AbortController();
        const timeout = setTimeout(() => controller.abort(), 10000);
        return await new Promise(resolve => {
            navigator.locks.request(`Skopka.Chat.${value.scope}.${name}`, { mode: 'exclusive', signal: controller.signal }, async () => {
                clearTimeout(timeout);
                if (!handles.has(handle)) { resolve({ status: 'locked' }); return; }
                const token = crypto.randomUUID();
                await new Promise(release => {
                    leases.set(token, { handle, release });
                    resolve({ status: 'ok', value: token });
                });
            }).catch(() => resolve({ status: 'unavailable' })).finally(() => clearTimeout(timeout));
        });
    } catch (error) { return failure(error); }
}
export function release(token) { const lease = leases.get(token); if (lease) { leases.delete(token); lease.release(); } }
export function close(handle) {
    handles.delete(handle);
    for (const [token, lease] of leases) if (lease.handle === handle) release(token);
}
export async function read(handle, kind, key) {
    try {
        const value = getHandle(handle);
        validateSlot(kind, key, '');
        const row = await transaction('records', 'readonly', tx => request(tx.objectStore('records').index('slot').get([value.scope, kind, key])));
        if (!row) return { status: 'absent' };
        return { status: 'ok', data: await unseal(value.key, row), revision: row.revision, partition: row.partition, sequence: row.sequence, key: row.key };
    } catch (error) { return failure(error); }
}
export async function write(handle, kind, key, partition, plaintext, expectedRevision) {
    try {
        const value = getHandle(handle);
        validateSlot(kind, key, partition);
        const row = await seal(value.key, { scope: value.scope, kind, key, partition, revision: crypto.randomUUID() }, plaintext);
        getHandle(handle); // logout before transaction must not start another write
        return await transaction('records', 'readwrite', async tx => {
            const store = tx.objectStore('records');
            const existing = await request(store.index('slot').get([value.scope, kind, key]));
            if ((existing?.revision ?? null) !== expectedRevision) return { status: 'conflict' };
            if (existing) row.sequence = existing.sequence;
            await request(store.put(row));
            return { status: 'ok' };
        });
    } catch (error) { return failure(error); }
    finally { if (plaintext instanceof Uint8Array) plaintext.fill(0); }
}
export async function remove(handle, kind, key, expectedRevision) {
    try {
        const value = getHandle(handle);
        validateSlot(kind, key, '');
        return await transaction('records', 'readwrite', async tx => {
            const store = tx.objectStore('records');
            const row = await request(store.index('slot').get([value.scope, kind, key]));
            if ((row?.revision ?? null) !== expectedRevision) return { status: 'conflict' };
            if (row) await request(store.delete(row.sequence));
            return { status: 'ok' };
        });
    } catch (error) { return failure(error); }
}
// Enumerate only bounded opaque keys/sequence numbers. Plaintext is read one record at a time by the managed adapter.
export async function page(handle, kind, partition, before, after, count) {
    try {
        const value = getHandle(handle);
        if (!Number.isSafeInteger(count) || count < 1 || count > 200 || !Number.isSafeInteger(before) || !Number.isSafeInteger(after) || before < 0 || after < 0) throw new VaultFault('corrupt');
        validateSlot(kind, 'page', partition ?? '');
        const prefix = partition === null ? [value.scope, kind] : [value.scope, kind, partition];
        const lower = [...prefix, after];
        const upper = [...prefix, before || Number.MAX_SAFE_INTEGER];
        const rows = await transaction('records', 'readonly', tx => new Promise((resolve, reject) => {
            const operation = tx.objectStore('records').index(partition === null ? 'ordered' : 'partition')
                .openCursor(IDBKeyRange.bound(lower, upper, true, true), before ? 'prev' : 'next');
            const result = [];
            operation.onerror = () => reject(new VaultFault('unavailable'));
            operation.onsuccess = () => {
                const cursor = operation.result;
                if (!cursor || result.length === count) { resolve(result); return; }
                result.push({ key: cursor.value.key, sequence: cursor.value.sequence });
                cursor.continue();
            };
        }));
        return { status: 'ok', rows };
    } catch (error) { return failure(error); }
}
