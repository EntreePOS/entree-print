import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
const types = { '/':'text/html', '/index.html':'text/html', '/styles.css':'text/css', '/app.js':'text/javascript',
  '/playground.html':'text/html', '/playground.css':'text/css', '/playground.mjs':'text/javascript', '/playground-data.mjs':'text/javascript',
  '/sdk/entree-print.mjs':'text/javascript', '/sdk/outbox.mjs':'text/javascript',
  '/assets/printer.png':'image/png', '/assets/receipt.html':'text/html', '/assets/receipt.svg':'image/svg+xml' };
const port = Number(process.env.ENTREE_DOCS_PORT || 19780);
const server = createServer(async (request, response) => {
  const path = new URL(request.url, 'http://127.0.0.1').pathname;
  if (!types[path] || !['GET','HEAD'].includes(request.method)) { response.writeHead(404); response.end('Not found'); return; }
  try {
    const body = await readFile(new URL((path.startsWith('/sdk/') ? '..' : '.') + (path === '/' ? '/index.html' : path), import.meta.url));
    response.writeHead(200, { 'Content-Type':types[path] + (types[path].startsWith('text/') ? '; charset=utf-8' : ''), 'X-Content-Type-Options':'nosniff', 'Cache-Control':'no-store' });
    response.end(request.method === 'HEAD' ? undefined : body);
  } catch { response.writeHead(500); response.end('Asset unavailable'); }
});
server.listen(port, '127.0.0.1', () => console.log(`Entree Print documentation: http://127.0.0.1:${port}`));
