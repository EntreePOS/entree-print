import { mkdir, copyFile } from 'node:fs/promises';
const root = new URL('./_site/', import.meta.url);
await mkdir(new URL('assets/', root), { recursive: true });
await mkdir(new URL('sdk/', root), { recursive: true });
for (const file of ['index.html', 'styles.css', 'app.js', 'playground.html', 'playground.css', 'playground.mjs', 'playground-data.mjs', '.nojekyll', 'assets/printer.png', 'assets/receipt.svg', 'assets/receipt.html'])
  await copyFile(new URL(file, import.meta.url), new URL(file, root));
for (const file of ['entree-print.mjs', 'outbox.mjs', 'events.mjs'])
  await copyFile(new URL('../sdk/' + file, import.meta.url), new URL('sdk/' + file, root));
console.log('Staged the public usage guide in site/_site.');
