#!/usr/bin/env bash
# One-time Google Cloud setup for the Cloud Run deployment. See deploy/README.md.
#
#   PROJECT_ID=travellermap-xxxx BILLING_ACCOUNT=XXXXXX-XXXXXX-XXXXXX ./deploy/setup.sh <stage>...
#
# Stages, in order (each is safe to re-run):
#   project   create the project and link billing           (needs BILLING_ACCOUNT)
#   infra     APIs, image registry, state bucket, service accounts
#   wif       let GitHub Actions deploy, keyless (Workload Identity Federation)
#   github    set the repository variables the deploy job reads, via `gh`
#   secrets   prompt for the secrets and store them in Secret Manager (hidden input)
#   alerts    deploy the alert responder, Pub/Sub wiring and Cloud Monitoring alert policies
#   budget    create the monthly budget and its notification thresholds   (needs BILLING_ACCOUNT)
#   live      after the first deploy: make the site public and let the responder cut it off
#   restore   make the site public again (after a cutoff)
#   status-access   let the site read its own Cloud Monitoring metrics (read-only), for /admin/fleet and /admin/usage
#   cutoff    make the site private by hand (what the responder does at 100% of budget)
#   drill budget|monitoring [level]   send a test alert through the whole chain
#
# Needs: gcloud (logged in as a project owner), curl, node. `github` also needs gh.

set -euo pipefail
cd "$(dirname "$0")/.."

: "${PROJECT_ID:?set PROJECT_ID}"
REGION="${REGION:-us-central1}"          # Cloud Run's free tier covers us-central1, us-east1, us-west1
SERVICE="${SERVICE:-travellermap}"
REGISTRY="${REGISTRY:-travellermap}"     # Artifact Registry repository
GITHUB_REPO="${GITHUB_REPO:-DavidLDawes/travellermap}"
BUDGET_USD="${BUDGET_USD:-2}"
TOPIC="cost-alerts"
ALERTS_SERVICE="cost-alerts"
STATE_BUCKET="${PROJECT_ID}-alert-state"
GENERATED="deploy/.generated"            # git-ignored; policy files with the placeholders filled in

g() { gcloud --project "$PROJECT_ID" "$@"; }
say() { printf '\n== %s\n' "$*"; }
project_number() { g projects describe "$PROJECT_ID" --format='value(projectNumber)'; }
sa() { echo "$1@${PROJECT_ID}.iam.gserviceaccount.com"; }
image() { echo "${REGION}-docker.pkg.dev/${PROJECT_ID}/${REGISTRY}/$1"; }
token() { gcloud auth print-access-token; }
api() { # api METHOD URL [JSON-FILE]
  local method="$1" url="$2" body="${3:-}"
  curl -sS -X "$method" -H "Authorization: Bearer $(token)" -H "x-goog-user-project: $PROJECT_ID" \
    -H 'Content-Type: application/json' ${body:+--data-binary "@$body"} "$url"
}

grant() { # grant SERVICE_ACCOUNT_NAME ROLE   (project level)
  g projects add-iam-policy-binding "$PROJECT_ID" --member="serviceAccount:$(sa "$1")" \
    --role="$2" --condition=None --quiet >/dev/null
}

ensure_sa() { # ensure_sa NAME DISPLAY
  g iam service-accounts describe "$(sa "$1")" >/dev/null 2>&1 \
    || g iam service-accounts create "$1" --display-name="$2"
}

stage_project() {
  : "${BILLING_ACCOUNT:?set BILLING_ACCOUNT (gcloud billing accounts list)}"
  say "Project $PROJECT_ID"
  gcloud projects describe "$PROJECT_ID" >/dev/null 2>&1 || gcloud projects create "$PROJECT_ID" --name="travellermap"
  gcloud billing projects link "$PROJECT_ID" --billing-account="$BILLING_ACCOUNT"
}

