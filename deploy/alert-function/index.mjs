// Cost-alert responder, run as its own Cloud Run service (see deploy/README.md).
//
// Receives Pub/Sub push messages from two sources on the same topic:
//   - Cloud Billing budget notifications (spend vs. the budget; can lag by hours)
//   - Cloud Monitoring incidents on the site's Cloud Run metrics (minutes)
// Sends a Pushover notification for each, and at 100% of the budget cuts the site off by removing
// its public (allUsers) invoker binding. Restore with `gcloud run services add-iam-policy-binding`
// (command in deploy/README.md). No dependencies: it uses Node's fetch and the metadata server.
//
// Environment: PUSHOVER_TOKEN, PUSHOVER_USER (from Secret Manager); PROJECT_ID, REGION, SERVICE
// (the site's Cloud Run service); STATE_BUCKET (optional: remembers what was already announced, and
// keeps latest-budget.json and latest-alert.json for /admin/budget to read);
// CUTOFF_ON_CRITICAL=1 (optional: Monitoring "critical" incidents also cut the site off).

import http from 'node:http';
import { pathToFileURL } from 'node:url';

const {
  PORT = '8080',
  PUSHOVER_TOKEN,
  PUSHOVER_USER,
  PROJECT_ID,
  REGION,
  SERVICE,
  CUTOFF_ON_CRITICAL,
} = process.env;

// Read when used (not at import), so tests can switch it.
const stateBucket = () => process.env.STATE_BUCKET;

const INVOKER_ROLE = 'roles/run.invoker';
const announced = new Set(); // fallback when there is no STATE_BUCKET; lost when the instance stops

const log = (severity, message, extra = {}) =>
  console.log(JSON.stringify({ severity, message, ...extra }));

async function accessToken() {
  const response = await fetch(
    'http://metadata.google.internal/computeMetadata/v1/instance/service-accounts/default/token',
    { headers: { 'Metadata-Flavor': 'Google' } });
  if (!response.ok) throw new Error(`metadata token: ${response.status}`);
  return (await response.json()).access_token;
}

async function pushover({ title, message, priority, url }) {
  const body = new URLSearchParams({
    token: PUSHOVER_TOKEN, user: PUSHOVER_USER, title, message, priority: String(priority),
  });
  if (priority === 2) {
    // Emergency: repeats every `retry` seconds until acknowledged, for up to `expire` seconds.
    body.set('retry', '60');
    body.set('expire', '3600');
    body.set('sound', 'siren');
  }
  if (url) body.set('url', url);
  const response = await fetch('https://api.pushover.net/1/messages.json', { method: 'POST', body });
  if (!response.ok) throw new Error(`pushover: ${response.status} ${await response.text()}`);
}

// Removes allUsers from the site's invoker role, so it answers 403. Idempotent.
async function cutOff() {
  const base = `https://run.googleapis.com/v2/projects/${PROJECT_ID}/locations/${REGION}/services/${SERVICE}`;
  const headers = { Authorization: `Bearer ${await accessToken()}`, 'Content-Type': 'application/json' };
  const got = await fetch(`${base}:getIamPolicy`, { headers });
  if (!got.ok) throw new Error(`getIamPolicy: ${got.status} ${await got.text()}`);
  const policy = await got.json();
  let changed = false;
  policy.bindings = (policy.bindings ?? []).flatMap(binding => {
    if (binding.role !== INVOKER_ROLE || !binding.members?.includes('allUsers')) return [binding];
    changed = true;
    const members = binding.members.filter(member => member !== 'allUsers');
    return members.length ? [{ ...binding, members }] : [];
  });
  if (!changed) return false;
  const set = await fetch(`${base}:setIamPolicy`, { method: 'POST', headers, body: JSON.stringify({ policy }) });
  if (!set.ok) throw new Error(`setIamPolicy: ${set.status} ${await set.text()}`);
  return true;
}

// True if `key` was announced before; otherwise records it. Announcing the same threshold again
// (budget notifications repeat several times a day) would be noise.
async function alreadyAnnounced(key) {
  const bucket = stateBucket();
  if (!bucket) {
    if (announced.has(key)) return true;
    announced.add(key);
    return false;
  }
  const headers = { Authorization: `Bearer ${await accessToken()}` };
  const name = encodeURIComponent(`announced/${key}`);
  const found = await fetch(`https://storage.googleapis.com/storage/v1/b/${bucket}/o/${name}`, { headers });
  if (found.ok) return true;
  if (found.status !== 404) throw new Error(`state lookup: ${found.status}`);
  return false;
}

async function markAnnounced(key) {
  const bucket = stateBucket();
  if (!bucket) return;
  const headers = { Authorization: `Bearer ${await accessToken()}` };
  const name = encodeURIComponent(`announced/${key}`);
  const response = await fetch(
    `https://storage.googleapis.com/upload/storage/v1/b/${bucket}/o?uploadType=media&name=${name}`,
    { method: 'POST', headers, body: new Date().toISOString() });
  if (!response.ok) throw new Error(`state write: ${response.status}`);
}

