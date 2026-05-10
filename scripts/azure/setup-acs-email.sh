#!/usr/bin/env bash
set -euo pipefail

# Provision Azure Communication Services Email for SqlOS email OTP.
#
# Required environment variables:
#   AZURE_SUBSCRIPTION_ID
#   AZURE_RESOURCE_GROUP
#   AZURE_DNS_ZONE_NAME
#   ACS_EMAIL_DOMAIN
#   ACS_EMAIL_SENDER_USERNAME
#   ACS_EMAIL_SENDER_DISPLAY_NAME
#
# Optional environment variables:
#   AZURE_DNS_ZONE_RESOURCE_GROUP       Defaults to AZURE_RESOURCE_GROUP
#   AZURE_SP_APP_ID / AZURE_SP_PASSWORD / AZURE_SP_TENANT_ID
#   ACS_PROJECT_PREFIX                  Defaults to ACS_EMAIL_DOMAIN with dots replaced
#   ACS_EMAIL_SERVICE_NAME              Defaults to "${ACS_PROJECT_PREFIX}-email"
#   ACS_COMMUNICATION_SERVICE_NAME      Defaults to "${ACS_PROJECT_PREFIX}-comm"
#   ACS_DATA_LOCATION                   Defaults to "United States"
#   ACS_DNS_TTL_SECONDS                 Defaults to 300
#
# Usage:
#   AZURE_SUBSCRIPTION_ID=... AZURE_RESOURCE_GROUP=... \
#   AZURE_DNS_ZONE_NAME=example.com ACS_EMAIL_DOMAIN=example.com \
#   ACS_EMAIL_SENDER_USERNAME=no-reply ACS_EMAIL_SENDER_DISPLAY_NAME="Example" \
#   ./scripts/azure/setup-acs-email.sh --apply-dns --yes

ACS_COMM_API_VERSION="${ACS_COMM_API_VERSION:-2023-03-31}"
ACS_EMAIL_API_VERSION="${ACS_EMAIL_API_VERSION:-2025-01-25-preview}"

APPLY_DNS=false
DRY_RUN=false
FORCE_DKIM=false
PRINT_CONNECTION_STRING=false
YES=false

usage() {
  cat <<'EOF'
Usage: scripts/azure/setup-acs-email.sh [options]

Options:
  --apply-dns                 Create Azure DNS records in AZURE_DNS_ZONE_NAME.
  --force-dkim                Replace existing DKIM CNAME records when applying DNS.
  --print-connection-string   Print the ACS connection string at the end.
  --dry-run                   Print actions without mutating Azure resources.
  --yes                       Skip confirmation prompts.
  -h, --help                  Show this help.

The script creates:
  - ACS Email Service
  - ACS Communication Service
  - Customer-managed email domain
  - Optional Azure DNS verification records
  - Sender username
  - Domain link from Communication Service to Email domain

EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    --apply-dns) APPLY_DNS=true ;;
    --force-dkim) FORCE_DKIM=true ;;
    --print-connection-string) PRINT_CONNECTION_STRING=true ;;
    --dry-run) DRY_RUN=true ;;
    --yes) YES=true ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage; exit 1 ;;
  esac
  shift
done

info() { printf '[info] %s\n' "$*" >&2; }
warn() { printf '[warn] %s\n' "$*" >&2; }
fail() { printf '[error] %s\n' "$*" >&2; exit 1; }

run() {
  if [ "$DRY_RUN" = true ]; then
    printf '[dry-run]'
    printf ' %q' "$@"
    printf '\n'
  else
    "$@"
  fi
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || fail "$1 is required but was not found."
}

require_env() {
  local name="$1"
  [ -n "${!name:-}" ] || fail "$name is required."
}

json_body() {
  jq -n "$@"
}

relative_record_name() {
  local record_name="$1"
  local zone="$2"
  record_name="${record_name%.}"
  zone="${zone%.}"

  if [ "$record_name" = "$zone" ]; then
    printf '@'
    return
  fi

  if [[ "$record_name" == *".$zone" ]]; then
    printf '%s' "${record_name%.$zone}"
    return
  fi

  printf '%s' "$record_name"
}

