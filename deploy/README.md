# Deploying to Cloud Run

Every push to `main` that passes CI builds the root `Dockerfile` and deploys it to Google Cloud Run
(`.github/workflows/ci.yml`, job `deploy`). The design goal is **$0/month**, with an alarm on your
phone if that stops being true.

```
GitHub Actions ──(keyless, main branch only)──> Artifact Registry ──> Cloud Run "travellermap"
                                                                        │  AdminKey from Secret Manager
Cloud Billing budget ($2/month) ──┐                                     │
Cloud Monitoring alerts ──────────┴─> Pub/Sub "cost-alerts" ──> Cloud Run "cost-alerts" ──> Pushover
                                                                        └─ at 100% of budget: removes the
                                                                           site's public access (403)
```

## What the settings are for

| Setting | Why |
| --- | --- |
| 1 vCPU, 1 GiB | Below 1 vCPU, Cloud Run forces concurrency 1, which would serialize tile requests. |
| `--max-instances 2`, `--concurrency 20` | Caps both capacity and the worst-case bill. |
| `--min-instances 0` | Scales to zero, so idle time is free. First request after idle takes a few seconds. |
| `--cpu-boost` | Faster cold starts. You pay for the extra CPU only during startup. |
| us-central1 | The always-free tier covers us-central1, us-east1 and us-west1. |

**Free tier (request-based billing):** 180,000 vCPU-seconds (= **50 hours** of busy 1-vCPU instance
time), 360,000 GiB-seconds, 2 million requests, and 1 GiB of egress per month. Idle time between
requests is not billed. Past the free tier it is about $0.086 per busy vCPU-hour. Always-on
(`--min-instances 1`) would use the whole allowance in about two days.

## Secrets

Runtime secrets live **only** in Secret Manager. They never pass through GitHub or the repo.

| Secret | Read by | How |
| --- | --- | --- |
| `admin-key` | the site (`site-runtime` account) | `--set-secrets AdminKey=admin-key:latest` → the `AdminKey` env var |
| `pushover-token`, `pushover-user` | the alert responder (`cost-alerts` account) | `--set-secrets` → env vars |

Rotate one with `printf %s "$NEW" | gcloud secrets versions add admin-key --data-file=-`, then
redeploy (`:latest` is resolved when a revision starts). The GitHub repository variables
(`GCP_PROJECT_ID`, …) are identifiers, not secrets.

## Setup

You need `gcloud` (logged in as an owner of the project and billing account), `gh`, `node`, `curl`.
Run these from the repo root in Git Bash. Use a dedicated project, so the budget and the cutoff
only ever affect this site. (`gcloud billing accounts list` shows your billing accounts.)

```bash
export PROJECT_ID=travellermap-xxxx            # globally unique
export BILLING_ACCOUNT=XXXXXX-XXXXXX-XXXXXX

./deploy/setup.sh project infra wif github     # project, APIs, registry, accounts, keyless GitHub access
./deploy/setup.sh secrets                      # prompts (hidden) for the admin key and Pushover keys
./deploy/setup.sh alerts budget                # alert responder, Monitoring policies, $2 budget
```

Then merge to `main`. The first deploy creates the site **privately** (403). Check the run, then:

```bash
./deploy/setup.sh live                         # public, and lets the responder cut it off
```

Why a separate step: deploys never touch public access, so a deploy cannot silently undo a cost
cutoff.

## Admin pages

Open an admin page over HTTPS with the key from Secret Manager as `?key=`:

```
https://travellermap.srd-tools.com/admin/overview?key=YOUR_ADMIN_KEY
```

Without the key (or over plain HTTP) the answer is 403 "Incorrect secret or connection not secure."
To read the key: `gcloud secrets versions access latest --secret=admin-key --project travellermap-dld`.

| Page | Shows |
| --- | --- |
| `/admin/overview` | Overview report (a good first page) |
| `/admin/errors` | Data errors found in the sector files |
| `/admin/uptime` | Server uptime |
| `/admin/codes`, `/admin/routes`, `/admin/dump`, `/admin/profile` | Code tables, route table, data dump, profiling |
| `/admin/flush`, `/admin/reindex` | **Change state:** reload cached sector data; rebuild the search index |

`/admin/admin` is the shared handler behind `flush`, `reindex`, `profile` and `uptime`. It needs an
`action` and prints "Unknown action:" without one, so don't use it as a landing page.

