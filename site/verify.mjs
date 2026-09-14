import assert from 'node:assert/strict';
import { readFile, access } from 'node:fs/promises';
import { examples } from './examples.mjs';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
execFileSync(process.execPath, [fileURLToPath(new URL('./generate.mjs', import.meta.url)), '--check'], { stdio:'inherit' });
const html = await readFile(new URL('./index.html', import.meta.url), 'utf8');
const ids = [...html.matchAll(/\bid="([^"]+)"/g)].map(match => match[1]);
assert.equal(new Set(ids).size, ids.length, 'Duplicate document IDs.');
for (const [, reference] of html.matchAll(/(?:href|src)="([^"]+)"/g)) {
  if (reference.startsWith('#')) assert.ok(ids.includes(reference.slice(1)), `Broken anchor ${reference}`);
  else if (!reference.startsWith('https:')) await access(new URL(reference, import.meta.url));
}
const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
for (const [name, code] of Object.entries(examples)) if (name !== 'build') new AsyncFunction(code.replace(/^import .*;\n/gm, ''));
assert.equal(html.includes('<!-- example:'), false);
const preview = await readFile(new URL('./assets/receipt.html', import.meta.url), 'utf8');
assert.match(preview, /ENTREE BETA TEST/); assert.match(preview, /<svg/);
assert.doesNotMatch(preview, /<script|<iframe|<img|(?:src|href)=["']https?:\/\//i);
console.log(`Verified ${Object.keys(examples).length} snippets, ${ids.length} anchors, local assets and the saved receipt.`);