confirm() {
  if [ "$YES" = true ] || [ "$DRY_RUN" = true ]; then
    return
  fi

  printf 'Continue and create/update Azure resources? [y/N] '
  read -r answer
  case "$answer" in
    y|Y|yes|YES) ;;
    *) fail "Aborted." ;;
  esac
}

wait_for_state() {
  local label="$1"
  local query_command="$2"
  local max_attempts="${3:-60}"
  local attempt=0
  local status

  while [ "$attempt" -lt "$max_attempts" ]; do
    status=$(eval "$query_command" 2>/dev/null || echo "NotFound")
    if [ "$status" = "Succeeded" ]; then
      info "$label is ready."
      return
    fi
    if [ "$status" = "Failed" ]; then
      fail "$label provisioning failed."
    fi
    attempt=$((attempt + 1))
    info "$label provisioning state: $status ($attempt/$max_attempts)"
    sleep 5
  done

  fail "Timed out waiting for $label."
}

validate_config() {
  require_command jq
  if [ "$DRY_RUN" != true ]; then
    require_command az
  fi

  require_env AZURE_SUBSCRIPTION_ID
  require_env AZURE_RESOURCE_GROUP
  require_env AZURE_DNS_ZONE_NAME
  require_env ACS_EMAIL_DOMAIN
  require_env ACS_EMAIL_SENDER_USERNAME
  require_env ACS_EMAIL_SENDER_DISPLAY_NAME

  AZURE_DNS_ZONE_RESOURCE_GROUP="${AZURE_DNS_ZONE_RESOURCE_GROUP:-$AZURE_RESOURCE_GROUP}"
  ACS_PROJECT_PREFIX="${ACS_PROJECT_PREFIX:-$(printf '%s' "$ACS_EMAIL_DOMAIN" | tr '.' '-' | cut -c1-20)}"
  ACS_EMAIL_SERVICE_NAME="${ACS_EMAIL_SERVICE_NAME:-${ACS_PROJECT_PREFIX}-email}"
  ACS_COMMUNICATION_SERVICE_NAME="${ACS_COMMUNICATION_SERVICE_NAME:-${ACS_PROJECT_PREFIX}-comm}"
  ACS_DATA_LOCATION="${ACS_DATA_LOCATION:-United States}"
  ACS_DNS_TTL_SECONDS="${ACS_DNS_TTL_SECONDS:-300}"

  info "Subscription:          $AZURE_SUBSCRIPTION_ID"
  info "Resource group:        $AZURE_RESOURCE_GROUP"
  info "DNS zone:              $AZURE_DNS_ZONE_NAME ($AZURE_DNS_ZONE_RESOURCE_GROUP)"
  info "Email domain:          $ACS_EMAIL_DOMAIN"
  info "Sender address:        $ACS_EMAIL_SENDER_USERNAME@$ACS_EMAIL_DOMAIN"
  info "Email service:         $ACS_EMAIL_SERVICE_NAME"
  info "Communication service: $ACS_COMMUNICATION_SERVICE_NAME"

  if [ "$APPLY_DNS" != true ]; then
    warn "DNS records will only be printed. Pass --apply-dns to create them in Azure DNS."
  fi
}

azure_login() {
  if [ "$DRY_RUN" = true ]; then
    info "Dry run: skipping Azure authentication."
    return
  fi

  if [ -n "${AZURE_SP_APP_ID:-}" ] || [ -n "${AZURE_SP_PASSWORD:-}" ] || [ -n "${AZURE_SP_TENANT_ID:-}" ]; then
    require_env AZURE_SP_APP_ID
    require_env AZURE_SP_PASSWORD
    require_env AZURE_SP_TENANT_ID
    info "Logging in with service principal."
    run az login --service-principal \
      -u "$AZURE_SP_APP_ID" \
      -p "$AZURE_SP_PASSWORD" \
      --tenant "$AZURE_SP_TENANT_ID" \
      --output none
  elif ! az account show --output none 2>/dev/null; then
    fail "Azure CLI is not logged in. Run az login or provide service principal env vars."
  fi

  run az account set --subscription "$AZURE_SUBSCRIPTION_ID"
  run az extension add --name communication --upgrade --output none >/dev/null 2>&1 || true
}

