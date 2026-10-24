// LCPDFR.com (GTA5 Mods section) extractor for V Mod Launcher.
// Injected into a hidden WebView2 that sits on https://www.lcpdfr.com after the site's own
// browser check has cleared. Reads pages with same-origin fetch + DOMParser (never navigates),
// at most one request per 1.1 s (robots.txt asks bots for <= 1/s). Never solves challenges.
// Contract: docs/site-extractors.md
(function () {
  'use strict';
  const VERSION = 1;
  if (window.__vml && window.__vml.site === 'lcpdfr' && window.__vml.version >= VERSION) return;

  const ORIGIN = 'https://www.lcpdfr.com';
  const BASE = ORIGIN + '/downloads/gta5mods/';
  const MIN_GAP_MS = 1100;

  const CATEGORIES = [
    { id: 'scripts', name: 'Scripts & Plugins' },
    { id: 'vehiclemodels', name: 'Vehicle Models' },
    { id: 'vehiclestextures', name: 'Vehicle Skins' },
    { id: 'character', name: 'Character' },
    { id: 'audio', name: 'Audio' },
    { id: 'datafile', name: 'Visual & Data File' },
    { id: 'misc', name: 'Misc' },
    { id: 'essential-mods', name: 'Essential Mods' },
    { id: 'g17media', name: 'LSPDFR' },
  ];

  // 'updated' is the category's default order, so it needs no query string.
  const SORTS = [
    { id: 'updated', name: 'Recently updated', by: null },
    { id: 'date', name: 'Newest', by: 'date', dir: 'desc' },
    { id: 'file_downloads', name: 'Most downloaded', by: 'file_downloads', dir: 'desc' },
    { id: 'rating', name: 'Highest rated', by: 'rating', dir: 'desc' },
    { id: 'views', name: 'Most viewed', by: 'views', dir: 'desc' },
    { id: 'title', name: 'Title (A-Z)', by: 'title', dir: 'asc' },
  ];

  // ------------------------------------------------------------------ fetching

  let lastFetch = 0;
  let queue = Promise.resolve();
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

  function isChallenge(doc) {
    const title = (doc.title || '').toLowerCase();
    if (/checking your browser|just a moment|attention required|verify you are human|ddos-guard/.test(title)) return true;
    return !!doc.querySelector('.h-captcha, .g-recaptcha, #challenge-form, .powstatus, iframe[src*="captcha"], iframe[src*="challenges.cloudflare.com"]');
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

  function imgSrc(img, base) {
    if (!img) return null;
    return abs(img.getAttribute('data-src') || img.getAttribute('data-original') || img.getAttribute('src'), base);
  }

  // /downloads/gta5mods/<cat>/<id>-<slug>/
  const FILE_RE = /\/downloads\/gta5mods\/([a-z0-9-]+)\/(\d+)-[^/?#]+\/?$/i;

  function parseFileUrl(u) {
    try {
      const m = new URL(u).pathname.match(FILE_RE);
      return m ? { category: m[1], id: m[2] } : null;
    } catch (e) { return null; }
  }

  function tagsOf(scope) {
    const labels = [];
    const shown = [];
    const isRow = !!(scope.matches && scope.matches('.ipsData__item'));
    for (const t of scope.querySelectorAll('.ipsTags__tag[data-tag-label]')) {
      // on a file page, skip tags that belong to "other files" rows and comments
      if (!isRow && t.closest('.ipsData__item, article[id^="elComment"]')) continue;
      const label = t.getAttribute('data-tag-label');
      if (labels.includes(label)) continue;
      labels.push(label);
      shown.push(text(t) || label.replace(/^[^:]+:/, ''));
    }
    const editions = ['legacy', 'enhanced'].filter((e) => labels.some((l) => l.toLowerCase() === 'game_version:' + e));
    return { tags: shown, labels, editions };
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
          const src = imgSrc(node, base);
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

  function pagesOf(doc) {
    const p = doc.querySelector('.ipsPagination[data-pages]');
    return p ? num(p.getAttribute('data-pages')) : 1;
  }

  // ------------------------------------------------------------------ listing

  function parseRow(row, base) {
    const a = row.querySelector('h4 a[href]') || row.querySelector('a.ipsLinkPanel[href]');
    const url = abs(a && a.getAttribute('href'), base);
    if (!url) return null;
    const info = parseFileUrl(url);
    const stat = (type) => {
      const s = row.querySelector('li[data-stattype="' + type + '"] [data-stat-value]');
      return s ? s.getAttribute('data-stat-value') : null;
    };
    const { tags, editions } = tagsOf(row);
    const ratingEl = row.querySelector('.ipsRating_collective[data-v]');
    const rating = ratingEl ? num(ratingEl.getAttribute('data-v')) : null;
    const last = row.querySelector('.ipsData__last');
    const time = last && last.querySelector('time[datetime]');
    const size = stat('filesize');
    return {
      site: 'lcpdfr',
      id: info ? info.id : row.getAttribute('data-rowid'),
      url,
      title: text(a) || (a && a.getAttribute('title')) || '',
      author: text(last && (last.querySelector('.ipsUsername') || last.querySelector('a[href*="/profile/"]'))) || null,
      version: null,
      image: imgSrc(row.querySelector('.ipsData__image img'), base),
      downloads: num(stat('downloads')),
      likes: null,
      rating: rating ? Math.round(rating * 100) / 100 : null,
      category: info ? info.category : null,
      tags,
      editions,
      updated: time ? time.getAttribute('datetime') : null,
      fileSize: size ? size.trim() : null,
      blurb: text(row.querySelector('.ipsData__desc')) || null,
      views: num(stat('views')),
      comments: num(stat('comments')),
    };
  }

  function listUrl(category, sort, page) {
    let u = BASE + category + '/';
    if (!sort.by) return page > 1 ? u + 'page/' + page + '/' : u;
    return u + '?sortby=' + sort.by + '&sortdirection=' + sort.dir + '&page=' + page;
  }

  async function list(opts) {
    opts = opts || {};
    const cat = (CATEGORIES.find((c) => c.id === opts.category) || CATEGORIES[0]).id;
    const sort = SORTS.find((s) => s.id === opts.sort) || SORTS[0];
    const page = Math.max(1, parseInt(opts.page, 10) || 1);
    const { doc, url } = await getDoc(listUrl(cat, sort, page));
    const items = [...doc.querySelectorAll('li.ipsData__item[data-rowid]')]
      .map((r) => parseRow(r, url)).filter(Boolean);
    const pages = pagesOf(doc);
    return { items, page, pages, hasMore: page < pages };
  }

  // ------------------------------------------------------------------ search
  // Invision search, limited to Downloads files. Only call on an explicit user search.

  function searchPageUrl(query, page) {
    let u = ORIGIN + '/search/?q=' + encodeURIComponent(String(query).trim()) + '&type=downloads_file&quick=1';
    if (page > 1) u += '&page=' + page;
    return u;
  }

  function parseSearch(doc, base) {
    const byId = new Map();
    for (const a of doc.querySelectorAll('a[href]')) {
      const url = abs(a.getAttribute('href'), base);
      const info = url && parseFileUrl(url);
      if (!info) continue;
      const box = a.closest('li, article') || a.parentElement;
      let item = byId.get(info.id);
      if (!item) {
        item = { box, url: url.split('?')[0].split('#')[0], info, title: '' };
        byId.set(info.id, item);
      }
      const t = text(a);
      if (t.length > item.title.length && !/^\d+$/.test(t)) item.title = t;
    }
    const items = [];
    for (const { box, url, info, title } of byId.values()) {
      const { tags, editions } = tagsOf(box);
      const time = box.querySelector('time[datetime]');
      const img = [...box.querySelectorAll('img')].find((i) => /lcpdfrusercontent\.com/.test(i.getAttribute('src') || i.getAttribute('data-src') || '') && !/avatar|profile|monthly_\d+_\d+\/[^/]*photo/i.test(i.getAttribute('src') || ''));
      const snippet = box.querySelector('.ipsStream__snippet, .ipsStreamItem__content, [data-ipstruncate], p');
      items.push({
        site: 'lcpdfr', id: info.id, url, title, author: text(box.querySelector('.ipsUsername')) || null,
        version: null, image: imgSrc(img, base), downloads: null, likes: null, rating: null,
        category: info.category, tags, editions, updated: time ? time.getAttribute('datetime') : null,
        fileSize: null, blurb: snippet ? text(snippet).slice(0, 300) || null : null,
      });
    }
    return items;
  }

  async function search(opts) {
    opts = opts || {};
    const query = String(opts.query || '').trim();
    if (!query) return { items: [], page: 1, pages: 0, hasMore: false, searchPageUrl: null };
    const page = Math.max(1, parseInt(opts.page, 10) || 1);
    const target = searchPageUrl(query, page);
    const { doc, url } = await getDoc(target);
    const items = parseSearch(doc, url);
    const pages = pagesOf(doc);
    return { items, page, pages, hasMore: page < pages, searchPageUrl: searchPageUrl(query, 1) };
  }

  // ------------------------------------------------------------------ detail

  function infoList(doc) {
    const info = {};
    for (const li of doc.querySelectorAll('li')) {
      const label = li.querySelector(':scope > strong.ipsList__label, strong.ipsList__label');
      if (!label || label.closest('li') !== li) continue;
      const key = text(label);
      if (!key || key in info) continue;
      const clone = li.cloneNode(true);
      clone.querySelectorAll('strong.ipsList__label').forEach((s) => s.remove());
      const time = li.querySelector('time[datetime]');
      info[key] = { value: text(clone), time: time ? time.getAttribute('datetime') : null };
    }
    return info;
  }

  function requirementsFrom(info) {
    const lines = [];
    for (const [key, { value }] of Object.entries(info)) {
      if (!/require|minimum|maximum|compatib|plugin type|dependenc/i.test(key)) continue;
      if (/staff|hide av/i.test(key)) continue;
      if (!value || /^(n\/a|none|-)$/i.test(value)) continue;
      lines.push(key + ': ' + value);
    }
    return lines.length ? lines.join('\n') : null;
  }

  async function detail(input) {
    const url = abs(input, ORIGIN);
    if (!url || new URL(url).host !== 'www.lcpdfr.com') throw new Error('Not an LCPDFR URL: ' + input);
    const { doc, url: finalUrl } = await getDoc(url);
    const cleanUrl = finalUrl.split('?')[0].split('#')[0];
    const fileInfo = parseFileUrl(cleanUrl);

    const h1 = doc.querySelector('h1');
    const versionEl = h1 && h1.querySelector('.i-color_soft');
    let title = '';
    if (h1) {
      const clone = h1.cloneNode(true);
      clone.querySelectorAll('.i-color_soft').forEach((v) => v.remove());
      title = text(clone);
    }

    // file description = first rich-text block that isn't a comment or review
    const descEl = doc.querySelector('#ipsTabs_file_desc_panel [data-role="commentContent"]')
      || [...doc.querySelectorAll('.ipsRichText--user')].find((e) => !e.closest('article[id^="elComment"], [data-commentid], [data-role="commentFeed"]'))
      || null;

    const info = infoList(doc);
    const allTags = tagsOf(doc);

    const images = [...new Set([...doc.querySelectorAll('a.ipsThumb[href]')]
      .map((a) => abs(a.getAttribute('href'), finalUrl)).filter(Boolean))];
    const og = doc.querySelector('meta[property="og:image"]');
    const firstThumb = doc.querySelector('a.ipsThumb img');

    const buttons = [...doc.querySelectorAll('a[href*="do=download"]')]
      .filter((a) => !/do=downloadMM/i.test(a.getAttribute('href')));
    const dl = buttons.find((a) => /ipsButton--primary/.test(a.className)) || buttons[0] || null;
    let downloadPageUrl = dl ? abs(dl.getAttribute('href'), finalUrl) : null;
    if (downloadPageUrl) {
      // csrfKey is tied to the current (guest) session; it breaks once the user logs in
      const u = new URL(downloadPageUrl);
      u.searchParams.delete('csrfKey');
      downloadPageUrl = u.href;
    }

    const versionTitle = doc.querySelector('[data-role="versionTitle"]');
    const versions = [];
    if (versionTitle) {
      const section = versionTitle.closest('section') || versionTitle.parentElement;
      const t = section && section.querySelector('time[datetime]');
      versions.push({ version: text(versionTitle), date: t ? t.getAttribute('datetime') : null });
    }
    // first releases have no "What's New" box: fall back to the version in the title
    if (!versions.length && versionEl && text(versionEl)) {
      const updated = (info['Updated'] && info['Updated'].time) || (info['Submitted'] && info['Submitted'].time) || null;
      versions.push({ version: text(versionEl), date: updated });
    }

    const authorEl = [...doc.querySelectorAll('a.ipsUsername')]
      .find((a) => text(a) && !a.closest('article[id^="elComment"], [data-role="commentFeed"], .ipsData__item'));
    const ratingEl = [...doc.querySelectorAll('.ipsRating_collective[data-v]')]
      .find((r) => !r.closest('article[id^="elComment"], [data-role="commentFeed"], .ipsData__item'));
    const rating = ratingEl ? num(ratingEl.getAttribute('data-v')) : null;
    const commentsText = [...doc.querySelectorAll('span, h2, h3, a, li')]
      .map(text).find((t) => /^\d[\d,]*\s+comments?$/i.test(t));
    const get = (k) => (info[k] ? info[k] : null);

    return {
      site: 'lcpdfr',
      id: fileInfo ? fileInfo.id : null,
      url: cleanUrl,
      title,
      author: text(authorEl) || null,
      version: versionEl ? text(versionEl) || null : (versions[0] && versions[0].version) || null,
      image: abs(og && og.getAttribute('content'), finalUrl) || imgSrc(firstThumb, finalUrl) || images[0] || null,
      downloads: get('Downloads') ? num(get('Downloads').value) : null,
      likes: null,
      rating: rating ? Math.round(rating * 100) / 100 : null,
      category: fileInfo ? fileInfo.category : null,
      tags: allTags.tags,
      editions: allTags.editions,
      updated: (get('Updated') && get('Updated').time) || (get('Published') && get('Published').time) || null,
      fileSize: get('File Size') ? get('File Size').value || null : null,
      blurb: null,
      descriptionHtml: sanitize(descEl, finalUrl),
      images,
      downloadPageUrl,
      versions,
      requirementsText: requirementsFrom(info),
      uploaded: (get('Submitted') && get('Submitted').time) || null,
      comments: commentsText ? num(commentsText) : null,
      views: get('Views') ? num(get('Views').value) : null,
      // Invision marks guest pages with data-ips-guest on <html>; downloads need an account
      loginRequired: doc.documentElement.hasAttribute('data-ips-guest'),
    };
  }

  window.__vml = {
    site: 'lcpdfr',
    version: VERSION,
    origin: ORIGIN,
    categories: () => CATEGORIES.map((c) => ({ id: c.id, name: c.name })),
    sorts: () => SORTS.map((s) => ({ id: s.id, name: s.name })),
    list,
    search,
    searchPageUrl: (q) => searchPageUrl(q, 1),
    detail,
  };
})();
