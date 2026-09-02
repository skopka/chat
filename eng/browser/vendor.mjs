// Mechanical vendoring only. pnpm-lock.yaml pins npm integrity; runtime never accesses npm/CDNs.
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { createHash } from 'node:crypto';
const destination = new URL('../../src/Skopka.Chat.Client.Browser/wwwroot/vendor/', import.meta.url);
await mkdir(destination, { recursive: true });
const files = [
  ['libsodium-sumo/dist/modules-sumo-esm/libsodium-sumo.mjs', 'libsodium-sumo.mjs'],
  ['libsodium-wrappers-sumo/dist/modules-sumo-esm/libsodium-wrappers.mjs', 'libsodium-wrappers.mjs'],
  ['libsodium-sumo/LICENSE', 'libsodium-LICENSE'],
  ['libsodium-wrappers-sumo/LICENSE', 'wrappers-LICENSE']
];
const hashes = [];
for (const [source, name] of files) {
  let bytes = await readFile(new URL(`node_modules/${source}`, import.meta.url));
  if (name === 'libsodium-wrappers.mjs') {
    // Only rewrite the bare ESM import to the same-origin vendored file; no primitive code is changed.
    bytes = Buffer.from(bytes.toString().replace(/from(["'])libsodium-sumo\1/g, 'from"./libsodium-sumo.mjs"'));
  }
  await writeFile(new URL(name, destination), bytes);
  hashes.push(`${createHash('sha256').update(bytes).digest('hex')}  ${name}`);
}
await writeFile(new URL('SHA256SUMS', destination), hashes.join('\n') + '\n');