create_email_service() {
  info "Ensuring ACS Email Service exists."
  local body
  body=$(json_body \
    --arg location "global" \
    --arg dataLocation "$ACS_DATA_LOCATION" \
    '{ location: $location, properties: { dataLocation: $dataLocation } }')

  run az rest \
    --method PUT \
    --url "https://management.azure.com/subscriptions/$AZURE_SUBSCRIPTION_ID/resourceGroups/$AZURE_RESOURCE_GROUP/providers/Microsoft.Communication/emailServices/$ACS_EMAIL_SERVICE_NAME?api-version=$ACS_EMAIL_API_VERSION" \
    --body "$body" \
    --output none

  [ "$DRY_RUN" = true ] || wait_for_state "Email Service" \
    "az communication email show --name '$ACS_EMAIL_SERVICE_NAME' --resource-group '$AZURE_RESOURCE_GROUP' --query provisioningState -o tsv"
}

create_communication_service() {
  info "Ensuring ACS Communication Service exists."
  local body
  body=$(json_body \
    --arg location "global" \
    --arg dataLocation "$ACS_DATA_LOCATION" \
    '{ location: $location, properties: { dataLocation: $dataLocation } }')

  run az rest \
    --method PUT \
    --url "https://management.azure.com/subscriptions/$AZURE_SUBSCRIPTION_ID/resourceGroups/$AZURE_RESOURCE_GROUP/providers/Microsoft.Communication/communicationServices/$ACS_COMMUNICATION_SERVICE_NAME?api-version=$ACS_COMM_API_VERSION" \
    --body "$body" \
    --output none

  [ "$DRY_RUN" = true ] || wait_for_state "Communication Service" \
    "az communication show --name '$ACS_COMMUNICATION_SERVICE_NAME' --resource-group '$AZURE_RESOURCE_GROUP' --query provisioningState -o tsv"
}

create_email_domain() {
  info "Ensuring customer-managed email domain exists."
  local body
  body=$(json_body \
    --arg location "global" \
    '{ location: $location, properties: { domainManagement: "CustomerManaged", userEngagementTracking: "Disabled" } }')

  run az rest \
    --method PUT \
    --url "https://management.azure.com/subscriptions/$AZURE_SUBSCRIPTION_ID/resourceGroups/$AZURE_RESOURCE_GROUP/providers/Microsoft.Communication/emailServices/$ACS_EMAIL_SERVICE_NAME/domains/$ACS_EMAIL_DOMAIN?api-version=$ACS_EMAIL_API_VERSION" \
    --body "$body" \
    --output none

  [ "$DRY_RUN" = true ] || wait_for_state "Email domain" \
    "az communication email domain show --domain-name '$ACS_EMAIL_DOMAIN' --email-service-name '$ACS_EMAIL_SERVICE_NAME' --resource-group '$AZURE_RESOURCE_GROUP' --query provisioningState -o tsv"
}

get_verification_records() {
  local attempts=12
  local attempt=0
  local details
  local records

  while [ "$attempt" -lt "$attempts" ]; do
    details=$(az communication email domain show \
      --domain-name "$ACS_EMAIL_DOMAIN" \
      --email-service-name "$ACS_EMAIL_SERVICE_NAME" \
      --resource-group "$AZURE_RESOURCE_GROUP" \
      --only-show-errors \
      -o json)
    records=$(printf '%s' "$details" | jq -c '.verificationRecords // {}')

    if [ "$records" != "{}" ] && [ "$records" != "null" ]; then
      printf '%s' "$records"
      return
    fi

    attempt=$((attempt + 1))
    info "Waiting for verification records ($attempt/$attempts)."
    sleep 5
  done

  fail "Could not retrieve ACS email domain verification records."
}

