import { createServer } from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import { resolve, sep, extname } from 'node:path';
const root = resolve(process.argv[2] ?? '../../artifacts/browser-publish/wwwroot');
const types = { '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript', '.json': 'application/json', '.wasm': 'application/wasm', '.css': 'text/css', '.dat': 'application/octet-stream' };
createServer(async (request, response) => {
  try {
    const url = new URL(request.url, 'http://127.0.0.1');
    const path = resolve(root, '.' + decodeURIComponent(url.pathname === '/' ? '/index.html' : url.pathname));
    if (!path.startsWith(root + sep) || !(await stat(path)).isFile()) { response.writeHead(404).end(); return; }
    response.writeHead(200, {
      'Content-Type': types[extname(path)] ?? 'application/octet-stream',
      'Content-Security-Policy': "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'",
      'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff'
    });
    response.end(await readFile(path));
  } catch { response.writeHead(404).end(); }
}).listen(Number(process.argv[3] ?? 5189), '127.0.0.1', () => console.log('Browser fixture listening on loopback'));
