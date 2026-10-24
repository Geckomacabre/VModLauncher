// GTA5-Mods.com extractor for V Mod Launcher.
// Injected into a hidden WebView2 that sits on https://www.gta5-mods.com. Reads pages with
// same-origin fetch + DOMParser (never navigates), at most one request per 1.1 s.
// Contract: docs/site-extractors.md
(function () {
  'use strict';
  const VERSION = 1;
  if (window.__vml && window.__vml.site === 'gta5mods' && window.__vml.version >= VERSION) return;

  const ORIGIN = 'https://www.gta5-mods.com';
  const MIN_GAP_MS = 1100;

  const CATEGORIES = [
    { id: 'all', name: 'All' },
    { id: 'scripts', name: 'Scripts' },
    { id: 'vehicles', name: 'Vehicles' },
    { id: 'player', name: 'Player' },
    { id: 'weapons', name: 'Weapons' },
    { id: 'maps', name: 'Maps' },
    { id: 'paintjobs', name: 'Paint Jobs' },
    { id: 'tools', name: 'Tools' },
    { id: 'misc', name: 'Misc' },
  ];

  // id '' = the site's default order ("Latest Versions")
  const SORTS = [
    { id: 'latest-versions', name: 'Latest versions', path: '' },
    { id: 'latest-uploads', name: 'Latest uploads', path: 'latest-uploads' },
    { id: 'most-downloaded', name: 'Most downloaded', path: 'most-downloaded' },
    { id: 'most-liked', name: 'Most liked', path: 'most-liked' },
    { id: 'highest-rated', name: 'Highest rated', path: 'highest-rated' },
  ];

  // ------------------------------------------------------------------ fetching

  let lastFetch = 0;
  let queue = Promise.resolve();
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

  function isChallenge(doc) {
    const title = (doc.title || '').toLowerCase();
    if (/checking your browser|just a moment|attention required|verify you are human|ddos-guard/.test(title)) return true;
    return !!doc.querySelector('.h-captcha, .g-recaptcha, #challenge-form, #cf-challenge-running, iframe[src*="captcha"], iframe[src*="challenges.cloudflare.com"]');
  }

  function getDoc(url) {
    const run = async () => {
      const wait = lastFetch + MIN_GAP_MS - Date.now();
      if (wait > 0) await sleep(wait);
      lastFetch = Date.now();
      const res = await fetch(url, { credentials: 'include', redirect: 'follow' });
      const text = await res.text();
      lastFetch = Date.now();
      const doc = new DOMParser().parseFromString(text, 'text/html');
      if (isChallenge(doc)) throw new Error('CHALLENGE: ' + url);
      if (!res.ok) throw new Error('HTTP ' + res.status + ' for ' + url);
      return { doc, url: res.url || url };
    };
    const p = queue.then(run, run);
    queue = p.catch(() => {});
    return p;
  }

  // ------------------------------------------------------------------ helpers

  function abs(u, base) {
    if (!u) return null;
    u = String(u).trim();
    if (!u || u.startsWith('data:') || u.startsWith('javascript:') || u.startsWith('#')) return null;
    if (u.startsWith('//')) u = 'https:' + u;
    try {
      const url = new URL(u, base || ORIGIN);
      if (url.protocol === 'http:') url.protocol = 'https:';
      return url.protocol === 'https:' ? url.href : null;
    } catch (e) { return null; }
  }

  function num(s) {
    if (s == null) return null;
    const m = String(s).replace(/,/g, '').match(/-?\d+(\.\d+)?/);
    return m ? Number(m[0]) : null;
  }

  const text = (el) => (el ? el.textContent.replace(/\s+/g, ' ').trim() : '');

  function editionsFrom(tags, title) {
    const set = new Set();
    for (const t of tags) {
      if (/^enhanced$/i.test(t) || /legacy\s*(&|and|\+|\/)\s*enhanced/i.test(t)) set.add('enhanced');
      if (/^legacy$/i.test(t) || /legacy\s*(&|and|\+|\/)\s*enhanced/i.test(t)) set.add('legacy');
    }
    // only bracketed title notes like "[LEGACY & ENHANCED]" or "(Enhanced)", not names like "Enhanced Trains"
    const notes = (title || '').match(/[\[(][^\])]*[\])]/g) || [];
    for (const n of notes) {
      if (/\benhanced\b|\bgen ?9\b|\be&e\b/i.test(n)) set.add('enhanced');
      if (/\blegacy\b/i.test(n)) set.add('legacy');
    }
    return ['legacy', 'enhanced'].filter((e) => set.has(e));
  }

  const ALLOWED = new Set(['P', 'BR', 'B', 'STRONG', 'I', 'EM', 'U', 'UL', 'OL', 'LI', 'A', 'IMG', 'H1', 'H2', 'H3', 'H4', 'BLOCKQUOTE', 'CODE', 'PRE', 'SPAN']);
  const DROP = new Set(['SCRIPT', 'STYLE', 'IFRAME', 'NOSCRIPT', 'OBJECT', 'EMBED', 'FORM', 'INPUT', 'BUTTON', 'SELECT', 'TEXTAREA', 'SVG', 'VIDEO', 'AUDIO', 'CANVAS', 'LINK', 'META']);

  function sanitize(root, base) {
    if (!root) return '';
    const out = document.implementation.createHTMLDocument('');
    const container = out.createElement('div');
    const walk = (src, dst) => {
      for (const node of src.childNodes) {
        if (node.nodeType === 3) { dst.appendChild(out.createTextNode(node.nodeValue)); continue; }
        if (node.nodeType !== 1) continue;
        const tag = node.tagName.toUpperCase();
        if (DROP.has(tag)) continue;
        if (!ALLOWED.has(tag)) { walk(node, dst); continue; }
        const el = out.createElement(tag.toLowerCase());
        if (tag === 'A') {
          const href = abs(node.getAttribute('href'), base);
          if (href) el.setAttribute('href', href);
        } else if (tag === 'IMG') {
          const src = abs(node.getAttribute('data-src') || node.getAttribute('data-original') || node.getAttribute('src'), base);
          if (!src) continue;
          el.setAttribute('src', src);
          if (node.getAttribute('alt')) el.setAttribute('alt', node.getAttribute('alt'));
        }
        walk(node, el);
        dst.appendChild(el);
      }
    };
    walk(root, container);
    return container.innerHTML.trim();
  }

  function pagination(doc) {
    const nums = [...doc.querySelectorAll('.pagination a, .pagination span')]
      .map((a) => text(a)).filter((t) => /^\d+$/.test(t)).map(Number);
    const pages = nums.length ? Math.max(...nums) : null;
    const next = !!doc.querySelector('.pagination .next a, .pagination a[rel="next"]');
    return { pages, next };
  }

  function categoryOf(path) {
    const seg = (path || '').replace(/^\/+/, '').split('/')[0];
    const c = CATEGORIES.find((x) => x.id === seg);
    return c ? c.id : seg || null;
  }

  // ------------------------------------------------------------------ listing

  function parseCard(card, base) {
    const link = card.querySelector('a.preview') || card.querySelector('.details .name a');
    const url = abs(link && link.getAttribute('href'), base);
    if (!url) return null;
    const path = new URL(url).pathname.replace(/^\/+|\/+$/g, '');
    const nameA = card.querySelector('.details .name a');
    const title = (nameA && (nameA.getAttribute('title') || text(nameA))) || (link && link.getAttribute('title')) || '';
    const img = card.querySelector('a.preview img');
    const tags = [...card.querySelectorAll('ul.categories li')].map(text).filter(Boolean);
    const span = (suffix) => {
      const s = [...card.querySelectorAll('.stats span[title]')].find((x) => x.getAttribute('title').toLowerCase().endsWith(suffix));
      return s ? s.getAttribute('title') : null;
    };
    const rating = span('star rating');
    const versionEl = card.querySelector('.details .version');
    return {
      site: 'gta5mods',
      id: path,
      url,
      title,
      author: text(card.querySelector('.details .bottom a')) || null,
      version: versionEl ? (versionEl.getAttribute('title') || text(versionEl)) || null : null,
      image: abs(img && (img.getAttribute('data-src') || img.getAttribute('src')), base),
      downloads: num(span(' downloads')),
      likes: num(span(' likes')),
      rating: rating ? num(rating) : null,
      category: categoryOf(path),
      tags,
      editions: editionsFrom(tags, title),
      updated: null,
      fileSize: null,
      blurb: null,
    };
  }

  function parseList(doc, base, page) {
    const items = [...doc.querySelectorAll('.file-list-obj')].map((c) => parseCard(c, base)).filter(Boolean);
    const { pages, next } = pagination(doc);
    return { items, page, pages, hasMore: pages != null ? page < pages : next };
  }

  async function list(opts) {
    opts = opts || {};
    const cat = CATEGORIES.find((c) => c.id === opts.category) ? opts.category : 'all';
    const sort = SORTS.find((s) => s.id === opts.sort) || SORTS[0];
    const page = Math.max(1, parseInt(opts.page, 10) || 1);
    let path = '/' + cat;
    if (sort.path) path += '/' + sort.path;
    if (page > 1) path += '/' + page;
    const { doc, url } = await getDoc(ORIGIN + path);
    return parseList(doc, url, page);
  }

  // ------------------------------------------------------------------ search
  // The site's own search box goes to /search/<query>. Only call this on an explicit user search.

  function searchPageUrl(query) {
    const q = encodeURIComponent(String(query).trim().replace(/\//g, '-')).replace(/%20/g, '+');
    return ORIGIN + '/search/' + q;
  }

  const searchPagers = new Map(); // query -> template with {n}

  // only the {n} placeholder is decoded: the rest of the URL (a search for "50% off", "c#") stays encoded
  function unPlaceholder(href) {
    return href.replace(/%7Bn%7D/gi, '{n}');
  }

  function pagerTemplate(doc, base, page) {
    for (const a of doc.querySelectorAll('.pagination a[href]')) {
      if (text(a) !== String(page)) continue;
      const href = abs(a.getAttribute('href'), base);
      if (!href) continue;
      const u = new URL(href);
      if (u.searchParams.get('page') === String(page)) { u.searchParams.set('page', '{n}'); return unPlaceholder(u.href); }
      const m = u.pathname.match(/^(.*\/)(\d+)\/?$/);
      if (m && m[2] === String(page)) { u.pathname = m[1] + '{n}'; return unPlaceholder(u.href); }
    }
    return null;
  }

  async function search(opts) {
    opts = opts || {};
    const query = String(opts.query || '').trim();
    if (!query) return { items: [], page: 1, pages: 0, hasMore: false, searchPageUrl: null };
    const page = Math.max(1, parseInt(opts.page, 10) || 1);
    const first = searchPageUrl(query);
    let target = first;
    if (page > 1) {
      let tpl = searchPagers.get(query);
      if (!tpl) {
        const r1 = await getDoc(first);
        tpl = pagerTemplate(r1.doc, r1.url, 2);
        if (!tpl) return { items: [], page, pages: 1, hasMore: false, searchPageUrl: first };
        searchPagers.set(query, tpl);
      }
      target = tpl.replace('{n}', String(page));
    }
    const { doc, url } = await getDoc(target);
    if (page === 1) { const tpl = pagerTemplate(doc, url, 2); if (tpl) searchPagers.set(query, tpl); }
    const result = parseList(doc, url, page);
    result.searchPageUrl = first;
    return result;
  }

  // ------------------------------------------------------------------ detail

  function readJsonLd(doc) {
    for (const s of doc.querySelectorAll('script[type="application/ld+json"]')) {
      try {
        const j = JSON.parse(s.textContent);
        if (j && j['@type'] === 'CreativeWork') return j;
      } catch (e) { /* ignore */ }
    }
    return null;
  }

  function decodeEntities(s) {
    if (!s) return s;
    const t = document.createElement('textarea');
    t.innerHTML = s;
    return t.value;
  }

  // "May 14, 2025" or "2 hours ago" -> ISO; anything else is returned as the site wrote it
  function isoOrRaw(s) {
    if (!s) return null;
    const rel = s.match(/^(an?|\d+)\s+(second|minute|hour|day|week|month|year)s?\s+ago$/i);
    if (rel) {
      const n = /^an?$/i.test(rel[1]) ? 1 : Number(rel[1]);
      const unit = { second: 1e3, minute: 6e4, hour: 36e5, day: 864e5, week: 6048e5, month: 2592e6, year: 31536e6 }[rel[2].toLowerCase()];
      return new Date(Date.now() - n * unit).toISOString();
    }
    if (/^yesterday$/i.test(s)) return new Date(Date.now() - 864e5).toISOString();
    const t = Date.parse(s);
    return isNaN(t) ? s : new Date(t).toISOString();
  }

  function findRequirements(descEl) {
    if (!descEl) return null;
    const lines = descEl.innerText !== undefined && descEl.innerText
      ? descEl.innerText.split(/\n+/)
      : descEl.textContent.split(/\n+/);
    const out = [];
    for (let i = 0; i < lines.length; i++) {
      if (/^\s*(requirements?|requires|dependencies|you need|needed)\b/i.test(lines[i])) {
        for (let j = i; j < Math.min(lines.length, i + 8); j++) {
          const l = lines[j].trim();
          if (!l) continue;
          if (j > i && /^\s*(installation|install|how to install|changelog|credits|features|description)\b/i.test(l)) break;
          out.push(l);
        }
        break;
      }
    }
    return out.length ? out.join('\n').slice(0, 800) : null;
  }

  async function detail(input) {
    const url = abs(input, ORIGIN);
    if (!url || new URL(url).host !== 'www.gta5-mods.com') throw new Error('Not a GTA5-Mods URL: ' + input);
    const { doc, url: finalUrl } = await getDoc(url);
    const path = new URL(finalUrl).pathname.replace(/^\/+|\/+$/g, '');
    const root = doc.querySelector('#file') || doc;
    const h1 = root.querySelector('h1');
    const versionEl = h1 && h1.querySelector('.version');
    let title = '';
    if (h1) {
      const clone = h1.cloneNode(true);
      clone.querySelectorAll('.version').forEach((v) => v.remove());
      title = text(clone);
    }
    const ld = readJsonLd(doc);
    const tags = [...new Set([...root.querySelectorAll('a[href*="/tags/"]')]
      .filter((a) => !a.closest('.file-list-obj'))
      .map(text).filter((t) => t && !/^featured files$/i.test(t)))];

    const images = [...new Set([...root.querySelectorAll('a.mfp-image[href], a.thumbnail[href*="img.gta5-mods.com"]')]
      .map((a) => abs(a.getAttribute('href'), finalUrl)).filter(Boolean))];
    const og = doc.querySelector('meta[property="og:image"]');

    const descs = [...doc.querySelectorAll('.file-description .description-body')];
    const descEl = descs.find((d) => !d.closest('.visible-xs-block')) || descs[0] || null;

    const versionPanel = doc.querySelector('.panel.hidden-xs') || doc;
    const versions = [];
    const seen = new Set();
    for (const v of versionPanel.querySelectorAll('.file-version-container')) {
      const left = v.querySelector('.pull-left') || v;
      const clone = left.cloneNode(true);
      clone.querySelectorAll('p, span').forEach((x) => x.remove());
      const ver = text(clone);
      if (!ver || seen.has(ver)) continue;
      seen.add(ver);
      const spans = left.querySelectorAll('p > span.num-downloads');
      const dateText = spans.length > 1 ? text(spans[spans.length - 1]) : null;
      const sizeEl = left.querySelector('.file-size');
      versions.push({
        version: ver,
        date: isoOrRaw(dateText),
        downloads: num(spans.length ? text(spans[0]) : null),
        fileSize: sizeEl ? text(sizeEl).replace(/^,\s*/, '') || null : null,
        current: /\(current\)/i.test(text(left)),
      });
    }
    const current = versions.find((v) => v.current) || versions[0] || null;

    const ratingEl = doc.querySelector('.comment-average-rating[data-rating]');
    let rating = ratingEl ? num(ratingEl.getAttribute('data-rating')) : null;
    if (rating == null && ld && ld.aggregateRating && num(ld.aggregateRating.reviewCount) > 0) rating = num(ld.aggregateRating.ratingValue);
    if (rating === 0) rating = null;

    const dl = root.querySelector('a.btn-download[href]');
    const commentsEl = doc.querySelector('.num-comments');

    return {
      site: 'gta5mods',
      id: path,
      url: finalUrl,
      title: title || decodeEntities(ld && ld.name) || '',
      author: (ld && ld.author) || text(root.querySelector('a.username')) || null,
      version: versionEl ? text(versionEl) || null : (current && current.version) || null,
      image: abs(og && og.getAttribute('content'), finalUrl) || images[0] || null,
      downloads: num(text(doc.querySelector('.file-stat.file-downloads'))),
      likes: num(text(doc.querySelector('.file-stat.file-likes'))),
      rating,
      category: categoryOf(path),
      tags,
      editions: editionsFrom(tags, title),
      updated: (ld && ld.dateModified) || null,
      fileSize: current ? current.fileSize : null,
      blurb: null,
      descriptionHtml: sanitize(descEl, finalUrl),
      images,
      downloadPageUrl: dl ? abs(dl.getAttribute('href'), finalUrl) : null,
      versions: versions.map((v) => ({ version: v.version, date: v.date, downloads: v.downloads, fileSize: v.fileSize })),
      requirementsText: findRequirements(descEl),
      uploaded: (ld && ld.datePublished) || null,
      comments: num((ld && ld.comment_count) || text(commentsEl)),
      loginRequired: false,
    };
  }

  window.__vml = {
    site: 'gta5mods',
    version: VERSION,
    origin: ORIGIN,
    categories: () => CATEGORIES.map((c) => ({ id: c.id, name: c.name })),
    sorts: () => SORTS.map((s) => ({ id: s.id, name: s.name })),
    list,
    search,
    searchPageUrl,
    detail,
  };
})();
