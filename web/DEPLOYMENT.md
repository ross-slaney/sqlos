# Web Deployment

The `/web` marketing app deploys to Azure Container Apps through [`../.github/workflows/deploy-web.yml`](../.github/workflows/deploy-web.yml).

## GitHub setup checklist

Add these in GitHub at:

`Repository -> Settings -> Secrets and variables -> Actions`

- Put the values in the `Variables` tab unless the table says `Secret`.
- Put `AZURE_CLIENT_SECRET` in the `Secrets` tab.
- Do not wrap values in quotes.

### Required GitHub Actions values

| Name | Type | Example | What it is | How to get it |
| --- | --- | --- | --- | --- |
| `AZURE_LOCATION` | Variable | `eastus2` | Azure region for the app resource group, ACR, Log Analytics, and Container Apps environment. | `az account list-locations -o table` |
| `AZURE_RESOURCE_GROUP` | Variable | `rg-sqlos-web-prod` | Resource group that will contain the deployed app infrastructure. | Choose the name you want to use. The workflow creates or updates it. |
| `AZURE_DNS_RESOURCE_GROUP` | Variable | `foundation` | Resource group that already contains the Azure DNS zone. | Use the existing resource group that holds `sqlos.dev`. |
| `AZURE_DNS_ZONE_NAME` | Variable | `sqlos.dev` | Azure DNS zone name to update during deploy. | Use your existing zone name. |
| `AZURE_NAME_PREFIX` | Variable | `sqlos` | Short naming prefix used to generate Azure resource names. | Choose a short lowercase prefix. Keep it conservative: letters, numbers, and hyphens only. |
| `AZURE_CLIENT_ID` | Variable | `11111111-1111-1111-1111-111111111111` | Application (client) ID of the Azure service principal used by GitHub Actions. | From `az ad sp create-for-rbac` output, or `az ad sp show --id <appId> --query appId -o tsv`. |
| `AZURE_TENANT_ID` | Variable | `22222222-2222-2222-2222-222222222222` | Azure Entra tenant ID that owns the subscription and service principal. | `az account show --query tenantId -o tsv` |
| `AZURE_SUBSCRIPTION_ID` | Variable | `33333333-3333-3333-3333-333333333333` | Azure subscription ID where both the app RG and DNS zone live. | `az account show --query id -o tsv` |
| `AZURE_CLIENT_SECRET` | Secret | `...` | Client secret for the Azure service principal used by GitHub Actions. | From `az ad sp create-for-rbac` output, or by creating a new app credential. |

### Recommended values for this repo

Set these explicitly even though the workflow has defaults for two of them:

```text
AZURE_DNS_RESOURCE_GROUP=foundation
AZURE_DNS_ZONE_NAME=sqlos.dev
```

## What the service principal must be allowed to do

The GitHub Actions service principal needs permission to:

- create or update the application resource group named by `AZURE_RESOURCE_GROUP`
- create Azure resources inside that resource group
- update DNS records in the existing DNS resource group that contains `sqlos.dev`

The simplest option is `Contributor` on the subscription.

If you want tighter scope, pre-create the app resource group yourself and then grant the principal `Contributor` on:

- the app resource group
- the DNS resource group

If you do not grant subscription-level access, remember that the workflow currently runs `az group create`, so a purely resource-group-scoped principal may not be able to create a brand new resource group from GitHub.

## Create the Azure service principal

### Option 1: Create a new one

Log in and select the correct subscription:

```bash
az login
az account set --subscription "<subscription-name-or-id>"
```

Get the subscription and tenant values for GitHub:

```bash
az account show --query "{subscriptionId:id,tenantId:tenantId}" -o json
```

Create a service principal with subscription-wide `Contributor` access:

```bash
az ad sp create-for-rbac \
  --name "github-sqlos-web-deploy" \
  --role Contributor \
  --scopes "/subscriptions/<subscription-id>" \
  --query "{clientId:appId, clientSecret:password, tenantId:tenant}" \
  -o json
```

Use the returned values like this:

- `clientId` -> `AZURE_CLIENT_ID`
- `clientSecret` -> `AZURE_CLIENT_SECRET`
- `tenantId` -> `AZURE_TENANT_ID`
- your chosen subscription ID -> `AZURE_SUBSCRIPTION_ID`

### Option 2: Reuse an existing service principal

If you already have a service principal, fetch the client ID:

```bash
az ad sp show --id "<existing-app-id-or-object-id>" --query appId -o tsv
```

