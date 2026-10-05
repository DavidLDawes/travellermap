// Tests for deploy/cloudflare/worker.mjs serving the site under a path of another site
// (srd-tools.com/TravellerMap/): prefix removed toward the origin, put back on what comes back.
import assert from 'node:assert/strict';
import {test} from 'node:test';
import {handle, resolveMount, withPrefix} from '../../deploy/cloudflare/worker.mjs';

const ORIGIN_HOST = 'site-abc-uc.a.run.app';
const MOUNT = '/TravellerMap';
const env = {ORIGIN_HOST, MOUNT_PATH: MOUNT};
const tools = 'https://srd-tools.com';

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

// Runs one request. HTML rewriting is HTMLRewriter in production (not in Node); this stand-in
// records each call and tags the body, so the tests can see when and with what it was applied.
async function run(origin, url, {init = {}, cache = fakeCache(), environment = env} = {}) {
  const pending = [];
  const seen = [];
  const rewrites = [];
  const response = await handle(
    new Request(url, init), environment, {waitUntil: promise => pending.push(promise)}, cache,
    async request => { seen.push(request); return origin(request); },
    (res, prefix) => {
      rewrites.push(prefix);
      return new Response(res.body.pipeThrough(new TransformStream({
        transform(chunk, controller) { controller.enqueue(chunk); },
        flush(controller) { controller.enqueue(new TextEncoder().encode(`<!--rewritten ${prefix}-->`)); },
      })), res);
    });
  await Promise.all(pending);
  return {response, seen, rewrites, cache};
}

