import config from './config.js';
import en from '../i18n/en.js';
import { initFlasher } from './flasher.js';

const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];

const LANG_KEY = 'cyd-lang';

function pickLang() {
  const fromUrl = new URLSearchParams(location.search).get('lang');
  if (fromUrl === 'vi' || fromUrl === 'en') {
    try { localStorage.setItem(LANG_KEY, fromUrl); } catch {}
    return fromUrl;
  }
  try {
    const saved = localStorage.getItem(LANG_KEY);
    if (saved === 'vi' || saved === 'en') return saved;
  } catch {}
  return (navigator.language || 'vi').toLowerCase().startsWith('vi') ? 'vi' : 'en';
}

const lang = pickLang();

function applyEnglish() {
  document.documentElement.lang = 'en';
  for (const el of $$('[data-i18n]')) {
    const v = en[el.dataset.i18n];
    if (v != null) el.innerHTML = v;
  }
  for (const el of $$('[data-i18n-aria]')) {
    const v = en[el.dataset.i18nAria];
    if (v != null) el.setAttribute('aria-label', v);
  }
  for (const el of $$('[data-i18n-alt]')) {
    const v = en[el.dataset.i18nAlt];
    if (v != null) el.setAttribute('alt', v);
  }
  if (en['meta.title']) document.title = en['meta.title'];
  const desc = $('meta[name="description"]');
  if (desc && en['meta.description']) desc.content = en['meta.description'];
}

function applyLang() {
  if (lang === 'en') applyEnglish();
  for (const img of $$('[data-shot-src]')) {
    const src = img.dataset.shotSrc.replace('{lang}', lang);
    if (img.getAttribute('src') !== src) img.srcset = src;
  }
  const other = lang === 'en' ? 'vi' : 'en';
  for (const a of $$('[data-lang-link]')) {
    a.href = `?lang=${other}`;
    a.hreflang = other;
    a.lang = other;
    a.addEventListener('click', (e) => {
      e.preventDefault();
      try { localStorage.setItem(LANG_KEY, other); } catch {}
      location.href = `${location.pathname}?lang=${other}${location.hash}`;
    });
  }
}

function repoUrl(path = '') {
  return `https://github.com/${config.repo}${path}`;
}

function applyConfig() {
  for (const a of $$('[data-repo-link]')) a.href = repoUrl(a.dataset.repoLink || '');
  for (const a of $$('[data-link]')) {
    const url = config.links && config.links[a.dataset.link];
    const li = a.closest('li');
    if (url) {
      a.href = url; a.target = '_blank'; a.rel = 'noopener'; a.hidden = false;
      if (li) li.hidden = false;
    } else {
      a.hidden = true;
      if (li) li.hidden = true;
    }
  }
}

applyLang();
applyConfig();
initFlasher({ config, getLang: () => lang, onLangChange: () => {} });
