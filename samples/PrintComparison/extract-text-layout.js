async function extractReceipt(width) {
  await document.fonts.ready;
  const text = [], rectangles = [];
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
  const rectangle = (x, y, w, h, fill) => { if (fill && w > 0 && h > 0) rectangles.push({ x, y, width: w, height: h, color: fill }); };
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
    rectangle(r.x, r.y, r.width, r.height, color(style.backgroundColor));
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
  if (unsupported.size) throw new Error('D prototype does not support: ' + [...unsupported].join(', ') + '. Use B for this receipt.');
  if (!text.length) throw new Error('Receipt has no visible text.');
  return { width, height: Math.ceil(height + 4), text, rectangles };
}
