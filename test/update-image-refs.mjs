// Regenerates the ImageTest reference images (test/refs/ref*.png|jpg) from a running server.
// Use only for a deliberate rendering change; review the diffs (git shows the old and new
// images) before committing.
//
//   npm run test:update-refs                                # http://localhost:50103
//   npm run test:update-refs -- --base http://localhost:8080
//   npm run test:update-refs -- ref3 ref28                  # only these
//
// Reads the reference/URL pairs from test/ImageTest.js (each check('refs/...', '/api/...')),
// skipping the intentionally wrong first example.

import { readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const args = process.argv.slice(2);
const baseIndex = args.indexOf('--base');
const BASE = (baseIndex >= 0 ? args[baseIndex + 1] : 'http://localhost:50103').replace(/\/$/, '');
const only = args.filter((a, i) => !a.startsWith('--') && (baseIndex < 0 || i !== baseIndex + 1));

const testDir = path.dirname(fileURLToPath(import.meta.url));
const source = await readFile(path.join(testDir, 'ImageTest.js'), 'utf8');

const pairs = [...source.matchAll(/check\(\s*'(refs\/[^']+)',\s*'([^']+)'/g)]
  .map(m => ({ ref: m[1], url: m[2] }))
  .filter(p => !p.ref.includes('bad_example'))
  .filter(p => only.length === 0 || only.includes(path.basename(p.ref).replace(/\.\w+$/, '')));

if (pairs.length === 0) {
  console.error('No matching references found.');
  process.exit(2);
}

let failed = 0;
for (const { ref, url } of pairs) {
  const response = await fetch(BASE + url);
  const type = response.headers.get('content-type') ?? '';
  const expected = ref.endsWith('.jpg') ? 'image/jpeg' : 'image/png';
  if (!response.ok || !type.startsWith(expected)) {
    console.error(`FAIL ${ref}: ${url} -> ${response.status} ${type} (expected ${expected})`);
    ++failed;
    continue;
  }
  const bytes = Buffer.from(await response.arrayBuffer());
  await writeFile(path.join(testDir, ref), bytes);
  console.log(`${ref.padEnd(16)} ${String(bytes.length).padStart(8)} bytes  ${url}`);
}
process.exit(failed ? 1 : 0);
