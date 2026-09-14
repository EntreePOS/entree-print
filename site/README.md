# Usage guide

The standalone Entree Print documentation is a static site. The guide makes no print-service requests. The playground makes requests only after the developer connects, and uses the real browser SDK for printer inventory and rendering. It never submits jobs. Tokens stay in memory; they are not included in copied code, URLs or storage.

Edit `index.template.html` and `examples.mjs`, then regenerate and verify:

```powershell
node site/generate.mjs
node site/verify.mjs
node site/serve.mjs
```

The development server listens only on `127.0.0.1:19780`. `ENTREE_DOCS_PORT` selects another port. It serves an explicit list of public assets, not arbitrary workspace files.

`generate.mjs` embeds escaped code snippets into the HTML and extracts the original SVG from the saved synthetic cashier preview. The SVG drawing is unchanged; it scales to fit the documentation view. `verify.mjs` checks snippet freshness and JavaScript syntax, duplicate IDs, anchor targets, local assets and absence of active/external content in the receipt fixture. These checks do not print tickets or prove physical output.

`stage.mjs` copies only the public files into `site/_site`. The GitHub Pages workflow verifies and publishes that directory on changes to `main`, or by manual dispatch. The release notice stays in development state until an installer and the 0.0.1 milestone have been verified.

Manual UI verification must cover desktop/mobile, light/dark themes, topic search including empty results, code copy feedback, disclosure panels and links. See DESIGN.md for the design decisions.

## Playground

Open `/playground.html` for editable receipt, kitchen and QR/barcode presets. Content is JSON, never executable JavaScript. The sandboxed browser draft uses an inert HTML subset and a restrictive CSP; code blocks are labeled placeholders until the service generates them. Editing content, printer or width discards the previous service result, including any in-flight result. The service preview and downloaded HTML come from `ticket.render()`.

The playground generates an application example with a separate operator-triggered print function. It does not print, open drawers or send raw commands. Actual browser-to-service rendering needs a reachable compatible service, its token, and permission for the page origin. HTTPS hosting can restrict access to the current HTTP service; the loopback development server is available for local testing. Do not disable browser security to work around network restrictions.

The site stages `sdk/entree-print.mjs` and `sdk/outbox.mjs` directly from the SDK source; no forked copy is maintained. Changes to those SDK files trigger Pages publication too. Run `node --test site/playground.test.mjs` for content/code-generation checks (also included by `verify.mjs`).
