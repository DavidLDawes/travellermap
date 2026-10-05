// Tests for deploy/cloudflare/worker.mjs: proxying to the Cloud Run host, edge caching that follows
// the origin's Cache-Control, Accept-aware cache keys, and redirect rewriting.
import assert from 'node:assert/strict';
import {test} from 'node:test';
import {handle, variesByAccept} from '../../deploy/cloudflare/worker.mjs';

const ORIGIN_HOST = 'site-abc-uc.a.run.app';
const env = {ORIGIN_HOST};

// A stand-in for caches.default: entries keyed by URL, bodies kept as text.
function fakeCache() {
  const entries = new Map();
  return {
    entries,
    async match(key) {
      const entry = entries.get(key.url);
      return entry && new Response(entry.body, {status: entry.status, headers: entry.headers});
    },
    async put(key, response) {
      entries.set(key.url, {status: response.status, headers: new Headers(response.headers), body: await response.text()});
    },
  };
}

// Runs one request through the Worker. `origin(request)` answers for Cloud Run and records what it saw.
async function run(cache, origin, url, init = {}) {
  const pending = [];
  const seen = [];
  const response = await handle(
    new Request(url, init), env, {waitUntil: promise => pending.push(promise)}, cache,
    async request => { seen.push(request); return origin(request); });
  await Promise.all(pending);
  return {response, seen};
}

const publicOrigin = (maxAge, extra = {}) => () =>
  new Response('body', {headers: {'Cache-Control': `public, max-age=${maxAge}`, ...extra}});
const site = 'https://travellermap.example.com';

test('requests go to the Cloud Run host with path and query intact', async () => {
  const {seen} = await run(fakeCache(), publicOrigin(60), `${site}/api/search?q=Regina&x=1`);
  assert.equal(seen.length, 1);
  assert.equal(new URL(seen[0].url).host, ORIGIN_HOST);
  assert.equal(new URL(seen[0].url).pathname + new URL(seen[0].url).search, '/api/search?q=Regina&x=1');
});

test('a public response is cached, and a repeat is served from the edge with the origin headers', async () => {
  const cache = fakeCache();
  const origin = publicOrigin(86400, {Vary: 'Accept'});
  const first = await run(cache, origin, `${site}/index.js`);
  assert.equal(first.response.headers.get('X-Edge'), 'MISS');

  const second = await run(cache, origin, `${site}/index.js`);
  assert.equal(second.seen.length, 0);
  assert.equal(second.response.headers.get('X-Edge'), 'HIT');
  assert.equal(await second.response.text(), 'body');
  assert.equal(second.response.headers.get('Cache-Control'), 'public, max-age=86400');
  assert.equal(second.response.headers.get('Vary'), 'Accept');
  assert.equal(second.response.headers.has('X-Origin-Cache-Control'), false);
});

test('the edge keeps an entry at most an hour, whatever the origin allows', async () => {
  const cache = fakeCache();
  await run(cache, publicOrigin(86400), `${site}/index.js`);
  const [stored] = [...cache.entries.values()];
  assert.equal(stored.headers.get('Cache-Control'), 'public, max-age=3600');
  const short = fakeCache();
  await run(short, publicOrigin(120), `${site}/a.js`);
  assert.equal([...short.entries.values()][0].headers.get('Cache-Control'), 'public, max-age=120');
});

test('the same API URL is cached separately per Accept header', async () => {
  const cache = fakeCache();
  const answer = request => new Response(request.headers.get('Accept'), {headers: {'Cache-Control': 'public, max-age=60'}});
  const url = `${site}/api/universe`;
  const json = await run(cache, answer, url, {headers: {Accept: 'application/json'}});
  const xml = await run(cache, answer, url, {headers: {Accept: 'text/xml'}});
  assert.equal(await json.response.text(), 'application/json');
  assert.equal(await xml.response.text(), 'text/xml');
  const again = await run(cache, answer, url, {headers: {Accept: 'application/json'}});
  assert.equal(again.seen.length, 0);
  assert.equal(await again.response.text(), 'application/json');
});

