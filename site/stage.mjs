import { mkdir, copyFile } from 'node:fs/promises';
const root = new URL('./_site/', import.meta.url);
await mkdir(new URL('assets/', root), { recursive: true });
for (const file of ['index.html', 'styles.css', 'app.js', '.nojekyll', 'assets/printer.png', 'assets/receipt.svg', 'assets/receipt.html'])
  await copyFile(new URL(file, import.meta.url), new URL(file, root));
console.log('Staged the public usage guide in site/_site.');
