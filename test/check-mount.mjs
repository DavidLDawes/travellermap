// Loads the site's pages in headless Chrome under a mount path (for example
// https://srd-tools.com/TravellerMap/) and checks, from the browser's own network log, that
// everything they fetch from that origin stays under the mount, nothing fails, the map draws
// tiles, and no script throws.
//
//   node test/check-mount.mjs --base https://srd-tools.com/TravellerMap [--key ADMIN_KEY]
//
// Admin pages are checked only with --key, or when the base is on this machine.
// Needs Node 22+ (global WebSocket) and an installed Chrome or Edge.

import {spawn} from 'node:child_process';
import {existsSync, mkdtempSync, rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import path from 'node:path';

const args = process.argv.slice(2);
const opt = name => {
  const i = args.indexOf(name);
  return i >= 0 ? args[i + 1] : undefined;
};
const BASE = (opt('--base') ?? 'http://localhost:8787/TravellerMap').replace(/\/$/, '');
const KEY = opt('--key');
const base = new URL(BASE + '/');
const MOUNT = base.pathname.replace(/\/$/, '');
const local = ['localhost', '127.0.0.1'].includes(base.hostname);

const PAGES = [
  {path: '', tiles: true},
  {path: '?sector=Spinward%20Marches&hex=1910', tiles: true},  // world card: jumpworlds, world data
  {path: 'go/spin/1910', tiles: true},                         // server redirect
  {path: 'doc/api'},
  {path: 'doc/about'},
  {path: 'make/poster'},
  {path: 'print/world?sector=spin&hex=1910'},
  {path: 'tools/lintsec'},
  {path: 'no/such/page', status: 404},
  ...(KEY || local ? [{path: 'admin/'}, {path: 'admin/status'}] : []),
];

const CHROME = process.env.CHROME ?? [
  'C:/Program Files/Google/Chrome/Application/chrome.exe',
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  '/usr/bin/google-chrome', '/usr/bin/chromium', '/usr/bin/chromium-browser',
  '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
].find(p => existsSync(p));
if (!CHROME) {
  console.error('Chrome not found; set the CHROME environment variable.');
  process.exit(2);
}

const sleep = ms => new Promise(r => setTimeout(r, ms));
const PORT = 9300 + Math.floor(Math.random() * 600);
const profile = mkdtempSync(path.join(tmpdir(), 'tm-mount-check-'));
const chrome = spawn(CHROME, ['--headless=new', `--remote-debugging-port=${PORT}`,
  `--user-data-dir=${profile}`, '--no-first-run', '--window-size=1280,900', 'about:blank'],
{stdio: ['ignore', 'ignore', 'ignore']});

let failed = false;
try {
  let target;
  for (let i = 0; i < 300 && !target; ++i) {
    try {
      target = await (await fetch(`http://127.0.0.1:${PORT}/json/new?about:blank`, {method: 'PUT'})).json();
    } catch {
      await sleep(200);
    }
  }
  if (!target) throw new Error('Could not connect to Chrome.');

  const ws = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise(r => ws.addEventListener('open', r));
  let nextId = 0;
  const pending = new Map();
  const listeners = [];
  ws.addEventListener('message', ev => {
    const m = JSON.parse(ev.data);
    if (m.id && pending.has(m.id)) {
      pending.get(m.id)(m.result);
      pending.delete(m.id);
    } else if (m.method) {
      for (const l of listeners) l(m);
    }
  });
  const send = (method, params = {}) => new Promise(r => {
    pending.set(++nextId, r);
    ws.send(JSON.stringify({id: nextId, method, params}));
  });
  const evaluate = async expr =>
    (await send('Runtime.evaluate', {expression: expr, returnByValue: true, awaitPromise: true})).result?.value;
  await send('Network.enable');
  await send('Runtime.enable');
  await send('Page.enable');

  for (const page of PAGES) {
    const requests = new Map();  // requestId -> {url, status, error}
    const exceptions = [];
    const listener = m => {
      const p = m.params;
      if (m.method === 'Network.requestWillBeSent') {
        requests.set(p.requestId, {url: p.request.url});
        // A redirect reuses the requestId; keep the hop that redirected.
        if (p.redirectResponse) requests.set(p.requestId + '>' + p.request.url, {url: p.redirectResponse.url, status: p.redirectResponse.status});
      } else if (m.method === 'Network.responseReceived') {
        const r = requests.get(p.requestId);
        if (r) r.status = p.response.status;
      } else if (m.method === 'Network.loadingFailed' && !p.canceled) {
        const r = requests.get(p.requestId);
        if (r) r.error = p.errorText;
      } else if (m.method === 'Runtime.exceptionThrown') {
        exceptions.push(p.exceptionDetails.exception?.description ?? p.exceptionDetails.text);
      }
    };
    listeners.push(listener);
    const url = BASE + '/' + page.path + (KEY && page.path.startsWith('admin') ? '?key=' + encodeURIComponent(KEY) : '');
    await send('Page.navigate', {url});
    await sleep(page.tiles ? 7000 : 4000);  // let the map settle and lazy requests finish
    listeners.splice(listeners.indexOf(listener), 1);

    const problems = [];
    const finalUrl = new URL(await evaluate('location.href'));
    if (!finalUrl.pathname.startsWith(MOUNT + '/')) problems.push(`ended up outside the mount: ${finalUrl}`);
    let sameOrigin = 0;
    let tiles = 0;
    for (const r of requests.values()) {
      const u = new URL(r.url);
      if (u.origin !== base.origin) continue;  // fonts, CDN scripts
      ++sameOrigin;
      const shown = u.pathname + u.search.slice(0, 60);
      if (!(u.pathname + '/').startsWith(MOUNT + '/')) problems.push(`outside the mount: ${shown}`);
      const isPage = u.href.split('#')[0] === url.split('#')[0];
      if (r.status >= 400 && !(isPage && r.status === page.status)) problems.push(`HTTP ${r.status}: ${shown}`);
      if (r.error) problems.push(`failed (${r.error}): ${shown}`);
      if (u.pathname.endsWith('/api/tile') && r.status === 200) ++tiles;
    }
    if (page.tiles && tiles === 0) problems.push('no map tiles loaded');
    for (const e of exceptions) problems.push(`script error: ${String(e).split('\n')[0]}`);

    const label = (page.path || '(map)').padEnd(36);
    console.log(`${problems.length ? 'FAIL' : 'ok  '} ${label} ${sameOrigin} requests${page.tiles ? `, ${tiles} tiles` : ''}`);
    for (const p of problems.slice(0, 12)) console.log('       ' + p);
    if (problems.length) failed = true;
  }
  ws.close();
} catch (e) {
  console.error(e);
  failed = true;
} finally {
  chrome.kill();
  await sleep(500);
  try { rmSync(profile, {recursive: true, force: true}); } catch { /* Chrome may still hold files */ }
}
process.exit(failed ? 1 : 0);