add_txt_record() {
  local name="$1"
  local value="$2"
  local record_set

  if [ "$DRY_RUN" != true ]; then
    record_set=$(az network dns record-set txt show \
      --resource-group "$AZURE_DNS_ZONE_RESOURCE_GROUP" \
      --zone-name "$AZURE_DNS_ZONE_NAME" \
      --name "$name" \
      -o json 2>/dev/null || true)

    if [ -n "$record_set" ] && printf '%s' "$record_set" | jq -e --arg value "$value" \
      'any(.TXTRecords[]?; ((.value // []) | join("")) == $value)' >/dev/null; then
      info "TXT record already exists at $name."
      return
    fi

    if [ -z "$record_set" ]; then
      run az network dns record-set txt create \
        --resource-group "$AZURE_DNS_ZONE_RESOURCE_GROUP" \
        --zone-name "$AZURE_DNS_ZONE_NAME" \
        --name "$name" \
        --ttl "$ACS_DNS_TTL_SECONDS" \
        --output none >/dev/null 2>&1
    fi
  fi

  run az network dns record-set txt add-record \
    --resource-group "$AZURE_DNS_ZONE_RESOURCE_GROUP" \
    --zone-name "$AZURE_DNS_ZONE_NAME" \
    --record-set-name "$name" \
    --value "$value" \
    --output none >/dev/null 2>&1 || warn "TXT record may already exist: $name"
}

apply_dns_records() {
  local records="$1"
  local domain_name domain_value domain_relative_name
  local spf_name spf_value spf_relative_name existing_spf
  local dkim_name dkim_value relative_name existing_cname

  info "Verification records:"
  printf '%s\n' "$records" | jq .

  domain_name=$(printf '%s' "$records" | jq -r '.Domain.name // empty')
  domain_value=$(printf '%s' "$records" | jq -r '.Domain.value // empty')
  spf_name=$(printf '%s' "$records" | jq -r '.SPF.name // empty')
  spf_value=$(printf '%s' "$records" | jq -r '.SPF.value // empty')

  if [ "$APPLY_DNS" != true ]; then
    return
  fi

  if [ -n "$domain_value" ]; then
    domain_name="${domain_name:-$ACS_EMAIL_DOMAIN}"
    domain_relative_name=$(relative_record_name "$domain_name" "$AZURE_DNS_ZONE_NAME")
    info "Adding domain verification TXT record at $domain_relative_name."
    add_txt_record "$domain_relative_name" "$domain_value"
  fi

  if [ -n "$spf_value" ]; then
    spf_name="${spf_name:-$ACS_EMAIL_DOMAIN}"
    spf_relative_name=$(relative_record_name "$spf_name" "$AZURE_DNS_ZONE_NAME")
    existing_spf=$(az network dns record-set txt show \
      --resource-group "$AZURE_DNS_ZONE_RESOURCE_GROUP" \
      --zone-name "$AZURE_DNS_ZONE_NAME" \
      --name "$spf_relative_name" \
      -o json 2>/dev/null | jq -r '.TXTRecords[]?.value? | join("") | select(test("^v=spf1"; "i"))' || true)

    if [ -n "$existing_spf" ]; then
      if printf '%s\n' "$existing_spf" | grep -Fx -- "$spf_value" >/dev/null; then
        info "SPF TXT record already exists at $spf_relative_name."
      else
        warn "Existing SPF record found at $spf_relative_name. Not adding another SPF record automatically."
        warn "Existing: $existing_spf"
        warn "ACS wants: $spf_value"
      fi
    else
      info "Adding SPF TXT record at $spf_relative_name."
      add_txt_record "$spf_relative_name" "$spf_value"
    fi
  fi

  for key in DKIM DKIM2; do
    dkim_name=$(printf '%s' "$records" | jq -r ".${key}.name // empty")
    dkim_value=$(printf '%s' "$records" | jq -r ".${key}.value // empty")
    [ -n "$dkim_name" ] && [ -n "$dkim_value" ] || continue

    relative_name=$(relative_record_name "$dkim_name" "$AZURE_DNS_ZONE_NAME")
    existing_cname=$(az network dns record-set cname show \
      --resource-group "$AZURE_DNS_ZONE_RESOURCE_GROUP" \
      --zone-name "$AZURE_DNS_ZONE_NAME" \
      --name "$relative_name" \
      --query "CNAMERecord.cname" \
      -o tsv 2>/dev/null || true)

    if [ -n "$existing_cname" ] && [ "$FORCE_DKIM" != true ]; then
      if [ "$existing_cname" = "$dkim_value" ]; then
        info "$key CNAME already exists at $relative_name."
      else
        warn "$key CNAME already exists at $relative_name. Use --force-dkim to replace it."
        warn "Existing: $existing_cname"
        warn "ACS wants: $dkim_value"
      fi
      continue
    fi

    if [ -n "$existing_cname" ]; then
      run az network dns record-set cname delete \
        --resource-group "$AZURE_DNS_ZONE_RESOURCE_GROUP" \
        --zone-name "$AZURE_DNS_ZONE_NAME" \
        --name "$relative_name" \
        --yes \
        --output none
    fi

    run az network dns record-set cname create \
      --resource-group "$AZURE_DNS_ZONE_RESOURCE_GROUP" \
      --zone-name "$AZURE_DNS_ZONE_NAME" \
      --name "$relative_name" \
      --ttl "$ACS_DNS_TTL_SECONDS" \
      --output none >/dev/null 2>&1 || true

    run az network dns record-set cname set-record \
      --resource-group "$AZURE_DNS_ZONE_RESOURCE_GROUP" \
      --zone-name "$AZURE_DNS_ZONE_NAME" \
      --record-set-name "$relative_name" \
      --cname "$dkim_value" \
      --output none
  done
}