const html = (body = '<a href="/doc/about">About</a>', headers = {}) => () =>
  new Response(body, {headers: {'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'public, max-age=60', ...headers}});
const redirect = location => () => new Response(null, {status: 302, headers: {Location: location}});

// resolveMount and withPrefix --------------------------------------------

test('paths under the mount are served from the origin root', () => {
  assert.deepEqual(resolveMount('/TravellerMap/', MOUNT), {originPath: '/', prefix: MOUNT});
  assert.deepEqual(resolveMount('/TravellerMap/api/tile', MOUNT), {originPath: '/api/tile', prefix: MOUNT});
  assert.deepEqual(resolveMount('/TravellerMap/doc/api', MOUNT), {originPath: '/doc/api', prefix: MOUNT});
});

test('the bare mount and other spellings redirect to the canonical path', () => {
  assert.deepEqual(resolveMount('/TravellerMap', MOUNT), {redirect: '/TravellerMap/'});
  assert.deepEqual(resolveMount('/travellermap', MOUNT), {redirect: '/TravellerMap/'});
  assert.deepEqual(resolveMount('/travellermap/', MOUNT), {redirect: '/TravellerMap/'});
  assert.deepEqual(resolveMount('/TRAVELLERMAP/doc/api', MOUNT), {redirect: '/TravellerMap/doc/api'});
});

test('paths outside the mount, or with no mount configured, pass through', () => {
  assert.deepEqual(resolveMount('/api/tile', MOUNT), {originPath: '/api/tile', prefix: ''});
  assert.deepEqual(resolveMount('/TravellerMapper/x', MOUNT), {originPath: '/TravellerMapper/x', prefix: ''}, 'a longer name is not the mount');
  assert.deepEqual(resolveMount('/TravellerMap/x', undefined), {originPath: '/TravellerMap/x', prefix: ''});
});

test('only root-absolute URLs get the prefix', () => {
  assert.equal(withPrefix('/doc/about', MOUNT), '/TravellerMap/doc/about');
  assert.equal(withPrefix('/', MOUNT), '/TravellerMap/');
  assert.equal(withPrefix('//cdn.example.com/x.js', MOUNT), '//cdn.example.com/x.js', 'protocol-relative');
  assert.equal(withPrefix('https://example.com/x', MOUNT), 'https://example.com/x');
  assert.equal(withPrefix('doc/about', MOUNT), 'doc/about');
  assert.equal(withPrefix('#top', MOUNT), '#top');
  assert.equal(withPrefix('/doc/about', ''), '/doc/about', 'no mount, no change');
});

// handle ------------------------------------------------------------------

test('a mounted request reaches the origin without the prefix, query intact', async () => {
  const {seen} = await run(html(), `${tools}/TravellerMap/api/search?q=Regina`);
  const url = new URL(seen[0].url);
  assert.equal(url.host, ORIGIN_HOST);
  assert.equal(url.pathname + url.search, '/api/search?q=Regina');
});

test('the bare mount path redirects with its query kept, without touching the origin', async () => {
  const {response, seen} = await run(html(), `${tools}/TravellerMap?sector=Spinward%20Marches`);
  assert.equal(response.status, 301);
  assert.equal(response.headers.get('Location'), `${tools}/TravellerMap/?sector=Spinward%20Marches`);
  assert.equal(seen.length, 0);
});

test('redirects from the origin land under the mount', async () => {
  // /go/{sector} answers "/?sector=..." (root-relative).
  const rootRelative = await run(redirect('/?sector=spin'), `${tools}/TravellerMap/go/spin`);
  assert.equal(rootRelative.response.headers.get('Location'), '/TravellerMap/?sector=spin');

  // The HTTPS/www redirects name the host the origin was called with.
  const absolute = await run(redirect(`https://${ORIGIN_HOST}/admin/?key=k`), `${tools}/TravellerMap/admin`);
  assert.equal(absolute.response.headers.get('Location'), `${tools}/TravellerMap/admin/?key=k`);

  const elsewhere = await run(redirect('https://example.org/x'), `${tools}/TravellerMap/x`);
  assert.equal(elsewhere.response.headers.get('Location'), 'https://example.org/x');
  const pageRelative = await run(redirect('api?x=1'), `${tools}/TravellerMap/doc/x`);
  assert.equal(pageRelative.response.headers.get('Location'), 'api?x=1');
});

test('mounted HTML is rewritten, other content types are not', async () => {
  const page = await run(html(), `${tools}/TravellerMap/`);
  assert.deepEqual(page.rewrites, [MOUNT]);
  assert.match(await page.response.text(), /rewritten \/TravellerMap/);

  const tile = await run(() => new Response('png', {headers: {'Content-Type': 'image/png'}}), `${tools}/TravellerMap/api/tile`);
  assert.deepEqual(tile.rewrites, []);
  const json = await run(() => new Response('{}', {headers: {'Content-Type': 'application/json'}}), `${tools}/TravellerMap/api/universe`);
  assert.deepEqual(json.rewrites, []);
});

test('HTML on the site\'s own hostname is not rewritten', async () => {
  const {rewrites} = await run(html(), 'https://travellermap.srd-tools.com/');
  assert.deepEqual(rewrites, []);
});

test('the rewritten page is what gets cached, so a hit needs no rewriting', async () => {
  const cache = fakeCache();
  await run(html(), `${tools}/TravellerMap/doc/about`, {cache});
  const hit = await run(html(), `${tools}/TravellerMap/doc/about`, {cache});
  assert.equal(hit.seen.length, 0);
  assert.equal(hit.response.headers.get('X-Edge'), 'HIT');
  assert.deepEqual(hit.rewrites, []);
  assert.match(await hit.response.text(), /rewritten \/TravellerMap/);
});

test('the content-negotiated API keeps Accept in the cache key under the mount', async () => {
  const cache = fakeCache();
  const answer = request => new Response(request.headers.get('Accept'), {headers: {'Cache-Control': 'public, max-age=60', 'Content-Type': 'text/plain'}});
  const url = `${tools}/TravellerMap/api/universe`;
  const json = await run(answer, url, {cache, init: {headers: {Accept: 'application/json'}}});
  const xml = await run(answer, url, {cache, init: {headers: {Accept: 'text/xml'}}});
  assert.equal(await json.response.text(), 'application/json');
  assert.equal(await xml.response.text(), 'text/xml', 'not the cached JSON');
});

test('mounted and unmounted copies of a page are cached separately', async () => {
  const cache = fakeCache();
  await run(html(), `${tools}/TravellerMap/doc/about`, {cache});
  const own = await run(html(), 'https://travellermap.srd-tools.com/doc/about', {cache});
  assert.equal(own.seen.length, 1, 'the unrewritten page is fetched, not the mounted copy');
});

test('a local test setup can use plain http and a port for the origin', async () => {
  const local = {ORIGIN_HOST: 'localhost:5080', ORIGIN_PROTOCOL: 'http:', MOUNT_PATH: MOUNT};
  const {response, seen} = await run(html(), 'http://localhost:8787/TravellerMap/api/tile?x=1', {environment: local});
  assert.notEqual(response.status, 301, 'no https redirect in local testing');
  assert.equal(seen[0].url, 'http://localhost:5080/api/tile?x=1');
});
