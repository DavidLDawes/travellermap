// Cloudflare Worker in front of the Cloud Run service (see deploy/README.md, "Cloudflare").
//
// Cloud Run routes by the Host header, and Cloudflare's free plan cannot override Host for a
// proxied DNS record, so this Worker fetches the run.app URL itself (which sets the right Host),
// caches the answers at the edge, and rewrites redirects back to the public hostname.
//
// Caching follows the origin's own Cache-Control (public, max-age=...), capped at EDGE_MAX_TTL so a
// deploy shows up within an hour. The Cache API ignores `Vary`, but the site picks JSON/XML/PNG
// by the Accept header on the same URL, so Accept is part of the cache key.

const EDGE_MAX_TTL = 3600; // seconds

// The key the Cache API files an entry under: the public URL, plus Accept for the content-negotiated API.
function cacheKeyFor(request) {
  const url = new URL(request.url);
  if (url.pathname.startsWith('/api/')) {
    url.searchParams.set('~accept', (request.headers.get('Accept') ?? '').slice(0, 200));
  }
  return new Request(url.toString(), {method: 'GET'});
}

// Seconds the origin allows a shared cache to keep this response; 0 means don't cache it.
function originTtl(response) {
  if (response.status !== 200 || response.headers.has('Set-Cookie')) return 0;
  const cacheControl = response.headers.get('Cache-Control') ?? '';
  if (!/\bpublic\b/i.test(cacheControl)) return 0;
  const maxAge = /\bmax-age=(\d+)/i.exec(cacheControl);
  return maxAge ? Number(maxAge[1]) : 0;
}

// Redirects from the app name the host it was called with (the run.app one).
function rewriteLocation(headers, originHost, publicHost) {
  const location = headers.get('Location');
  if (!location) return;
  try {
    const target = new URL(location);
    if (target.host !== originHost) return;
    target.protocol = 'https:';
    target.host = publicHost;
    headers.set('Location', target.toString());
  } catch {
    // A relative Location needs no change.
  }
}

// What a visitor gets for a cached entry: the origin's own headers restored.
function fromCache(cached) {
  const response = new Response(cached.body, cached);
  for (const name of ['Cache-Control', 'Vary']) {
    const original = response.headers.get(`X-Origin-${name}`);
    if (original === null) response.headers.delete(name);
    else response.headers.set(name, original);
    response.headers.delete(`X-Origin-${name}`);
  }
  response.headers.set('X-Edge', 'HIT');
  return response;
}

// What goes into the cache: the origin's headers kept aside, the lifetime capped, `Vary` removed.
function forCache(response, ttl) {
  const stored = response.clone();
  for (const name of ['Cache-Control', 'Vary']) {
    const original = response.headers.get(name);
    if (original !== null) stored.headers.set(`X-Origin-${name}`, original);
  }
  stored.headers.set('Cache-Control', `public, max-age=${Math.min(ttl, EDGE_MAX_TTL)}`);
  stored.headers.delete('Vary');
  return stored;
}

/**
 * @param {Request} request
 * @param {{ORIGIN_HOST: string}} env
 * @param {{waitUntil(promise: Promise<unknown>): void}} ctx
 * @param {{match(key: Request): Promise<Response | undefined>, put(key: Request, response: Response): Promise<void>}} cache
 * @param {(request: Request, init?: RequestInit) => Promise<Response>} fetchOrigin
 */
export async function handle(request, env, ctx, cache, fetchOrigin) {
  const publicUrl = new URL(request.url);
  const originUrl = new URL(request.url);
  originUrl.protocol = 'https:';
  originUrl.hostname = env.ORIGIN_HOST;
  originUrl.port = '';

  // Only plain GETs are cached: not HEAD, not POST, not partial (Range) requests.
  const cacheable = request.method === 'GET' && !request.headers.has('Range');
  const key = cacheable ? cacheKeyFor(request) : null;
  if (key) {
    const cached = await cache.match(key);
    if (cached) return fromCache(cached);
  }

  let originResponse;
  try {
    originResponse = await fetchOrigin(new Request(originUrl, request), {redirect: 'manual'});
  } catch (error) {
    console.error(JSON.stringify({message: 'origin fetch failed', error: String(error)}));
    return new Response('The map server is not answering.', {status: 502});
  }

  // A mutable copy (a fetch() response's headers are immutable).
  const response = new Response(originResponse.body, originResponse);
  rewriteLocation(response.headers, env.ORIGIN_HOST, publicUrl.host);
  response.headers.set('X-Edge', 'MISS');

  const ttl = key ? originTtl(response) : 0;
  if (ttl > 0) ctx.waitUntil(cache.put(key, forCache(response, ttl)));
  return response;
}

export default {
  fetch(request, env, ctx) {
    return handle(request, env, ctx, caches.default, fetch);
  },
};
