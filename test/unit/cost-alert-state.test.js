// Tests for the status files deploy/alert-function writes for /admin/budget:
// latest-budget.json (newest budget notification) and latest-alert.json (last alert sent).
import assert from 'node:assert/strict';
import {Buffer} from 'node:buffer';
import process from 'node:process';
import {afterEach, test} from 'node:test';

Object.assign(process.env, {
  PUSHOVER_TOKEN: 'app-token',
  PUSHOVER_USER: 'user-key',
  PROJECT_ID: 'proj',
  REGION: 'us-central1',
  SERVICE: 'site',
  STATE_BUCKET: 'state-bucket',
});
const {handle} = await import('../../deploy/alert-function/index.mjs');

const envelope = data => ({message: {data: Buffer.from(JSON.stringify(data)).toString('base64')}});
const budget = fields => ({
  budgetDisplayName: 'site', costAmount: 1, budgetAmount: 2, currencyCode: 'USD',
  costIntervalStart: '2026-10-01T07:00:00Z', ...fields,
});

const realFetch = globalThis.fetch;
afterEach(() => { globalThis.fetch = realFetch; });

// A fake network with a working state bucket: uploads are recorded by object name.
function fakeNetwork({failUploads = false} = {}) {
  const uploads = new Map();
  const pushes = [];
  globalThis.fetch = async (url, init = {}) => {
    url = String(url);
    const json = body => new Response(JSON.stringify(body), {status: 200});
    if (url.includes('metadata.google.internal')) return json({access_token: 't'});
    if (url.includes('api.pushover.net')) { pushes.push(Object.fromEntries(init.body)); return json({status: 1}); }
    if (url.includes('/upload/storage/v1/b/state-bucket/o')) {
      if (failUploads) return new Response('denied', {status: 403});
      uploads.set(decodeURIComponent(new URL(url).searchParams.get('name')), init.body);
      return json({});
    }
    if (url.includes('/storage/v1/b/state-bucket/o/announced')) return new Response('', {status: 404});
    if (url.endsWith(':getIamPolicy')) return json({bindings: []});
    throw new Error(`unexpected request: ${url}`);
  };
  return {uploads, pushes};
}

test('a plain budget update is remembered even though it sends no alert', async () => {
  const {uploads, pushes} = fakeNetwork();
  await handle(envelope(budget({costAmount: 0.37})));

  const saved = JSON.parse(uploads.get('latest-budget.json'));
  assert.equal(saved.costAmount, 0.37);
  assert.equal(saved.budgetAmount, 2);
  assert.equal(saved.currencyCode, 'USD');
  assert.equal(saved.costIntervalStart, '2026-10-01T07:00:00Z');
  assert.ok(Date.parse(saved.receivedAt) > 0, 'when it arrived');
  assert.equal(pushes.length, 0);
  assert.equal(uploads.has('latest-alert.json'), false, 'no alert was sent');
});

test('a threshold alert records the notification, the announcement and the last alert', async () => {
  const {uploads, pushes} = fakeNetwork();
  await handle(envelope(budget({costAmount: 1.1, alertThresholdExceeded: 0.5, costIntervalStart: '2040-10-01T07:00:00Z'})));

  assert.equal(JSON.parse(uploads.get('latest-budget.json')).alertThresholdExceeded, 0.5);
  assert.ok(uploads.has('announced/2040-10-budget-actual-50'));
  const last = JSON.parse(uploads.get('latest-alert.json'));
  assert.equal(last.priority, 1);
  assert.equal(last.drill, false);
  assert.equal(last.cutOff, false);
  assert.match(last.title, /50% of budget/);
  assert.equal(pushes.length, 1);
});

test('a drill is recorded as the last alert but not as a budget notification', async () => {
  const {uploads} = fakeNetwork();
  await handle(envelope({...budget({alertThresholdExceeded: 1}), drill: true}));

  assert.equal(uploads.has('latest-budget.json'), false, 'a drill must not look like real spend');
  assert.equal([...uploads.keys()].some(name => name.startsWith('announced/')), false);
  const last = JSON.parse(uploads.get('latest-alert.json'));
  assert.equal(last.drill, true);
  assert.equal(last.priority, 2);
});

test('a failure writing the status files does not stop the alert', async () => {
  const {pushes} = fakeNetwork({failUploads: true});
  // The announcement marker is not best-effort, so use a drill, which skips it.
  await handle(envelope({...budget({alertThresholdExceeded: 0.9}), drill: true}));
  assert.equal(pushes.length, 1);
});
