// Cloudflare Worker in front of the Cloud Run service (see deploy/README.md, "Cloudflare").
//
// Cloud Run routes by the Host header, and Cloudflare's free plan cannot override Host for a
// proxied DNS record, so this Worker fetches the run.app URL itself (which sets the right Host),
// caches the answers at the edge, and rewrites redirects back to the public hostname.
//
// Caching follows the origin's own Cache-Control (public, max-age=...), capped at EDGE_MAX_TTL so a
// deploy shows up within an hour. The Cache API ignores `Vary`, but the site picks JSON/XML/PNG
// by the Accept header on the same URL, so Accept is part of the cache key.
//
// Mounting: the same Worker also serves the site under a path of another site, MOUNT_PATH
// ("/TravellerMap" on srd-tools.com). There the prefix is removed before the origin sees the path,
// and put back on what the origin sends that names its own paths: redirect Locations and
// root-absolute links in HTML ("/doc/about"). URLs that the pages build in JavaScript are made
// relative to the site's root by the pages themselves (map.js, SERVICE_BASE).

const EDGE_MAX_TTL = 3600; // seconds

// HTML attributes that hold a URL. Root-absolute ones get the mount prefix.
const URL_ATTRIBUTES = ['href', 'src', 'action', 'formaction', 'poster'];

/**
 * Where a public path stands relative to the mount:
 *   {originPath, prefix}  serve it; send originPath to the origin, put prefix back on the answer
 *   {redirect}            send the visitor to the canonical path first
 * Paths outside the mount (the site's own hostname) are served as they are, with no prefix.
 */
export function resolveMount(pathname, mount) {
  if (!mount) return {originPath: pathname, prefix: ''};
  if (pathname.startsWith(mount + '/')) return {originPath: pathname.slice(mount.length), prefix: mount};
  // "/TravellerMap" has to become "/TravellerMap/", or the page's relative URLs resolve one level up.
  if (pathname === mount) return {redirect: mount + '/'};
  // Paths are case-sensitive; send "/travellermap/..." and friends to the canonical spelling.
  const lower = pathname.toLowerCase();
  const lowerMount = mount.toLowerCase();
  if (lower === lowerMount || lower.startsWith(lowerMount + '/'))
    return {redirect: mount + (pathname.slice(mount.length) || '/')};
  return {originPath: pathname, prefix: ''};
}

/** A root-absolute URL ("/x", not "//host/x") with the prefix added; anything else unchanged. */
export function withPrefix(value, prefix) {
  if (!prefix || !value.startsWith('/') || value.startsWith('//')) return value;
  return prefix + value;
}

// Whether the app chooses the answer's format by the Accept header. Everything it generates may
// (/api/, /data/, /t5ss/: JSON, XML, text, PNG, PDF...), and none of its routes ends in a file
// extension; static files do, and don't vary. Decided on the origin's path (same with or without a mount).
export function variesByAccept(originPath) {
  return !/\.[A-Za-z0-9]+$/.test(originPath);
}

// The key the Cache API files an entry under: the public URL, plus Accept where the answer depends on it.
function cacheKeyFor(request, originPath) {
  const url = new URL(request.url);
  if (variesByAccept(originPath)) {
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

// Redirects from the app name the host it was called with (the run.app one), and paths from its
// own root ("/?sector=..."). Point both at the public host and, when mounted, under the prefix.
function rewriteLocation(headers, originHost, publicUrl, prefix) {
  const location = headers.get('Location');
  if (!location) return;
  if (location.startsWith('/') && !location.startsWith('//')) {
    headers.set('Location', withPrefix(location, prefix));
    return;
  }
  let target;
  try {
    target = new URL(location);
  } catch {
    return; // relative to the current path: right as it is
  }
  if (target.host !== originHost) return;
  target.protocol = 'https:';
  target.host = publicUrl.host;
  target.pathname = withPrefix(target.pathname, prefix);
  headers.set('Location', target.toString());
}

// Prefixes root-absolute URL attributes in an HTML response, streaming (Cloudflare's HTMLRewriter).
function rewriteHtmlLinks(response, prefix) {
  const rewriter = new HTMLRewriter();
  for (const name of URL_ATTRIBUTES) {
    rewriter.on(`[${name}^="/"]`, {
      element(element) {
        const value = element.getAttribute(name);
        if (value !== null) element.setAttribute(name, withPrefix(value, prefix));
      },
    });
  }
  return rewriter.transform(response);
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
 * @param {{ORIGIN_HOST: string, ORIGIN_PROTOCOL?: string, MOUNT_PATH?: string}} env
 *   ORIGIN_HOST may include a port, and ORIGIN_PROTOCOL may be "http:", for local testing.
 * @param {{waitUntil(promise: Promise<unknown>): void}} ctx
 * @param {{match(key: Request): Promise<Response | undefined>, put(key: Request, response: Response): Promise<void>}} cache
 * @param {(request: Request, init?: RequestInit) => Promise<Response>} fetchOrigin
 * @param {(response: Response, prefix: string) => Response} rewriteHtml prefixes links in HTML
 */
export async function handle(request, env, ctx, cache, fetchOrigin, rewriteHtml = rewriteHtmlLinks) {
  const publicUrl = new URL(request.url);
  if (publicUrl.protocol === 'http:' && env.ORIGIN_PROTOCOL !== 'http:') {
    publicUrl.protocol = 'https:';
    return Response.redirect(publicUrl.toString(), 301);
  }

  const mount = resolveMount(publicUrl.pathname, env.MOUNT_PATH);
  if (mount.redirect) {
    const target = new URL(publicUrl);
    target.pathname = mount.redirect;
    return Response.redirect(target.toString(), 301);
  }
  const {originPath, prefix} = mount;

  const originUrl = new URL(request.url);
  originUrl.protocol = env.ORIGIN_PROTOCOL || 'https:';
  originUrl.port = ''; // setting host keeps an old port unless the new host names one
  originUrl.host = env.ORIGIN_HOST;
  originUrl.pathname = originPath;

  // Only plain GETs are cached: not HEAD, not POST, not partial (Range) requests.
  const cacheable = request.method === 'GET' && !request.headers.has('Range');
  const key = cacheable ? cacheKeyFor(request, originPath) : null;
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
  let response = new Response(originResponse.body, originResponse);
  rewriteLocation(response.headers, env.ORIGIN_HOST, publicUrl, prefix);
  response.headers.set('X-Edge', 'MISS');
  // Mounted HTML is cached after rewriting, so a cache hit needs no work.
  if (prefix && (response.headers.get('Content-Type') ?? '').startsWith('text/html'))
    response = rewriteHtml(response, prefix);

  const ttl = key ? originTtl(response) : 0;
  if (ttl > 0) ctx.waitUntil(cache.put(key, forCache(response, ttl)));
  return response;
}

export default {
  fetch(request, env, ctx) {
    return handle(request, env, ctx, caches.default, fetch);
  },
};
