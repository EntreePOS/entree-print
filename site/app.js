(() => {
  const root = document.documentElement, theme = document.querySelector('.theme');
  let preference;
  try { preference = localStorage.getItem('entree-print-docs-theme'); } catch {}
  const media = matchMedia('(prefers-color-scheme: dark)');
  function apply(value) {
    root.dataset.theme = value;
    theme.textContent = value === 'dark' ? 'Light' : 'Dark';
    theme.setAttribute('aria-label', `Switch to ${value === 'dark' ? 'light' : 'dark'} theme`);
  }
  apply(preference || (media.matches ? 'dark' : 'light'));
  theme.addEventListener('click', () => {
    preference = root.dataset.theme === 'dark' ? 'light' : 'dark'; apply(preference);
    try { localStorage.setItem('entree-print-docs-theme', preference); } catch {}
  });
  media.addEventListener('change', () => { if (!preference) apply(media.matches ? 'dark' : 'light'); });
  document.querySelectorAll('.copy').forEach(button => button.addEventListener('click', async () => {
    const code = button.closest('.code-panel').querySelector('code');
    try {
      await navigator.clipboard.writeText(code.textContent);
      button.textContent = 'Copied'; document.querySelector('#copy-status').textContent = 'Code copied.';
    } catch {
      const selection = getSelection(), range = document.createRange(); range.selectNodeContents(code);
      selection.removeAllRanges(); selection.addRange(range);
      button.textContent = 'Selected'; document.querySelector('#copy-status').textContent = 'Code selected. Use your copy shortcut.';
    }
    setTimeout(() => { button.textContent = 'Copy'; }, 1800);
  }));
  const search = document.querySelector('#topic-search'), links = [...document.querySelectorAll('.contents nav a')];
  search.addEventListener('input', () => {
    const query = search.value.trim().toLowerCase();
    links.forEach(link => { link.hidden = !`${link.textContent} ${link.dataset.keywords}`.toLowerCase().includes(query); });
    document.querySelector('.search-empty').hidden = links.some(link => !link.hidden);
  });
  document.addEventListener('keydown', event => {
    if (event.key === '/' && !['INPUT','TEXTAREA'].includes(document.activeElement.tagName) && !document.activeElement.isContentEditable) {
      event.preventDefault(); search.focus();
    }
  });
  const observer = new IntersectionObserver(entries => {
    const visible = entries.filter(entry => entry.isIntersecting).sort((a, b) => a.boundingClientRect.top - b.boundingClientRect.top)[0];
    if (!visible) return;
    links.forEach(link => {
      if (link.hash === `#${visible.target.id}`) link.setAttribute('aria-current', 'location');
      else link.removeAttribute('aria-current');
    });
  }, { rootMargin:'-15% 0px -65% 0px' });
  document.querySelectorAll('.guide-body section').forEach(section => observer.observe(section));
})();