verify_domain() {
  if [ "$DRY_RUN" = true ]; then
    return
  fi

  local attempt=0
  local max_attempts=30
  local details domain_status spf_status dkim_status dkim2_status

  if [ "$APPLY_DNS" = true ]; then
    info "Waiting briefly for DNS propagation."
    sleep 30
  else
    warn "DNS was not applied by this script. Checking current domain verification status once."
  fi

  if [ "$APPLY_DNS" = true ]; then
    for type in Domain SPF DKIM DKIM2; do
      run az communication email domain initiate-verification \
        --domain-name "$ACS_EMAIL_DOMAIN" \
        --email-service-name "$ACS_EMAIL_SERVICE_NAME" \
        --resource-group "$AZURE_RESOURCE_GROUP" \
        --verification-type "$type" \
        --only-show-errors \
        --output none >/dev/null 2>&1 || true
    done
  fi

  while [ "$attempt" -lt "$max_attempts" ]; do
    details=$(az communication email domain show \
      --domain-name "$ACS_EMAIL_DOMAIN" \
      --email-service-name "$ACS_EMAIL_SERVICE_NAME" \
      --resource-group "$AZURE_RESOURCE_GROUP" \
      --only-show-errors \
      -o json)
    domain_status=$(printf '%s' "$details" | jq -r '.verificationStates.Domain.status // "Unknown"')
    spf_status=$(printf '%s' "$details" | jq -r '.verificationStates.SPF.status // "Unknown"')
    dkim_status=$(printf '%s' "$details" | jq -r '.verificationStates.DKIM.status // "Unknown"')
    dkim2_status=$(printf '%s' "$details" | jq -r '.verificationStates.DKIM2.status // "Unknown"')

    info "Verification: Domain=$domain_status SPF=$spf_status DKIM=$dkim_status DKIM2=$dkim2_status"
    if [ "$domain_status" = "Verified" ] && [ "$spf_status" = "Verified" ] && [ "$dkim_status" = "Verified" ] && [ "$dkim2_status" = "Verified" ]; then
      info "Domain verification completed."
      return
    fi

    if [ "$APPLY_DNS" != true ]; then
      warn "Domain verification is not complete. Create the printed DNS records, then rerun this script."
      return 1
    fi

    attempt=$((attempt + 1))
    sleep 20
  done

  warn "Domain verification did not complete. DNS propagation can take longer; verify the records in Azure DNS or Azure Portal."
  return 1
}

