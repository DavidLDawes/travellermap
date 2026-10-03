// Tests for deploy/alert-function: how budget and Monitoring notifications become Pushover
// messages, and that the 100% budget alert removes allUsers from the site's invoker role.
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
});
const {describeBudget, describeIncident, handle} = await import('../../deploy/alert-function/index.mjs');

const budget = fields => ({
  budgetDisplayName: 'site', costAmount: 1, budgetAmount: 2, currencyCode: 'USD',
  costIntervalStart: '2026-10-01T07:00:00Z', ...fields,
});
const envelope = data => ({message: {data: Buffer.from(JSON.stringify(data)).toString('base64')}});

test('plain spend updates are ignored', () => {
  assert.equal(describeBudget(budget({})), null);
});

test('budget thresholds map to Pushover priorities', () => {
  const priority = threshold => describeBudget(budget({alertThresholdExceeded: threshold})).priority;
  assert.equal(priority(0.25), 0);
  assert.equal(priority(0.5), 1);
  assert.equal(priority(0.9), 1);
  assert.equal(priority(1), 2);
});

test('only 100% of budget cuts the site off', () => {
  assert.equal(describeBudget(budget({alertThresholdExceeded: 0.9})).cutoff, false);
  assert.equal(describeBudget(budget({alertThresholdExceeded: 1})).cutoff, true);
  assert.equal(describeBudget(budget({forecastThresholdExceeded: 1})).cutoff, false);
});

test('the same threshold in another month is a different alert', () => {
  const october = describeBudget(budget({alertThresholdExceeded: 0.5})).key;
  const november = describeBudget(budget({alertThresholdExceeded: 0.5, costIntervalStart: '2026-11-01T07:00:00Z'})).key;
  assert.notEqual(october, november);
});

test('critical incidents alarm without cutting off; resolved ones are quiet', () => {
  const incident = (state, severity) => describeIncident({
    incident: {incident_id: 'i1', state, policy_name: 'p', summary: 's', policy_user_labels: {severity}},
  });
  assert.deepEqual([incident('open', 'critical').priority, incident('open', 'critical').cutoff], [2, false]);
  assert.equal(incident('open', 'warn').priority, 1);
  assert.equal(incident('closed', 'critical').priority, -1);
});

// handle() with the network replaced: records each request and answers like the real services.
const realFetch = globalThis.fetch;
afterEach(() => { globalThis.fetch = realFetch; });

function fakeNetwork(policy) {
  const calls = [];
  globalThis.fetch = async (url, init = {}) => {
    url = String(url);
    calls.push({url, init});
    const json = body => new Response(JSON.stringify(body), {status: 200});
    if (url.includes('metadata.google.internal')) return json({access_token: 't'});
    if (url.includes('api.pushover.net')) return json({status: 1});
    if (url.endsWith(':getIamPolicy')) return json(policy);
    if (url.endsWith(':setIamPolicy')) return json({});
    throw new Error(`unexpected request: ${url}`);
  };
  return calls;
}

const pushed = calls => calls.filter(c => c.url.includes('pushover')).map(c => Object.fromEntries(c.init.body));

test('100% of budget removes allUsers and sends an emergency message', async () => {
  const calls = fakeNetwork({bindings: [
    {role: 'roles/run.invoker', members: ['allUsers']},
    {role: 'roles/run.admin', members: ['serviceAccount:a@b']},
  ]});
  await handle(envelope(budget({alertThresholdExceeded: 1, costIntervalStart: '2030-01-01T00:00:00Z'})));

  const set = calls.find(c => c.url.endsWith(':setIamPolicy'));
  assert.deepEqual(JSON.parse(set.init.body).policy.bindings,
    [{role: 'roles/run.admin', members: ['serviceAccount:a@b']}]);
  assert.match(set.url, /projects\/proj\/locations\/us-central1\/services\/site:setIamPolicy/);
  const [message] = pushed(calls);
  assert.equal(message.priority, '2');
  assert.equal(message.retry, '60');
  assert.match(message.message, /cut off/);
});

test('a drill alerts but never changes access', async () => {
  const calls = fakeNetwork({bindings: [{role: 'roles/run.invoker', members: ['allUsers']}]});
  await handle(envelope({...budget({alertThresholdExceeded: 1}), drill: true}));
  assert.equal(calls.some(c => c.url.includes('IamPolicy')), false);
  const [message] = pushed(calls);
  assert.match(message.title, /^\[DRILL\]/);
  assert.equal(message.priority, '2');
});

test('cutting off an already-private site changes nothing', async () => {
  const calls = fakeNetwork({bindings: [{role: 'roles/run.admin', members: ['serviceAccount:a@b']}]});
  await handle(envelope(budget({alertThresholdExceeded: 1, costIntervalStart: '2031-01-01T00:00:00Z'})));
  assert.equal(calls.some(c => c.url.endsWith(':setIamPolicy')), false);
  assert.equal(pushed(calls).length, 1);
});

test('a repeated notification for the same threshold is announced once', async () => {
  const calls = fakeNetwork({});
  const repeat = envelope(budget({alertThresholdExceeded: 0.5, costIntervalStart: '2032-01-01T00:00:00Z'}));
  await handle(repeat);
  await handle(repeat);
  assert.equal(pushed(calls).length, 1);
});

test('a Pushover failure is an error, so Pub/Sub redelivers', async () => {
  globalThis.fetch = async () => new Response('nope', {status: 400});
  await assert.rejects(handle(envelope(budget({alertThresholdExceeded: 0.25, costIntervalStart: '2033-01-01T00:00:00Z'}))));
});