test('only public, successful, cookie-free GETs are cached', async () => {
  const cases = [
    ['no-store', () => new Response('x', {headers: {'Cache-Control': 'no-store'}}), {}],
    ['private', () => new Response('x', {headers: {'Cache-Control': 'private, max-age=60'}}), {}],
    ['no Cache-Control', () => new Response('x'), {}],
    ['cookie', publicOrigin(60, {'Set-Cookie': 'a=b'}), {}],
    ['not found', () => new Response('x', {status: 404, headers: {'Cache-Control': 'public, max-age=60'}}), {}],
    ['POST', publicOrigin(60), {method: 'POST', body: 'x'}],
    ['HEAD', publicOrigin(60), {method: 'HEAD'}],
    ['Range', publicOrigin(60), {headers: {Range: 'bytes=0-9'}}],
  ];
  for (const [name, origin, init] of cases) {
    const cache = fakeCache();
    await run(cache, origin, `${site}/thing`, init);
    assert.equal(cache.entries.size, 0, name);
  }
});

test('redirects to the Cloud Run host are rewritten to the public host', async () => {
  const redirect = location => () => new Response(null, {status: 301, headers: {Location: location}});
  const absolute = await run(fakeCache(), redirect(`https://${ORIGIN_HOST}/doc/api?x=1`), `${site}/doc`);
  assert.equal(absolute.response.status, 301);
  assert.equal(absolute.response.headers.get('Location'), 'https://travellermap.example.com/doc/api?x=1');

  const relative = await run(fakeCache(), redirect('/doc/api'), `${site}/doc`);
  assert.equal(relative.response.headers.get('Location'), '/doc/api');
  const elsewhere = await run(fakeCache(), redirect('https://example.org/x'), `${site}/doc`);
  assert.equal(elsewhere.response.headers.get('Location'), 'https://example.org/x');
});

test('an unreachable origin is a 502, not an exception', async () => {
  const {response} = await run(fakeCache(), () => { throw new Error('connection refused'); }, `${site}/`);
  assert.equal(response.status, 502);
});

test('plain http is redirected to https without touching the origin', async () => {
  const {response, seen} = await run(fakeCache(), publicOrigin(60), 'http://travellermap.example.com/a?b=1');
  assert.equal(response.status, 301);
  assert.equal(response.headers.get('Location'), 'https://travellermap.example.com/a?b=1');
  assert.equal(seen.length, 0);
});

test('sector data and T5SS tables are cached per Accept header too, not just the API', async () => {
  // /data/spin/1910 answers JSON, XML or text by Accept; one format must never be served for another.
  for (const path of ['/data/spin/1910', '/t5ss/sophonts', '/data/spin/C/image']) {
    const cache = fakeCache();
    const answer = request => new Response(request.headers.get('Accept'), {headers: {'Cache-Control': 'public, max-age=60'}});
    const json = await run(cache, answer, `${site}${path}`, {headers: {Accept: 'application/json'}});
    const xml = await run(cache, answer, `${site}${path}`, {headers: {Accept: 'text/xml'}});
    assert.equal(await json.response.text(), 'application/json', path);
    assert.equal(await xml.response.text(), 'text/xml', `${path}: not the cached JSON`);
  }
});

test('static files share one cache entry whatever the Accept header', async () => {
  const cache = fakeCache();
  await run(cache, publicOrigin(60), `${site}/index.js`, {headers: {Accept: '*/*'}});
  const other = await run(cache, publicOrigin(60), `${site}/index.js`, {headers: {Accept: 'application/javascript'}});
  assert.equal(other.seen.length, 0, 'served from the same entry');
});

test('generated paths vary by Accept; static files do not', () => {
  for (const path of ['/api/tile', '/data/spin/1910', '/t5ss/sophonts', '/', '/doc/api', '/admin/status'])
    assert.equal(variesByAccept(path), true, path);
  for (const path of ['/index.js', '/res/mains.json', '/favicon.svg', '/doc/api.html', '/res/app/regina192.png'])
    assert.equal(variesByAccept(path), false, path);
});
