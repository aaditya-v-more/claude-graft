const copy = document.documentElement.cloneNode(true);
const original = [...document.documentElement.querySelectorAll('*')];
const cloned = [...copy.querySelectorAll('*')];
const styleKeys = ['color', 'background-color', 'font-family', 'font-size', 'font-weight', 'font-style',
  'text-align', 'text-decoration', 'line-height', 'border-collapse', 'border-color', 'border-style',
  'border-width', 'padding', 'margin', 'white-space'];
for (let i = 0; i < cloned.length; i++) {
  const node = cloned[i];
  const style = getComputedStyle(original[i]);
  if (style.display === 'none' || style.visibility === 'hidden') { node.remove(); continue; }
  for (const attribute of [...node.attributes]) {
    if (attribute.name.startsWith('on') || ['src', 'srcset', 'poster', 'background', 'style', 'action', 'formaction'].includes(attribute.name)) {
      if (attribute.name === 'src' && node.tagName === 'IMG' && /^data:image\/(png|jpeg|gif|webp);base64,/i.test(attribute.value)) continue;
      node.removeAttribute(attribute.name);
    }
  }
  if (node.tagName === 'A' && !/^https?:\/\//i.test(node.getAttribute('href') || '')) node.removeAttribute('href');
  for (const key of styleKeys) node.style.setProperty(key, style.getPropertyValue(key));
}
copy.querySelectorAll('script, style, link, base, meta, noscript, iframe, object, embed, template, svg, canvas, video, audio, source, input, button, select, textarea').forEach(e => e.remove());
copy.querySelectorAll('img:not([src])').forEach(e => e.remove());
return '<!doctype html><html><head><meta charset="utf-8"></head>' + copy.querySelector('body').outerHTML + '</html>';
