import assert from 'node:assert/strict';
import { readFile, access } from 'node:fs/promises';
import { examples } from './examples.mjs';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
execFileSync(process.execPath, [fileURLToPath(new URL('./generate.mjs', import.meta.url)), '--check'], { stdio:'inherit' });
const html = await readFile(new URL('./index.html', import.meta.url), 'utf8');
for (const page of ['index.html', 'playground.html']) {
const html = await readFile(new URL(page, import.meta.url), 'utf8');
const ids = [...html.matchAll(/\bid="([^"]+)"/g)].map(match => match[1]);
assert.equal(new Set(ids).size, ids.length, 'Duplicate document IDs.');
for (const [, reference] of html.matchAll(/(?:href|src)="([^"]+)"/g)) {
  if (reference.startsWith('#')) assert.ok(ids.includes(reference.slice(1)), `Broken anchor ${reference}`);
  else if (!reference.startsWith('https:')) {
    const [file, anchor] = reference.split('#');
    const target = new URL(file, import.meta.url);
    await access(target);
    if (anchor) assert.ok((await readFile(target, 'utf8')).includes(`id="${anchor}"`), `Broken page anchor ${reference}`);
  }
}
}
const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
for (const [name, code] of Object.entries(examples)) if (name !== 'build') new AsyncFunction(code.replace(/^import .*;\n/gm, ''));
assert.equal(html.includes('<!-- example:'), false);
const preview = await readFile(new URL('./assets/receipt.html', import.meta.url), 'utf8');
assert.match(preview, /ENTREE BETA TEST/); assert.match(preview, /<svg/);
assert.doesNotMatch(preview, /<script|<iframe|<img|(?:src|href)=["']https?:\/\//i);
for (const file of ['playground.mjs', 'playground-data.mjs', 'app.js']) execFileSync(process.execPath, ['--check', fileURLToPath(new URL(file, import.meta.url))], { stdio:'inherit' });
execFileSync(process.execPath, ['--test', fileURLToPath(new URL('./playground.test.mjs', import.meta.url))], { stdio:'inherit' });
console.log(`Verified ${Object.keys(examples).length} guide snippets, both pages, local assets and the saved receipt.`);
