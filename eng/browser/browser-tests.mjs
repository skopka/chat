import { chromium, firefox } from 'playwright';
import { writeFile, mkdir } from 'node:fs/promises';
const url = process.env.SKOPKA_BROWSER_TEST_URL ?? 'http://127.0.0.1:5190/';
const artifacts = new URL('../../artifacts/browser-results/', import.meta.url);
await mkdir(artifacts, { recursive: true });
for (const engine of [chromium, firefox]) {
    const browser = await engine.launch();
    try {
        const context = await browser.newContext();
        const errors = [];
        const page = await context.newPage();
        page.on('pageerror', () => errors.push('pageerror'));
        page.on('console', message => { if (message.type() === 'error') errors.push('consoleerror'); });
        const open = async target => { await target.goto(url); await target.locator('#status').filter({ hasText: 'ready' }).waitFor(); };
        const run = async (action, target = page) => {
            const result = await target.evaluate(action => globalThis.runChatTest(action), action);
            if (result.startsWith('FAIL:')) throw new Error(`${engine.name()} ${result}`);
            console.log(`${engine.name()}: ${action} passed`);
            return result;
        };
        await open(page);
        const interop = await run('crypto');
        JSON.parse(interop);
        await writeFile(new URL(`${engine.name()}-interop.json`, artifacts), interop);
        await run('bff');
        await run('prepare');
        const second = await context.newPage();
        await open(second);
        const ids = await Promise.all([run('identity'), run('identity', second)]);
        if (ids[0] !== ids[1]) throw new Error('Cross-tab identity mismatch');
        await run('storage');
        await run('backup');
        await run('trusted-vault');
        const eventResults = await Promise.all([run('event-race'), run('event-race', second)]);
        if (eventResults.sort().join(',') !== 'ok:Duplicate,ok:Stored') throw new Error('Independent event writers were not atomic');
        await run('partial');
        await page.evaluate(async () => {
            const db = await new Promise(resolve => { const request = indexedDB.open('Skopka.Chat.Browser.v1'); request.onsuccess = () => resolve(request.result); });
            const rows = await new Promise(resolve => { const request = db.transaction('records').objectStore('records').getAll(); request.onsuccess = () => resolve(request.result); });
            db.close();
            const allowed = ['ciphertext', 'key', 'kind', 'nonce', 'partition', 'revision', 'scope', 'sequence'].sort().join(',');
            for (const row of rows) {
                if (Object.keys(row).sort().join(',') !== allowed || !(row.ciphertext instanceof Uint8Array) || row.ciphertext.length < 16 || row.nonce.length !== 12) throw new Error('Unexpected plaintext storage field');
            }
            for (const kind of ['keys', 'identity', 'events', 'plans', 'jobs', 'backup', 'backupkeys']) if (!rows.some(row => row.kind === kind)) throw new Error('Missing encrypted storage category');
            if (localStorage.length !== 0 || sessionStorage.length !== 0) throw new Error('Unexpected browser string storage');
        });
        await page.reload();
        await page.locator('#status').filter({ hasText: 'ready' }).waitFor();
        if (await run('identity') !== ids[0]) throw new Error('Reload identity changed');
        const completions = await Promise.all([run('race-retry'), run('race-retry', second)]);
        if (completions.sort().join(',') !== 'ok:0,ok:1') throw new Error('Concurrent delivery was not serialized');
        await second.close();
        await run('no-ack');
        await page.evaluate(() => {
            globalThis.originalChatPut = IDBObjectStore.prototype.put;
            IDBObjectStore.prototype.put = function (value, ...args) {
                if (this.name === 'records' && value.kind === 'events') throw new DOMException('synthetic-marker', 'QuotaExceededError');
                return globalThis.originalChatPut.call(this, value, ...args);
            };
        });
        await run('quota-no-ack');
        await page.evaluate(() => { IDBObjectStore.prototype.put = globalThis.originalChatPut; });
        await run('revoke');
        if (await run('load') !== 'ok:Revoked') throw new Error('Revocation not retained');
        if (errors.length) throw new Error('Browser emitted runtime or CSP errors');
        await context.close();
        for (const fault of ['corrupt', 'missing', 'unavailable', 'interrupted', 'finalizing']) {
            const faultContext = await browser.newContext();
            const target = await faultContext.newPage();
            await open(target);
            if (fault === 'unavailable') {
                await target.evaluate(() => { Object.defineProperty(globalThis, 'indexedDB', { value: { open() { throw new DOMException('synthetic-marker', 'SecurityError'); } } }); });
                if (await run('probe', target) !== 'ok:vault-unavailable') throw new Error('Unavailable storage was not rejected');
            } else {
                await run('prepare', target);
                if (fault === 'interrupted' || fault === 'finalizing') {
                    await target.evaluate(action => { globalThis.runChatTest(action).catch(() => {}); }, fault === 'interrupted' ? 'pause-create' : 'pause-finalize');
                    await target.waitForFunction(() => globalThis.chatCreationReserved === true);
                    const expectedDevice = await target.evaluate(() => globalThis.chatExpectedDevice);
                    await target.reload();
                    await target.locator('#status').filter({ hasText: 'ready' }).waitFor();
                    if (fault === 'interrupted') {
                        if (await run('load', target) !== 'ok:RecoveryRequired') throw new Error('Interrupted identity was replaced');
                    } else {
                        if (await run('load', target) !== 'ok:Ready' || await run('identity', target) !== `ok:${expectedDevice}`) throw new Error('Interrupted finalization changed the device');
                    }
                } else {
                    await run('identity', target);
                    await target.evaluate(async fault => {
                        const db = await new Promise(resolve => { const r = indexedDB.open('Skopka.Chat.Browser.v1'); r.onsuccess = () => resolve(r.result); });
                        await new Promise((resolve, reject) => {
                            const tx = db.transaction('records', 'readwrite');
                            const cursor = tx.objectStore('records').openCursor();
                            cursor.onsuccess = () => {
                                const row = cursor.result;
                                if (!row) return;
                                if (row.value.kind === 'keys') {
                                    if (fault === 'missing') row.delete();
                                    else { const value = row.value; value.ciphertext[0] ^= 1; row.update(value); }
                                } else row.continue();
                            };
                            tx.oncomplete = resolve; tx.onerror = reject;
                        });
                        db.close();
                    }, fault);
                    const expected = fault === 'corrupt' ? 'ok:Corrupt' : 'ok:RecoveryRequired';
                    if (await run('load', target) !== expected) throw new Error('Lost/corrupt keys were replaced');
                }
            }
            console.log(`${engine.name()}: ${fault} storage recovery passed`);
            await faultContext.close();
        }
    } finally { await browser.close(); }
}