stage_infra() {
  say "APIs"
  g services enable run.googleapis.com artifactregistry.googleapis.com secretmanager.googleapis.com \
    iam.googleapis.com iamcredentials.googleapis.com sts.googleapis.com cloudbuild.googleapis.com \
    pubsub.googleapis.com monitoring.googleapis.com billingbudgets.googleapis.com \
    storage.googleapis.com cloudresourcemanager.googleapis.com serviceusage.googleapis.com

  say "Image registry (free tier: 0.5 GB, so keep only the two newest images of each)"
  g artifacts repositories describe "$REGISTRY" --location="$REGION" >/dev/null 2>&1 \
    || g artifacts repositories create "$REGISTRY" --repository-format=docker --location="$REGION" \
         --description="travellermap site and alert responder images"
  mkdir -p "$GENERATED"
  cat > "$GENERATED/cleanup-policy.json" <<'JSON'
[
  { "name": "keep-newest-two", "action": { "type": "Keep" }, "mostRecentVersions": { "keepCount": 2 } },
  { "name": "delete-the-rest", "action": { "type": "Delete" }, "condition": { "tagState": "any" } }
]
JSON
  g artifacts repositories set-cleanup-policies "$REGISTRY" --location="$REGION" \
    --policy="$GENERATED/cleanup-policy.json" --no-dry-run

  say "Alert state bucket"
  g storage buckets describe "gs://$STATE_BUCKET" >/dev/null 2>&1 \
    || g storage buckets create "gs://$STATE_BUCKET" --location="$REGION" --uniform-bucket-level-access

  say "Service accounts"
  ensure_sa gh-deployer "GitHub Actions deployer"
  ensure_sa site-runtime "travellermap site runtime"
  ensure_sa cost-alerts "Cost alert responder"
  ensure_sa alerts-pusher "Pub/Sub push to alert responder"

  # Deployer: create revisions, push images, act as the runtime account. Nothing else.
  grant gh-deployer roles/run.developer
  grant gh-deployer roles/serviceusage.serviceUsageConsumer
  g artifacts repositories add-iam-policy-binding "$REGISTRY" --location="$REGION" \
    --member="serviceAccount:$(sa gh-deployer)" --role=roles/artifactregistry.writer --quiet >/dev/null
  g iam service-accounts add-iam-policy-binding "$(sa site-runtime)" \
    --member="serviceAccount:$(sa gh-deployer)" --role=roles/iam.serviceAccountUser --quiet >/dev/null

  # Alert responder: read its bucket marker files.
  g storage buckets add-iam-policy-binding "gs://$STATE_BUCKET" \
    --member="serviceAccount:$(sa cost-alerts)" --role=roles/storage.objectUser --quiet >/dev/null

  # `gcloud builds submit` (used for the responder image) runs as the default compute account.
  local compute="$(project_number)-compute@developer.gserviceaccount.com"
  g projects add-iam-policy-binding "$PROJECT_ID" --member="serviceAccount:$compute" \
    --role=roles/cloudbuild.builds.builder --condition=None --quiet >/dev/null
}

stage_wif() {
  say "Workload Identity Federation for GitHub Actions ($GITHUB_REPO, main branch only)"
  g iam workload-identity-pools describe github --location=global >/dev/null 2>&1 \
    || g iam workload-identity-pools create github --location=global --display-name="GitHub Actions"
  g iam workload-identity-pools providers describe github --location=global --workload-identity-pool=github >/dev/null 2>&1 \
    || g iam workload-identity-pools providers create-oidc github --location=global --workload-identity-pool=github \
         --issuer-uri="https://token.actions.githubusercontent.com" \
         --attribute-mapping="google.subject=assertion.sub,attribute.repository=assertion.repository,attribute.ref=assertion.ref" \
         --attribute-condition="assertion.repository=='${GITHUB_REPO}' && assertion.ref=='refs/heads/main'"
  g iam service-accounts add-iam-policy-binding "$(sa gh-deployer)" --role=roles/iam.workloadIdentityUser \
    --member="principalSet://iam.googleapis.com/projects/$(project_number)/locations/global/workloadIdentityPools/github/attribute.repository/${GITHUB_REPO}" \
    --quiet >/dev/null
  echo "Provider: $(wif_provider)"
}

