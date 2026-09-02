import { chromium, firefox } from 'playwright';
const origin = 'http://127.0.0.1:5200/';
const phrase = 'synthetic sample vault phrase never sent';
const message = 'synthetic sample text stays E2EE 🔐';
const offlineMessage = 'synthetic queued while offline';
const interruptedMessage = 'synthetic interrupted HTTP delivery';
const sensitive = [phrase, message, offlineMessage, interruptedMessage];
for (const engine of [chromium, firefox]) {
    const browser = await engine.launch();
    const failures = [];
    try {
        const aliceContext = await browser.newContext();
        const bobContext = await browser.newContext();
        const alice = await aliceContext.newPage();
        const bob = await bobContext.newPage();
        for (const page of [alice, bob]) {
            page.on('pageerror', () => failures.push('pageerror'));
            page.on('console', event => {
                if (sensitive.some(value => event.text().includes(value)) || event.text().includes('violates the following Content Security Policy')) failures.push('sensitive log or CSP');
            });
            page.on('request', request => {
                if (sensitive.some(value => (request.postData() ?? '').includes(value)) || request.headers().authorization) failures.push('plaintext/token network disclosure');
            });
        }
        const status = async (page, text) => { await page.locator('#status').filter({ hasText: text }).waitFor({ timeout: 30000 }); };
        const connect = async (page, account) => {
            await page.goto(origin);
            await status(page, 'Browser ready');
            await page.locator('select').selectOption(account);
            await page.locator('#login').click();
            await status(page, 'Logged in');
            await page.locator('#phrase').fill(phrase);
            await page.locator('#create-vault').click();
            await status(page, 'Identity: Absent');
            await page.locator('#create-device').click();
            await status(page, 'Identity: Ready');
            const id = await page.locator('#device').textContent();
            await page.locator('#bind').click();
            await status(page, 'Session bound');
            await page.locator('#conversation').click();
            await status(page, 'Conversation opened');
            return id;
        };
        const reloadAndBind = async (page, id) => {
            await page.reload();
            await status(page, 'Session restored');
            await page.locator('#phrase').fill(phrase);
            await page.locator('#unlock').click();
            await status(page, 'Identity: Ready');
            if (await page.locator('#device').textContent() !== id) throw new Error('Device changed on reload');
            await page.locator('#bind').click();
            await status(page, 'Session bound');
            await page.locator('#conversation').click();
            await status(page, 'Conversation opened');
        };
        const sync = async page => {
            await page.locator('#sync').click();
            await status(page, 'Synchronized');
        };
        const aliceId = await connect(alice, 'alice');
        const bobId = await connect(bob, 'bob');
        await alice.locator('#message').fill(message);
        await alice.locator('#send').click();
        await status(alice, 'Queued messages delivered');
        await bob.locator('#sync').click();
        await status(bob, 'Synchronized');
        await bob.getByText(message, { exact: true }).waitFor();
        await aliceContext.setOffline(true);
        await alice.locator('#message').fill(offlineMessage);
        await alice.locator('#send').click();
        await status(alice, 'Operation failed');
        if (await alice.locator('#message').inputValue() !== '') throw new Error('Offline job was not durably queued');
        await aliceContext.setOffline(false);
        await reloadAndBind(alice, aliceId);
        await sync(alice);
        await sync(bob);
        await bob.getByText(offlineMessage, { exact: true }).waitFor();

        // Hold a real server acceptance response, then destroy the WASM runtime before it can record acceptance.
        // After rebind the outbox must retry the byte-identical envelope, not re-encrypt the message.
        const accepted = Promise.withResolvers();
        const resume = Promise.withResolvers();
        let interruptedBody;
        const retriedBodies = [];
        const routePattern = '**/skopka-chat/v1/envelopes';
        await aliceContext.route(routePattern, async route => {
            if (route.request().method() !== 'POST' || interruptedBody) { await route.continue(); return; }
            interruptedBody = route.request().postData();
            try {
                const response = await route.fetch();
                if (!response.ok()) throw new Error('Synthetic delivery was not accepted');
                accepted.resolve();
                await resume.promise;
                await route.fulfill({ response });
            } catch { accepted.reject(new Error('Interrupted delivery setup failed')); }
        });
        try {
            await alice.locator('#message').fill(interruptedMessage);
            await alice.locator('#send').click();
            await Promise.race([accepted.promise, new Promise((_, reject) => setTimeout(() => reject(new Error('No server acceptance')), 30000).unref())]);
            alice.on('request', request => {
                if (request.method() === 'POST' && request.url().endsWith('/skopka-chat/v1/envelopes')) retriedBodies.push(request.postData());
            });
            await reloadAndBind(alice, aliceId);
        } finally {
            resume.resolve();
            await aliceContext.unrouteAll({ behavior: 'wait' });
        }
        await sync(alice);
        if (!interruptedBody || !retriedBodies.includes(interruptedBody)) throw new Error('Retry changed the prepared envelope');
        await sync(bob);
        await bob.getByText(interruptedMessage, { exact: true }).waitFor();
        if (await bob.getByText(interruptedMessage, { exact: true }).count() !== 1) throw new Error('Duplicate delivery was projected twice');
        await reloadAndBind(bob, bobId);
        await bob.getByText(message, { exact: true }).waitFor();
        await bob.locator('#logout').click();
        await status(bob, 'Logged out and locked');
        await bob.locator('select').selectOption('bob');
        await bob.locator('#login').click();
        await status(bob, 'Logged in');
        await bob.locator('#phrase').fill(phrase);
        await bob.locator('#unlock').click();
        await status(bob, 'Identity: Ready');
        if (await bob.locator('#device').textContent() !== bobId) throw new Error('Device changed on relogin');
        await bob.locator('#bind').click();
        await status(bob, 'Session bound');
        const csrfStatus = await bob.evaluate(async () => (await fetch('/demo/logout', { method: 'POST' })).status);
        if (csrfStatus !== 403) throw new Error('Missing CSRF proof accepted');
        if (failures.length) throw new Error(failures.join(', '));
        console.log(`${engine.name()}: cookie login, enrollment, E2EE, offline queue, reload during HTTP send, exact retry, history, re-login/rebind and CSRF rejection passed`);
        await aliceContext.close(); await bobContext.close();
    } finally { await browser.close(); }
}