link_domain() {
  local domain_id="/subscriptions/$AZURE_SUBSCRIPTION_ID/resourceGroups/$AZURE_RESOURCE_GROUP/providers/Microsoft.Communication/emailServices/$ACS_EMAIL_SERVICE_NAME/domains/$ACS_EMAIL_DOMAIN"
  local body
  body=$(json_body --arg domainId "$domain_id" '{ properties: { linkedDomains: [ $domainId ] } }')

  info "Linking email domain to Communication Service."
  run az rest \
    --method PATCH \
    --url "https://management.azure.com/subscriptions/$AZURE_SUBSCRIPTION_ID/resourceGroups/$AZURE_RESOURCE_GROUP/providers/Microsoft.Communication/communicationServices/$ACS_COMMUNICATION_SERVICE_NAME?api-version=$ACS_COMM_API_VERSION" \
    --headers "Content-Type=application/json" \
    --body "$body" \
    --output none || warn "Link failed. The domain may need to finish verification first."
}

create_sender_username() {
  local body
  body=$(json_body \
    --arg username "$ACS_EMAIL_SENDER_USERNAME" \
    --arg displayName "$ACS_EMAIL_SENDER_DISPLAY_NAME" \
    '{ properties: { username: $username, displayName: $displayName } }')

  info "Ensuring sender username exists."
  run az rest \
    --method PUT \
    --url "https://management.azure.com/subscriptions/$AZURE_SUBSCRIPTION_ID/resourceGroups/$AZURE_RESOURCE_GROUP/providers/Microsoft.Communication/emailServices/$ACS_EMAIL_SERVICE_NAME/domains/$ACS_EMAIL_DOMAIN/senderUsernames/$ACS_EMAIL_SENDER_USERNAME?api-version=$ACS_EMAIL_API_VERSION" \
    --body "$body" \
    --output none || warn "Sender creation failed. The domain may need to finish verification first."
}

print_sqlos_config() {
  local from_address="$ACS_EMAIL_SENDER_USERNAME@$ACS_EMAIL_DOMAIN"
  info "SqlOS configuration:"
  cat <<EOF

builder.AddSqlOS<AppDbContext>(options =>
{
    options.AuthServer.ConfigureEmailOtp(email =>
    {
        email.AzureCommunicationServicesConnectionString =
            builder.Configuration["SqlOS:EmailOtp:AzureCommunicationServicesConnectionString"];
        email.FromAddress = "$from_address";
    });
});

Environment variables:
  SqlOS__EmailOtp__AzureCommunicationServicesConnectionString=<connection-string>
  SqlOS__EmailOtp__FromAddress=$from_address

EOF

  if [ "$PRINT_CONNECTION_STRING" = true ] && [ "$DRY_RUN" != true ]; then
    warn "Printing connection strings can leak secrets into logs."
    az communication list-key \
      --name "$ACS_COMMUNICATION_SERVICE_NAME" \
      --resource-group "$AZURE_RESOURCE_GROUP" \
      --query "primaryConnectionString" \
      -o tsv
  else
    info "Connection string command:"
    printf 'az communication list-key --name %q --resource-group %q --query primaryConnectionString -o tsv\n' "$ACS_COMMUNICATION_SERVICE_NAME" "$AZURE_RESOURCE_GROUP"
  fi
}

main() {
  validate_config
  confirm
  azure_login
  create_email_service
  create_communication_service
  create_email_domain

  if [ "$DRY_RUN" = true ]; then
    info "Dry run complete."
    exit 0
  fi

  records=$(get_verification_records)
  apply_dns_records "$records"
  if verify_domain; then
    link_domain
    create_sender_username
  else
    warn "Skipping domain link and sender username until domain verification completes."
  fi
  print_sqlos_config
}

main "$@"