wif_provider() {
  echo "projects/$(project_number)/locations/global/workloadIdentityPools/github/providers/github"
}

stage_github() {
  say "GitHub repository variables ($GITHUB_REPO)"
  gh api --method PUT "repos/${GITHUB_REPO}/environments/production" >/dev/null
  local pair
  for pair in "GCP_PROJECT_ID=$PROJECT_ID" "GCP_REGION=$REGION" "GCP_SERVICE=$SERVICE" \
              "GCP_REGISTRY=$REGISTRY" "GCP_WIF_PROVIDER=$(wif_provider)" \
              "GCP_DEPLOYER_SA=$(sa gh-deployer)" "GCP_RUNTIME_SA=$(sa site-runtime)"; do
    gh variable set "${pair%%=*}" --body "${pair#*=}" --repo "$GITHUB_REPO"
  done
  echo "Done. Deploys run once GCP_PROJECT_ID is set; to pause them, delete that variable."
}

put_secret() { # put_secret NAME VALUE
  if g secrets describe "$1" >/dev/null 2>&1; then
    printf %s "$2" | g secrets versions add "$1" --data-file=- >/dev/null
  else
    printf %s "$2" | g secrets create "$1" --replication-policy=automatic --data-file=- >/dev/null
  fi
}

stage_secrets() {
  say "Secrets. Input is hidden; nothing is written to disk or shown (except a generated admin key)."
  local admin token user
  read -rsp "AdminKey for /admin pages (blank = generate one and show it): " admin; echo
  if [ -z "$admin" ]; then
    admin="$(node -e "console.log(require('crypto').randomBytes(18).toString('hex'))")"
    echo "Generated AdminKey (save it now): $admin"
  fi
  read -rsp "Pushover application API token: " token; echo
  read -rsp "Pushover user key (or delivery group key): " user; echo

  say "Checking the Pushover credentials (you should get a normal-priority message)"
  local reply
  reply="$(curl -sS https://api.pushover.net/1/messages.json \
    --data-urlencode "token=$token" --data-urlencode "user=$user" \
    --data-urlencode "title=travellermap alerts" --data-urlencode "message=Pushover is configured.")"
  case "$reply" in *'"status":1'*) echo "Pushover accepted it." ;; *) echo "Pushover said: $reply" >&2; exit 1 ;; esac

  put_secret admin-key "$admin"
  put_secret pushover-token "$token"
  put_secret pushover-user "$user"
  g secrets add-iam-policy-binding admin-key --member="serviceAccount:$(sa site-runtime)" \
    --role=roles/secretmanager.secretAccessor --quiet >/dev/null
  for name in pushover-token pushover-user; do
    g secrets add-iam-policy-binding "$name" --member="serviceAccount:$(sa cost-alerts)" \
      --role=roles/secretmanager.secretAccessor --quiet >/dev/null
  done
  echo "Stored admin-key, pushover-token, pushover-user."
}

alerts_url() { g run services describe "$ALERTS_SERVICE" --region="$REGION" --format='value(status.url)'; }