// Overwrites a small JSON status file in the state bucket, which /admin/budget reads
// (latest-budget.json: the newest budget notification; latest-alert.json: the last alert sent).
// Best effort: a failure here is logged but never stops an alert from going out.
export async function writeState(name, value) {
  const bucket = stateBucket();
  if (!bucket) return;
  try {
    const headers = { Authorization: `Bearer ${await accessToken()}`, 'Content-Type': 'application/json' };
    const response = await fetch(
      `https://storage.googleapis.com/upload/storage/v1/b/${bucket}/o?uploadType=media&name=${encodeURIComponent(name)}`,
      { method: 'POST', headers, body: JSON.stringify(value) });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
  } catch (error) {
    log('WARNING', `could not write ${name}: ${error}`);
  }
}

const money = (amount, currency) => `${amount.toFixed(2)} ${currency}`;

// A budget notification: {budgetDisplayName, costAmount, budgetAmount, currencyCode,
// costIntervalStart, alertThresholdExceeded?, forecastThresholdExceeded?}. Most are plain spend
// updates with neither threshold; those are ignored.
export function describeBudget(data) {
  const actual = data.alertThresholdExceeded;
  const forecast = data.forecastThresholdExceeded;
  if (actual === undefined && forecast === undefined) return null;
  const currency = data.currencyCode ?? 'USD';
  const spend = `${money(data.costAmount, currency)} of ${money(data.budgetAmount, currency)}`;
  const month = String(data.costIntervalStart ?? '').slice(0, 7);
  if (actual !== undefined) {
    const percent = Math.round(actual * 100);
    return {
      key: `${month}-budget-actual-${percent}`,
      title: `${SERVICE}: ${percent}% of budget spent`,
      message: `${data.budgetDisplayName}: ${spend} spent this month.`,
      priority: actual >= 1 ? 2 : actual >= 0.5 ? 1 : 0,
      cutoff: actual >= 1,
    };
  }
  const percent = Math.round(forecast * 100);
  return {
    key: `${month}-budget-forecast-${percent}`,
    title: `${SERVICE}: forecast ${percent}% of budget`,
    message: `${data.budgetDisplayName}: ${spend} so far, forecast to reach ${percent}% this month.`,
    priority: 1,
    cutoff: false,
  };
}

// A Cloud Monitoring incident: {incident: {incident_id, state, summary, url, policy_name,
// policy_user_labels: {severity: 'critical'|'warn'}}}.
export function describeIncident({ incident }) {
  const critical = incident.policy_user_labels?.severity === 'critical';
  if (incident.state === 'open') {
    return {
      key: `incident-${incident.incident_id}-open`,
      title: `${SERVICE}: ${incident.policy_name}`,
      message: incident.summary,
      url: incident.url,
      priority: critical ? 2 : 1,
      cutoff: critical && CUTOFF_ON_CRITICAL === '1',
    };
  }
  return {
    key: `incident-${incident.incident_id}-closed`,
    title: `${SERVICE}: ${incident.policy_name} (resolved)`,
    message: incident.summary,
    url: incident.url,
    priority: -1,
    cutoff: false,
  };
}

export async function handle(envelope) {
  const data = JSON.parse(Buffer.from(envelope.message.data, 'base64').toString('utf8'));

  // "drill": true in a hand-published test message: notify at the given level, but never cut
  // off, and never remember it as a real notification (see deploy/README.md).
  const drill = data.drill === true;

  // Keep the newest budget notification, whether or not it crossed a threshold (most do not).
  if (data.budgetDisplayName && !drill) {
    await writeState('latest-budget.json', {
      receivedAt: new Date().toISOString(),
      budgetDisplayName: data.budgetDisplayName,
      costAmount: data.costAmount,
      budgetAmount: data.budgetAmount,
      currencyCode: data.currencyCode,
      costIntervalStart: data.costIntervalStart,
      alertThresholdExceeded: data.alertThresholdExceeded,
      forecastThresholdExceeded: data.forecastThresholdExceeded,
    });
  }

  const alert = data.incident ? describeIncident(data) : data.budgetDisplayName ? describeBudget(data) : null;
  if (!alert) return;

  if (!drill && await alreadyAnnounced(alert.key)) return;

  let cutOffNow = false;
  if (alert.cutoff && !drill) cutOffNow = await cutOff();
  await pushover({
    title: `${drill ? '[DRILL] ' : ''}${alert.title}`,
    message: alert.message + (cutOffNow ? '\nSite cut off (public access removed).' : ''),
    priority: alert.priority,
    url: alert.url,
  });
  if (!drill) await markAnnounced(alert.key);
  await writeState('latest-alert.json', {
    time: new Date().toISOString(), title: alert.title, priority: alert.priority, cutOff: cutOffNow, drill,
  });
  log('NOTICE', 'alert sent', { key: alert.key, priority: alert.priority, cutOff: cutOffNow, drill });
}

const server = http.createServer((request, response) => {
  if (request.method !== 'POST') {
    response.writeHead(200).end('ok');
    return;
  }
  const chunks = [];
  let size = 0;
  request.on('data', chunk => {
    size += chunk.length;
    if (size > 1 << 20) request.destroy();
    else chunks.push(chunk);
  });
  request.on('end', async () => {
    try {
      await handle(JSON.parse(Buffer.concat(chunks).toString('utf8')));
      response.writeHead(204).end();
    } catch (error) {
      // A non-2xx response makes Pub/Sub redeliver, so a failed notification is retried.
      log('ERROR', String(error?.stack ?? error));
      response.writeHead(500).end();
    }
  });
});

// Not started when imported by the unit tests.
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  server.listen(Number(PORT), () => log('INFO', `listening on ${PORT}`));
}
