#!/usr/bin/env bash
#
# Deploys BoloPay to Azure Container Apps.
#
# Intended for Azure Cloud Shell (https://shell.azure.com), which is already
# authenticated, so there is no az login step. The image is built by ACR Tasks
# directly from the public GitHub repo, so no local Docker is required.
#
# Usage: set GROQ_KEY below, then paste this whole script into Cloud Shell (bash).

set -euo pipefail

# ---------------------------------------------------------------------------
# Settings
# ---------------------------------------------------------------------------
# Pass the key as the first argument, or export GROQ_KEY beforehand:
#   bash deploy-azure.sh gsk_yourkey
# Taking it as an argument avoids editing this file, which previously broke
# because sed also rewrote the guard below and made the check compare the key
# against itself.
GROQ_KEY="${1:-${GROQ_KEY:-}}"

RG="bolopay-rg"
LOC="southeastasia"          # closest region to Bangladesh; see fallback below
ENV_NAME="bolopay-env"
APP="bolopay-demo"
REPO="https://github.com/syed-rafi404/bolopay.git"
IMAGE="bolopay:v1"

# Container registry names must be globally unique and alphanumeric only.
ACR="bolopayacr$RANDOM$RANDOM"

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

echo "==> Creating resource group $RG in $LOC"
# Student subscriptions sometimes restrict regions. If this fails, change LOC
# to one from: az account list-locations -o table
az group create -n "$RG" -l "$LOC" -o none

echo "==> Creating container registry $ACR"
az acr create -n "$ACR" -g "$RG" --sku Basic --admin-enabled true -o none

echo "==> Building image from $REPO (runs in Azure, not locally)"
az acr build --registry "$ACR" --image "$IMAGE" "$REPO" -o none

echo "==> Creating Container Apps environment $ENV_NAME"
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
echo "  Deployed:  https://$FQDN"
echo "  Health:    https://$FQDN/healthz"
echo "============================================================"
echo
echo "Checking health endpoint (allow a moment for first start)..."
sleep 20
curl -s "https://$FQDN/healthz" || echo "(not ready yet — retry in a minute)"
echo
echo
echo 'Expect {"status":"ok","transcription":"groq"}.'
echo 'If it says "stub", the Groq key did not reach the app.'