stage_alerts() {
  mkdir -p "$GENERATED"
  say "Pub/Sub topic"
  g pubsub topics describe "$TOPIC" >/dev/null 2>&1 || g pubsub topics create "$TOPIC"

  say "Alert responder image and service (private; only Pub/Sub may call it)"
  g builds submit --tag "$(image "$ALERTS_SERVICE")" deploy/alert-function
  g run deploy "$ALERTS_SERVICE" --image="$(image "$ALERTS_SERVICE")" --region="$REGION" \
    --service-account="$(sa cost-alerts)" --no-allow-unauthenticated \
    --min-instances=0 --max-instances=1 --cpu=1 --memory=256Mi --concurrency=10 --timeout=60 \
    --set-env-vars="PROJECT_ID=${PROJECT_ID},REGION=${REGION},SERVICE=${SERVICE},STATE_BUCKET=${STATE_BUCKET}" \
    --set-secrets="PUSHOVER_TOKEN=pushover-token:latest,PUSHOVER_USER=pushover-user:latest"
  g run services add-iam-policy-binding "$ALERTS_SERVICE" --region="$REGION" \
    --member="serviceAccount:$(sa alerts-pusher)" --role=roles/run.invoker --quiet >/dev/null

  say "Pub/Sub push subscription (authenticated)"
  g iam service-accounts add-iam-policy-binding "$(sa alerts-pusher)" \
    --member="serviceAccount:service-$(project_number)@gcp-sa-pubsub.iam.gserviceaccount.com" \
    --role=roles/iam.serviceAccountTokenCreator --quiet >/dev/null
  local url; url="$(alerts_url)"
  if g pubsub subscriptions describe "${TOPIC}-push" >/dev/null 2>&1; then
    g pubsub subscriptions modify-push-config "${TOPIC}-push" --push-endpoint="$url" \
      --push-auth-service-account="$(sa alerts-pusher)" --push-auth-token-audience="$url"
  else
    g pubsub subscriptions create "${TOPIC}-push" --topic="$TOPIC" --push-endpoint="$url" \
      --push-auth-service-account="$(sa alerts-pusher)" --push-auth-token-audience="$url" \
      --ack-deadline=60 --min-retry-delay=30s --max-retry-delay=600s --expiration-period=never
  fi

  say "Cloud Monitoring: notification channel"
  local channel
  channel="$(api GET "https://monitoring.googleapis.com/v3/projects/${PROJECT_ID}/notificationChannels" \
    | node -e "const r=JSON.parse(require('fs').readFileSync(0,'utf8'));const c=(r.notificationChannels||[]).find(c=>c.displayName==='${TOPIC}');console.log(c?c.name:'')")"
  if [ -z "$channel" ]; then
    printf '{"type":"pubsub","displayName":"%s","labels":{"topic":"projects/%s/topics/%s"}}' \
      "$TOPIC" "$PROJECT_ID" "$TOPIC" > "$GENERATED/channel.json"
    channel="$(api POST "https://monitoring.googleapis.com/v3/projects/${PROJECT_ID}/notificationChannels" "$GENERATED/channel.json" \
      | node -e "const r=JSON.parse(require('fs').readFileSync(0,'utf8'));if(!r.name){console.error(JSON.stringify(r));process.exit(1)}console.log(r.name)")"
  fi
  echo "Channel: $channel"
  g pubsub topics add-iam-policy-binding "$TOPIC" \
    --member="serviceAccount:service-$(project_number)@gcp-sa-monitoring-notification.iam.gserviceaccount.com" \
    --role=roles/pubsub.publisher --quiet >/dev/null

  say "Cloud Monitoring: alert policies"
  local file name
  for file in deploy/monitoring/*.json; do
    name="$(node -pe "JSON.parse(require('fs').readFileSync('$file','utf8')).displayName")"
    if [ -n "$(g monitoring policies list --filter="displayName=\"$name\"" --format='value(name)')" ]; then
      echo "exists: $name (delete it in the console to recreate with new thresholds)"
      continue
    fi
    sed "s|__SERVICE__|${SERVICE}|g; s|__CHANNEL__|${channel}|g" "$file" > "$GENERATED/$(basename "$file")"
    g monitoring policies create --policy-from-file="$GENERATED/$(basename "$file")" >/dev/null
    echo "created: $name"
  done
}

stage_budget() {
  : "${BILLING_ACCOUNT:?set BILLING_ACCOUNT (gcloud billing accounts list)}"
  say "Budget: ${BUDGET_USD} USD per month for project $PROJECT_ID"
  gcloud billing budgets create --billing-project="$PROJECT_ID" --billing-account="$BILLING_ACCOUNT" --display-name="$SERVICE" \
    --budget-amount="${BUDGET_USD}USD" --calendar-period=month \
    --filter-projects="projects/$(project_number)" \
    --threshold-rule=percent=0.25 --threshold-rule=percent=0.5 --threshold-rule=percent=0.9 \
    --threshold-rule=percent=1.0 --threshold-rule=percent=1.0,basis=forecasted-spend \
    --notifications-rule-pubsub-topic="projects/${PROJECT_ID}/topics/${TOPIC}"
}

stage_live() {
  say "Make the site public, and let the responder cut it off"
  stage_restore
  g run services add-iam-policy-binding "$SERVICE" --region="$REGION" \
    --member="serviceAccount:$(sa cost-alerts)" --role=roles/run.admin --quiet >/dev/null
  echo "Site: $(g run services describe "$SERVICE" --region="$REGION" --format='value(status.url)')"
}

stage_status_access() {
  say "Read-only access for the site's service account: metrics and alerts, its own access policy, the alert state files"
  grant site-runtime roles/monitoring.viewer
  # Whether the cost cutoff has made the site private (reads the IAM policy of this one service).
  g run services add-iam-policy-binding "$SERVICE" --region="$REGION" \
    --member="serviceAccount:$(sa site-runtime)" --role=roles/run.viewer --quiet >/dev/null
  # The responder's latest-budget.json, latest-alert.json and announced/ marker files.
  g storage buckets add-iam-policy-binding "gs://$STATE_BUCKET" \
    --member="serviceAccount:$(sa site-runtime)" --role=roles/storage.objectViewer --quiet >/dev/null
}

stage_restore() {
  g run services add-iam-policy-binding "$SERVICE" --region="$REGION" \
    --member=allUsers --role=roles/run.invoker --quiet >/dev/null
}

stage_cutoff() {
  g run services remove-iam-policy-binding "$SERVICE" --region="$REGION" \
    --member=allUsers --role=roles/run.invoker --quiet >/dev/null
}

stage_drill() { # drill budget|monitoring [level]
  mkdir -p "$GENERATED"
  local kind="${1:?drill budget|monitoring [level]}" level="${2:-}" json
  case "$kind" in
    budget)
      json="{\"drill\":true,\"budgetDisplayName\":\"$SERVICE\",\"costAmount\":1.0,\"budgetAmount\":${BUDGET_USD}.0,\"currencyCode\":\"USD\",\"costIntervalStart\":\"2000-01-01T00:00:00Z\",\"alertThresholdExceeded\":${level:-0.5}}" ;;
    monitoring)
      json="{\"drill\":true,\"incident\":{\"incident_id\":\"drill\",\"state\":\"open\",\"policy_name\":\"drill\",\"summary\":\"Test of the critical alert path.\",\"policy_user_labels\":{\"severity\":\"${level:-critical}\"}}}" ;;
    *) echo "drill budget|monitoring" >&2; exit 2 ;;
  esac
  printf '{"messages":[{"data":"%s"}]}' "$(printf %s "$json" | node -e "process.stdout.write(require('fs').readFileSync(0).toString('base64'))")" \
    > "$GENERATED/drill.json"
  api POST "https://pubsub.googleapis.com/v1/projects/${PROJECT_ID}/topics/${TOPIC}:publish" "$GENERATED/drill.json"
  echo
}

[ $# -gt 0 ] || { sed -n '2,22p' "$0"; exit 2; }
if [ "$1" = drill ]; then
  shift
  stage_drill "$@"
  exit
fi
for stage in "$@"; do
  case "$stage" in
    project|infra|wif|github|secrets|alerts|budget|live|restore|cutoff|status-access) "stage_${stage//-/_}" ;;
    *) echo "unknown stage: $stage (drill must be used on its own)" >&2; exit 2 ;;
  esac
done
