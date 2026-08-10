#!/usr/bin/env bash
#
# Deploys BoloPay to Azure Container Apps.
#
# Intended for Azure Cloud Shell (https://shell.azure.com), which is already
# authenticated, so there is no az login step. The image is built by ACR Tasks
# directly from the public GitHub repo, so no local Docker is required.
#
# Usage: bash deploy-azure.sh gsk_yourgroqkey
#
# Student and free subscriptions are region-restricted by an Azure policy, and
# the allowed set is not discoverable up front — az account list-locations
# reports geography, not policy. So the region is found by attempting the
# registry creation across candidates until one is permitted.

set -euo pipefail

# ---------------------------------------------------------------------------
# Settings
# ---------------------------------------------------------------------------
GROQ_KEY="${1:-${GROQ_KEY:-}}"

RG="bolopay-rg"
ENV_NAME="bolopay-env"
APP="bolopay-demo"
REPO="https://github.com/syed-rafi404/bolopay.git"
IMAGE="bolopay:v1"

# Ordered by latency from Bangladesh, then by how commonly the region is
# permitted on restricted subscriptions.
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
az provider register --namespace Microsoft.ContainerRegistry --wait

# A resource group's location is only metadata — the resources inside it may
# live in any region — so an existing group from a previous run is reused as-is.
# Trying to recreate it elsewhere fails with InvalidResourceGroupLocation.
if az group show -n "$RG" -o none 2>/dev/null; then
  echo "==> Reusing existing resource group $RG ($(az group show -n "$RG" --query location -o tsv))"
else
  echo "==> Creating resource group $RG"
  az group create -n "$RG" -l eastus -o none 2>/dev/null \
    || az group create -n "$RG" -l centralindia -o none
fi

# Reuse a registry from a previous partial run rather than orphaning one.
ACR="$(az acr list -g "$RG" --query "[0].name" -o tsv 2>/dev/null || true)"

if [ -n "$ACR" ]; then
  LOC="$(az acr show -n "$ACR" -g "$RG" --query location -o tsv)"
  echo "==> Reusing existing registry $ACR in $LOC"
else
  ACR="bolopayacr$RANDOM$RANDOM"
  LOC=""

  echo "==> Finding a region this subscription allows"
  for r in $REGION_CANDIDATES; do
    printf '    %-16s ' "$r"
    if az acr create -n "$ACR" -g "$RG" -l "$r" --sku Basic --admin-enabled true -o none 2>/dev/null; then
      echo "allowed"
      LOC="$r"
      break
    fi
    echo "blocked"
  done

  if [ -z "$LOC" ]; then
    echo >&2
    echo "ERROR: no candidate region was permitted for this subscription." >&2
    echo "Check the portal for your allowed regions, then re-run as:" >&2
    echo "  LOC=\"<region>\" bash deploy-azure.sh $GROQ_KEY" >&2
    exit 1
  fi
fi

echo "==> Building image from $REPO (builds in Azure, not locally)"
az acr build --registry "$ACR" --image "$IMAGE" "$REPO" -o none

echo "==> Creating Container Apps environment $ENV_NAME in $LOC"
az containerapp env create -n "$ENV_NAME" -g "$RG" -l "$LOC" -o none

echo "==> Deploying container app $APP"
ACR_SERVER="$(az acr show -n "$ACR" -g "$RG" --query loginServer -o tsv)"
ACR_USER="$(az acr credential show -n "$ACR" -g "$RG" --query username -o tsv)"
ACR_PASS="$(az acr credential show -n "$ACR" -g "$RG" --query 'passwords[0].value' -o tsv)"

az containerapp create \
  -n "$APP" \
  -g "$RG" \
  --environment "$ENV_NAME" \
  --image "$ACR_SERVER/$IMAGE" \
  --registry-server "$ACR_SERVER" \
  --registry-username "$ACR_USER" \
  --registry-password "$ACR_PASS" \
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

FQDN="$(az containerapp show -n "$APP" -g "$RG" --query properties.configuration.ingress.fqdn -o tsv)"

echo
echo "============================================================"
echo "  Region:    $LOC"
echo "  Deployed:  https://$FQDN"
echo "  Health:    https://$FQDN/healthz"
echo "============================================================"
echo
echo "Checking health endpoint (first start can take a moment)..."
sleep 25
curl -s "https://$FQDN/healthz" || echo "(not ready yet — retry in a minute)"
echo
echo
echo 'Expect {"status":"ok","transcription":"groq"}.'
echo 'If it reports "stub", the Groq key did not reach the container.'