If you do not have a current secret value, create a new one:

```bash
az ad app credential reset \
  --id "<existing-app-id>" \
  --append \
  --query password \
  -o tsv
```

Important: Azure does not let you read back an old secret value later. If you lost it, you must create a new one and update GitHub with the new `AZURE_CLIENT_SECRET`.

## How to obtain the non-secret values

### `AZURE_LOCATION`

Pick one of your supported Azure regions:

```bash
az account list-locations -o table
```

### `AZURE_RESOURCE_GROUP`

Choose the app resource group name yourself. Example:

```text
rg-sqlos-web-prod
```

### `AZURE_DNS_RESOURCE_GROUP`

Find the resource group that contains the DNS zone:

```bash
az network dns zone list --query "[].{name:name,resourceGroup:resourceGroup}" -o table
```

For your current setup this should be:

```text
foundation
```

### `AZURE_DNS_ZONE_NAME`

This repo expects:

```text
sqlos.dev
```

You can confirm it exists with:

```bash
az network dns zone show \
  --resource-group foundation \
  --name sqlos.dev \
  -o table
```

### `AZURE_NAME_PREFIX`

This is a naming seed used by Bicep.

Rules:

- keep it short
- keep it lowercase
- use only letters, numbers, and hyphens
- avoid long values, because Azure resource names have length limits

Recommended value for this repo:

```text
sqlos
```

## GitHub values to add

### Variables

```text
AZURE_LOCATION=<example: eastus2>
AZURE_RESOURCE_GROUP=<example: rg-sqlos-web-prod>
AZURE_DNS_RESOURCE_GROUP=foundation
AZURE_DNS_ZONE_NAME=sqlos.dev
AZURE_NAME_PREFIX=sqlos
AZURE_CLIENT_ID=<service-principal-app-id>
AZURE_TENANT_ID=<tenant-id>
AZURE_SUBSCRIPTION_ID=<subscription-id>
```

### Secret

```text
AZURE_CLIENT_SECRET=<service-principal-secret>
```

## Azure resources

The workflow creates or updates:

- a Basic Azure Container Registry
- a Log Analytics workspace
- an Azure Container Apps environment
- a user-assigned managed identity with `AcrPull`
- a single public Azure Container App for the `/web` site
- DNS records in the existing `sqlos.dev` zone:
  - `A @`
  - `TXT asuid`
  - `CNAME www`
  - `TXT asuid.www`

## Prerequisites

- `sqlos.dev` must already exist as an Azure DNS zone in the configured DNS resource group.
- The public registrar must delegate `sqlos.dev` to Azure DNS before custom-domain validation can succeed.
- The app and DNS zone are assumed to live in the same Azure subscription.

## First deployment checklist

1. Add all GitHub variables and the `AZURE_CLIENT_SECRET` secret.
2. Confirm the service principal has access to both the app RG scope and the DNS RG scope.
3. Confirm `sqlos.dev` is delegated to Azure DNS publicly.
4. Push to `main`.
5. Watch the `Deploy Web to Azure Container Apps` workflow in GitHub Actions.

## Manual recovery commands

```bash
# Re-run the Bicep layers
az deployment group create \
  --resource-group <app-resource-group> \
  --name foundation \
  --template-file infra/layers/foundation.bicep \
  --parameters location=<azure-region> projectPrefix=<name-prefix>

az deployment group create \
  --resource-group <app-resource-group> \
  --name applications \
  --template-file infra/layers/applications.bicep \
  --parameters \
    location=<azure-region> \
    projectPrefix=<name-prefix> \
    containerAppsEnvId=<container-apps-env-id> \
    containerAppsEnvStaticIp=<environment-static-ip> \
    acrLoginServer=<acr-login-server> \
    containerImage=<acr-login-server>/sqlos-web:<tag> \
    uamiId=<uami-resource-id> \
    dnsResourceGroup=<dns-resource-group> \
    dnsZoneName=sqlos.dev

# Inspect the deployed app
az containerapp show \
  --resource-group <app-resource-group> \
  --name <container-app-name>

# Rebind custom domains after DNS changes
az containerapp hostname bind \
  --resource-group <app-resource-group> \
  --name <container-app-name> \
  --environment <container-apps-env-id> \
  --hostname sqlos.dev \
  --validation-method HTTP

az containerapp hostname bind \
  --resource-group <app-resource-group> \
  --name <container-app-name> \
  --environment <container-apps-env-id> \
  --hostname www.sqlos.dev \
  --validation-method CNAME
```