Each Cloud Run instance has its own memory and search index, so `flush` and `reindex` affect only the
instance that answers. Data changes go out by deploying, not through these pages. The key travels in the
URL, so it ends up in browser history and logs; don't share links that contain it.

## Fire drill

An alarm you have never triggered is a guess. The drill messages go through Pub/Sub exactly as real
ones do. A `"drill": true` message never changes access and is not remembered.

```bash
./deploy/setup.sh drill budget 0.5             # normal "50% of budget" message
./deploy/setup.sh drill budget 1               # emergency priority: the phone should alarm until acknowledged
./deploy/setup.sh drill monitoring critical    # the Monitoring path
```

To test the real cutoff end to end, run `./deploy/setup.sh cutoff`, then poll the site until it answers
403, then `./deploy/setup.sh restore`. **Removing access takes about 100 seconds to take effect** (measured:
still 200 after 90 s, 403 at about 100 s); restoring it takes seconds. A cutoff is therefore not
instantaneous, which is one more reason for the `--max-instances 2` cap.

## Alerts

| Event | Pushover | Action |
| --- | --- | --- |
| Budget 25% ($0.50) | normal | |
| Budget 50% / 90%, or forecast to reach 100% | high (bypasses quiet hours) | |
| **Budget 100% ($2)** | **emergency** (repeats every 60 s for an hour, until acknowledged) | **site made private** |
| Request rate over 5/s for 10 min (`critical`) | emergency | |
| Both instances busy for 10 min (`critical`) | emergency | |
| Billable time over 0.15 instance-s/s for an hour (`warn`) | high | |
| An incident resolves | lowest (silent) | |

- Budget notifications can lag spending by hours. The Monitoring alerts react in minutes; the budget
  is the backstop.
- Monitoring `critical` alerts do not cut the site off. To make them do so, set
  `CUTOFF_ON_CRITICAL=1` on the `cost-alerts` service.
- The monitoring thresholds in `deploy/monitoring/*.json` are starting guesses. Watch the real
  numbers for a few weeks, then edit the file, delete the policy in the Cloud Console, and re-run
  `./deploy/setup.sh alerts`.
- Change the budget: edit it in the Cloud Console (Billing → Budgets), or re-run `budget` with
  `BUDGET_USD=5` after deleting the old one.

## After a cutoff

The site answers 403. Find out why (Cloud Run logs, the Monitoring incident, the billing report),
then `./deploy/setup.sh restore`.

## Cloudflare in front (free): travellermap.srd-tools.com

`deploy/cloudflare/` is a Worker that serves the site's hostname and caches in front of Cloud Run.
Why a Worker: Cloud Run routes by the `Host` header, and overriding Host for a proxied DNS record is
an Enterprise feature. The Worker fetches the `run.app` URL itself, which sets the right Host.

- **Caching** follows the app's own `Cache-Control: public, max-age=...`, capped at 1 hour at the edge, so a
  deploy shows up within an hour. `Accept` is part of the cache key, because the API picks JSON, XML or
  PNG by `Accept` on one URL and Cloudflare's cache ignores `Vary`. Only plain 200 GETs that are `public`
  and cookie-free are cached; `/admin` pages (not `public`) never are. Redirects are rewritten from the
  `run.app` host to the public one. Responses carry `X-Edge: HIT` or `MISS`.
- **Free-plan limit: 100,000 Worker requests per day** (resets at midnight UTC), and cache hits count.
  Past it, visitors get Cloudflare error 1027 until the reset. Each map view is roughly 10-30 requests.
  The Cache API is per datacenter, so a cache hit is not guaranteed in every location.
- **The `run.app` URL still works directly.** Cloudflare saves requests and money; it is not a shield.
  The budget cutoff is the real protection.
- Deploy: `cd deploy/cloudflare && npx wrangler@latest deploy` (after `wrangler login`; the zone
  `srd-tools.com` must be on that Cloudflare account). It creates the DNS record and certificate.
  Don't create a DNS record for the hostname by hand. The Worker rarely changes, so this is a manual step,
  not part of CI. If the Cloud Run service is ever recreated with a different URL, update `ORIGIN_HOST`
  in `wrangler.jsonc`.
- Tests: `test/unit/edge-worker.test.js` (run by `npm test`).
