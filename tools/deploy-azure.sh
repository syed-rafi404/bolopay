#!/usr/bin/env bash
#
# Deploys BoloPay to Azure Container Apps from a prebuilt GHCR image.
#
# Run in Azure Cloud Shell (https://shell.azure.com), which is already
# authenticated.
#
#   bash deploy-azure.sh gsk_yourgroqkey
#
# The image is built by GitHub Actions rather than ACR Tasks, because Tasks are
# not permitted on Student subscriptions (TasksOperationsNotAllowed) and Cloud
# Shell has no Docker daemon. Container Apps pulls the public GHCR image
# directly, so no registry credentials are needed.
#
# Prerequisite: the "Build and publish container image" workflow must have run
# successfully, and the resulting package must be public. See README.

set -euo pipefail

GROQ_KEY="${1:-${GROQ_KEY:-}}"

RG="bolopay-rg"
ENV_NAME="bolopay-env"
APP="bolopay-demo"
IMAGE="${IMAGE:-ghcr.io/syed-rafi404/bolopay:latest}"

# Student subscriptions restrict regions by policy, and the allowed set is not
# discoverable up front — az account list-locations reports geography, not
# policy. Candidates are ordered by latency from Bangladesh.
REGION_CANDIDATES="${LOC:-centralindia southindia japaneast koreacentral australiaeast uaenorth eastus eastus2 westus2 westeurope northeurope uksouth}"

case "$GROQ_KEY" in
  gsk_*) ;;
  "")
    echo "ERROR: no Groq key supplied." >&2
    echo "Usage: bash deploy-azure.sh gsk_yourkeyhere" >&2
    exit 1
    ;;
  *)
    echo "ERROR: that does not look like a Groq key (should start with gsk_)." >&2
    exit 1
    ;;
esac

echo "==> Registering resource providers (no-op if already registered)"
az provider register --namespace Microsoft.App --wait
az provider register --namespace Microsoft.OperationalInsights --wait

# A resource group's location is only metadata — resources inside it may live
# in any region — so an existing group is reused as-is. Recreating it elsewhere
# fails with InvalidResourceGroupLocation.
if az group show -n "$RG" -o none 2>/dev/null; then
  echo "==> Reusing resource group $RG ($(az group show -n "$RG" --query location -o tsv))"
else
  echo "==> Creating resource group $RG"
  az group create -n "$RG" -l eastus -o none 2>/dev/null \
    || az group create -n "$RG" -l centralindia -o none
fi

# Reuse an environment from a previous run; otherwise probe for a permitted region.
LOC="$(az containerapp env show -n "$ENV_NAME" -g "$RG" --query location -o tsv 2>/dev/null || true)"

if [ -n "$LOC" ]; then
  echo "==> Reusing Container Apps environment $ENV_NAME in $LOC"
else
  echo "==> Creating Container Apps environment (probing for an allowed region)"
  for r in $REGION_CANDIDATES; do
    printf '    %-16s ' "$r"
    if az containerapp env create -n "$ENV_NAME" -g "$RG" -l "$r" -o none 2>/dev/null; then
      echo "allowed"
      LOC="$r"
      break
    fi
    echo "blocked"
  done

  if [ -z "$LOC" ]; then
    echo >&2
    echo "ERROR: no candidate region was permitted for this subscription." >&2
    echo "Find an allowed region in the portal, then re-run as:" >&2
    echo "  LOC=\"<region>\" bash deploy-azure.sh $GROQ_KEY" >&2
    exit 1
  fi
fi

echo "==> Deploying $APP from $IMAGE"

if az containerapp show -n "$APP" -g "$RG" -o none 2>/dev/null; then
  # Update in place so re-runs are idempotent rather than erroring on conflict.
  az containerapp secret set -n "$APP" -g "$RG" --secrets "groq-key=$GROQ_KEY" -o none
  az containerapp update -n "$APP" -g "$RG" --image "$IMAGE" -o none
else
  az containerapp create \
    -n "$APP" \
    -g "$RG" \
    --environment "$ENV_NAME" \
    --image "$IMAGE" \
    --target-port 8080 \
    --ingress external \
    --min-replicas 0 \
    --max-replicas 1 \
    --cpu 0.5 \
    --memory 1.0Gi \
    --secrets "groq-key=$GROQ_KEY" \
    --env-vars \
        "Groq__ApiKey=secretref:groq-key" \
        "ASPNETCORE_ENVIRONMENT=Production" \
        "RateLimit__VoicePermitsPerHour=20" \
    -o none
fi

FQDN="$(az containerapp show -n "$APP" -g "$RG" --query properties.configuration.ingress.fqdn -o tsv)"

echo
echo "============================================================"
echo "  Region:   $LOC"
echo "  App:      https://$FQDN"
echo "  Health:   https://$FQDN/healthz"
echo "============================================================"
echo
echo "Waiting for the first container start..."

for attempt in 1 2 3 4 5 6; do
  sleep 15
  BODY="$(curl -fsS --max-time 20 "https://$FQDN/healthz" 2>/dev/null || true)"
  if [ -n "$BODY" ]; then
    echo "$BODY"
    echo
    case "$BODY" in
      *'"transcription":"groq"'*)
        echo "OK — the Groq key reached the container."
        ;;
      *'"transcription":"stub"'*)
        echo "WARNING: running in stub mode; the Groq key did not reach the container."
        ;;
    esac
    exit 0
  fi
  echo "    attempt $attempt: not ready yet"
done

echo
echo "Health check did not respond yet. Inspect with:"
echo "  az containerapp logs show -n $APP -g $RG --tail 50"
