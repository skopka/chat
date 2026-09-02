// Published-WASM gate. Native fixtures contain synthetic keys only; never deploy browser-tests/wwwroot.
import { spawn, spawnSync } from 'node:child_process';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';
const root = fileURLToPath(new URL('../../', import.meta.url));
const node = process.execPath;
const run = (program, args) => {
    const result = spawnSync(program, args, { cwd: root, stdio: 'inherit', windowsHide: true });
    if (result.status !== 0) throw new Error('Browser gate command failed.');
};
const children = [];
async function start(program, args, url) {
    let occupied = false;
    try { occupied = (await fetch(url)).ok; } catch { /* expected before startup */ }
    if (occupied) throw new Error('Browser gate port is already in use; stop the previous local test host.');
    const child = spawn(program, args, { cwd: root, stdio: 'ignore', windowsHide: true });
    children.push(child);
    for (let attempt = 0; attempt < 100; attempt++) {
        if (child.exitCode !== null) throw new Error('Browser gate host failed.');
        try { if ((await fetch(url)).ok) return; } catch { /* bounded startup wait */ }
        await new Promise(resolve => setTimeout(resolve, 100));
    }
    throw new Error('Browser gate host startup timed out.');
}
const publish = (project, output, extra = []) => run('dotnet', ['publish', project, '-c', 'Release', '-o', output, '--configfile', 'NuGet.Config', ...extra]);
const fixture = (...args) => run('dotnet', ['run', '--project', 'tests/Skopka.Chat.Browser.Fixtures', '-c', 'Release', '--', ...args]);
try {
    const vendor = new URL('../../src/Skopka.Chat.Client.Browser/wwwroot/vendor/', import.meta.url);
    for (const line of (await readFile(new URL('SHA256SUMS', vendor), 'utf8')).trim().split('\n')) {
        const [expected, name] = line.trim().split(/\s+/);
        if (createHash('sha256').update(await readFile(new URL(name, vendor))).digest('hex') !== expected) throw new Error('Vendored cryptography checksum mismatch.');
    }
    publish('tests/Skopka.Chat.Browser.Tests', 'artifacts/browser-tests');
    fixture('generate', 'artifacts/browser-tests/wwwroot/test-vectors.json');
    await start(node, ['eng/browser/serve.mjs', 'artifacts/browser-tests/wwwroot', '5190'], 'http://127.0.0.1:5190/');
    run(node, ['eng/browser/browser-tests.mjs']);
    for (const browser of ['chromium', 'firefox']) fixture('verify', 'artifacts/browser-tests/wwwroot/test-vectors.json', `artifacts/browser-results/${browser}-interop.json`);

    const version = process.env.CHAT_BROWSER_PACKAGE_VERSION;
    if (version) {
        if (!/^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$/.test(version)) throw new Error('Invalid local package version.');
        const feed = resolve(root, process.env.CHAT_BROWSER_PACKAGE_FEED ?? 'artifacts/packages');
        const cache = resolve(root, 'artifacts/browser-consumer/cache', version);
        await mkdir(resolve(root, 'artifacts/browser-consumer'), { recursive: true });
        const escaped = feed.replaceAll('&', '&amp;').replaceAll('"', '&quot;');
        await writeFile(resolve(root, 'artifacts/browser-consumer/NuGet.Config'),
            `<configuration><packageSources><clear/><add key="local-chat" value="${escaped}"/><add key="nuget" value="https://api.nuget.org/v3/index.json"/></packageSources><packageSourceMapping><packageSource key="local-chat"><package pattern="Skopka.Chat*"/></packageSource><packageSource key="nuget"><package pattern="*"/></packageSource></packageSourceMapping></configuration>`);
        run('dotnet', ['publish', 'samples/Skopka.Chat.Browser.Sample', '-c', 'Release', '-o', 'artifacts/browser-publish',
            '--configfile', 'artifacts/browser-consumer/NuGet.Config', '-p:UseChatPackages=true', `-p:ChatPackageVersion=${version}`,
            `-p:RestorePackagesPath=${cache}`]);
        run('dotnet', ['restore', 'tests/Skopka.Chat.PackageConsumer', '--configfile', 'artifacts/browser-consumer/NuGet.Config',
            `-p:ChatPackageVersion=${version}`, `-p:RestorePackagesPath=${cache}`]);
        run('dotnet', ['run', '--project', 'tests/Skopka.Chat.PackageConsumer', '-c', 'Release', '--no-restore',
            `-p:ChatPackageVersion=${version}`, `-p:RestorePackagesPath=${cache}`]);
    } else {
        publish('samples/Skopka.Chat.Browser.Sample', 'artifacts/browser-publish');
    }
    const assets = JSON.parse(await readFile(resolve(root, 'samples/Skopka.Chat.Browser.Sample/obj/project.assets.json'), 'utf8'));
    if (Object.keys(assets.libraries).some(name => /^(NSec\.Cryptography|libsodium|Microsoft\.AspNetCore\.App\/|Skopka\.Chat\.(?:Server|Persistence|Bots|Client\.Storage\.Sqlite)(?:\.|\/))/i.test(name))) throw new Error('Browser dependency graph contains a forbidden native/server asset.');
    run('dotnet', ['build', 'samples/Skopka.Chat.Browser.Host', '-c', 'Release', '--configfile', 'NuGet.Config']);
    await start('dotnet', ['samples/Skopka.Chat.Browser.Host/bin/Release/net10.0/Skopka.Chat.Browser.Host.dll', '--webroot', resolve(root, 'artifacts/browser-publish/wwwroot')], 'http://127.0.0.1:5200/');
    run(node, ['eng/browser/sample-tests.mjs']);
    console.log('Published browser gate passed: Chromium + Firefox, native interop, storage/crash/retry and cookie BFF sample.');
} finally {
    for (const child of children) child.kill();
    if (process.env.CHAT_BROWSER_PACKAGE_VERSION) run('dotnet', ['restore', 'samples/Skopka.Chat.Browser.Sample', '--configfile', 'NuGet.Config']);
}
