async function extractReceipt(width) {
  // Chromium's viewport is integer pixels; the physical receipt width is often fractional.
  // Size the document to that exact width so right alignment does not target a wider viewport.
  document.documentElement.style.setProperty('width', `${width}px`, 'important');
  await document.fonts.ready;
  const text = [], rectangles = [], keepTogether = [];
  const unsupported = new Set();
  const canvas = document.createElement('canvas');
  const measure = canvas.getContext('2d');
  let height = 1;
  const visible = element => {
    const style = getComputedStyle(element);
    return style.display !== 'none' && style.visibility === 'visible' && element.getClientRects().length;
  };
  const color = value => {
    const numbers = value.match(/[\d.]+/g)?.map(Number);
    if (!numbers || (numbers.length === 4 && numbers[3] === 0)) return null;
    if (numbers.length === 4 && numbers[3] !== 1) unsupported.add('translucent colors');
    return '#' + numbers.slice(0, 3).map(n => Math.round(n).toString(16).padStart(2, '0')).join('');
  };
  const rectangle = (x, y, w, h, fill, dotDpi = null) => { if (fill && w > 0 && h > 0) rectangles.push({ x, y, width: w, height: h, color: fill, dotDpi }); };
  if (document.querySelector('script,link[rel="stylesheet"],style')) {
    if (document.querySelector('script,link[rel="stylesheet"]')) unsupported.add('scripts or external stylesheets');
    if ([...document.querySelectorAll('style')].some(el => /@font-face|@import/i.test(el.textContent))) unsupported.add('web fonts or imported styles');
  }
  for (const element of [document.body, ...document.body.querySelectorAll('*')]) {
    if (!visible(element)) continue;
    const style = getComputedStyle(element), r = element.getBoundingClientRect();
    if (['IMG','SVG','CANVAS','IFRAME','VIDEO','INPUT','TEXTAREA','SELECT'].includes(element.tagName)) unsupported.add(element.tagName.toLowerCase());
    if (style.transform !== 'none' || style.backgroundImage !== 'none' || style.boxShadow !== 'none' || style.textShadow !== 'none' || style.opacity !== '1') unsupported.add('transforms, images, shadows or opacity');
    if (style.direction !== 'ltr' || style.writingMode !== 'horizontal-tb') unsupported.add('right-to-left or vertical text');
    if (style.letterSpacing !== 'normal' && parseFloat(style.letterSpacing) !== 0) unsupported.add('letter spacing');
    if ((style.wordSpacing !== 'normal' && parseFloat(style.wordSpacing) !== 0) || ['hidden','clip','scroll','auto'].includes(style.overflowX) || ['hidden','clip','scroll','auto'].includes(style.overflowY)) unsupported.add('word spacing or clipped/scrolling content');
    if (style.textDecorationLine !== 'none' || style.textTransform !== 'none') unsupported.add('text decoration or text transform');
    if (parseFloat(style.borderTopLeftRadius) > 0) unsupported.add('rounded borders');
    for (const pseudo of ['::before', '::after']) {
      const content = getComputedStyle(element, pseudo).content;
      if (content !== 'none' && content !== 'normal' && content !== '""') unsupported.add('generated content');
    }
    height = Math.max(height, r.bottom);
    if (['avoid', 'avoid-page'].includes(style.breakInside) && r.height > 0)
      keepTogether.push({ y: r.y, height: r.height });
    const dotDpi = element.hasAttribute('data-entree-dot-dpi') ? Number(element.getAttribute('data-entree-dot-dpi')) : null;
    if (dotDpi !== null && (!Number.isInteger(dotDpi) || dotDpi < 150 || dotDpi > 1200)) unsupported.add('invalid code dot resolution');
    if (dotDpi !== null) {
      const dots = (element.getAttribute('data-entree-dot-box') || '').trim().split(/\s+/).map(Number);
      if (dots.length !== 4 || dots.some(n => !Number.isSafeInteger(n) || n < 0 || n > 100000) || dots[2] <= 0 || dots[3] <= 0) {
        unsupported.add('invalid code dot geometry');
      } else {
        // Snap the shared code origin once. Preserve encoder dot counts instead of
        // rounding each browser-quantized bar independently around a half-dot origin.
        const origin = element.parentElement.getBoundingClientRect(), unit = 96 / dotDpi;
        rectangle((Math.round(origin.x / unit) + dots[0]) * unit, (Math.round(origin.y / unit) + dots[1]) * unit,
          dots[2] * unit, dots[3] * unit, color(style.backgroundColor), dotDpi);
      }
    } else rectangle(r.x, r.y, r.width, r.height, color(style.backgroundColor));
    for (const side of ['Top','Right','Bottom','Left']) {
      const thickness = parseFloat(style['border' + side + 'Width']);
      if (!thickness) continue;
      if (style['border' + side + 'Style'] !== 'solid') { unsupported.add('non-solid borders'); continue; }
      const horizontal = side === 'Top' || side === 'Bottom';
      rectangle(r.x + (side === 'Right' ? r.width - thickness : 0), r.y + (side === 'Bottom' ? r.height - thickness : 0),
        horizontal ? r.width : thickness, horizontal ? thickness : r.height, color(style['border' + side + 'Color']));
    }
  }
  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
  let node;
  while ((node = walker.nextNode())) {
    if (!visible(node.parentElement) || ['STYLE','SCRIPT'].includes(node.parentElement.tagName)) continue;
    const style = getComputedStyle(node.parentElement);
    const range = document.createRange();
    let line = null;
    const flush = () => {
      if (!line || !line.text.trim()) { line = null; return; }
      measure.font = `${style.fontStyle} ${style.fontWeight} ${style.fontSize} ${style.fontFamily}`;
      const descent = measure.measureText(line.text).fontBoundingBoxDescent ?? parseFloat(style.fontSize) * 0.2;
      keepTogether.push({ y: line.top, height: line.bottom - line.top });
      text.push({ text: line.text, x: line.left, baseline: line.bottom - descent, width: line.right - line.left,
        size: parseFloat(style.fontSize), family: style.fontFamily,
        bold: Number(style.fontWeight) >= 600, italic: style.fontStyle !== 'normal', color: color(style.color) || '#000000' });
      height = Math.max(height, line.bottom);
      line = null;
    };
    // Grapheme ranges retain combining marks and surrogate pairs; group complete visual lines for shaping.
    for (const part of new Intl.Segmenter(undefined, { granularity: 'grapheme' }).segment(node.textContent)) {
      range.setStart(node, part.index); range.setEnd(node, part.index + part.segment.length);
      const r = range.getBoundingClientRect();
      if (r.width < 0.01 || r.height < 0.01) continue;
      if (line && (Math.abs(line.top - r.top) > 0.5 || r.left < line.left - 0.5)) flush();
      if (!line) line = { text: '', top: r.top, bottom: r.bottom, left: r.left, right: r.right };
      line.text += /^(normal|nowrap|pre-line)$/.test(style.whiteSpace) ? part.segment.replace(/\s/g, ' ') : part.segment;
      line.right = Math.max(line.right, r.right);
    }
    flush();
  }
  if (unsupported.size) throw new Error('Unsupported receipt layout: ' + [...unsupported].join(', ') + '.');
  if (!text.length && !rectangles.length) throw new Error('Receipt has no visible content.');
  return { width, height: Math.ceil(height + 4), text, rectangles, keepTogether };
}
