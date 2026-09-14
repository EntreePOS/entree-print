import { readFile, writeFile } from 'node:fs/promises';
import { examples } from './examples.mjs';
const escape = value => value.replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;');
const template = await readFile(new URL('./index.template.html', import.meta.url), 'utf8');
const html = template.replace(/<!-- example:(\w+) -->/g, (_, key) => {
  if (!examples[key]) throw new Error(`Missing example ${key}`);
  return `<div class="code-panel" data-example="${key}"><div class="code-toolbar"><span>${key === 'build' ? 'PowerShell' : 'JavaScript'}</span><button class="copy" type="button" aria-label="Copy ${key} example">Copy</button></div><pre tabindex="0"><code>${escape(examples[key])}</code></pre></div>`;
});
const target = new URL('./index.html', import.meta.url);
const preview = await readFile(new URL('./assets/receipt.html', import.meta.url), 'utf8');
const svg = preview.match(/<svg[\s\S]*<\/svg>/)?.[0];
if (!svg) throw new Error('The saved receipt SVG is missing.');
const svgTarget = new URL('./assets/receipt.svg', import.meta.url);
if (process.argv.includes('--check')) {
  if (await readFile(target, 'utf8') !== html) throw new Error('Site snippets are stale. Run node site/generate.mjs.');
  if (await readFile(svgTarget, 'utf8') !== svg) throw new Error('The receipt image differs from the saved renderer artifact.');
  console.log('Site snippets match their source.');
} else { await writeFile(target, html); await writeFile(svgTarget, svg); console.log('Generated site/index.html and the unchanged receipt SVG.'); }
