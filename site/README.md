# Usage guide

The standalone Entree Print documentation is a static site. It makes no requests to a print service and does not collect tokens or submit jobs.

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
