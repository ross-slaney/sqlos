#!/usr/bin/env bash
set -euo pipefail

# Create or reuse a Twilio Verify Service for SqlOS phone OTP.
#
# Required environment variables:
#   TWILIO_ACCOUNT_SID
#   TWILIO_AUTH_TOKEN
#
# Optional environment variables:
#   TWILIO_VERIFY_SERVICE_SID       Reuse this Verify Service instead of creating one.
#   TWILIO_VERIFY_SERVICE_NAME      Defaults to "SqlOS Auth"
#   TWILIO_VERIFY_CODE_LENGTH       Defaults to 6
#
# Usage:
#   TWILIO_ACCOUNT_SID=... TWILIO_AUTH_TOKEN=... \
#   TWILIO_VERIFY_SERVICE_NAME="SqlOS Example" \
#   ./scripts/twilio/setup-twilio-verify.sh

DRY_RUN=false
PRINT_SECRETS=false
YES=false

usage() {
  cat <<'EOF'
Usage: scripts/twilio/setup-twilio-verify.sh [options]

Options:
  --dry-run         Print the Twilio API action without mutating Twilio resources.
  --print-secrets   Include auth-token values in the printed env block.
  --yes             Skip confirmation prompts.
  -h, --help        Show this help.

The script creates a Twilio Verify Service unless TWILIO_VERIFY_SERVICE_SID is already set.
It prints the SqlOS and sample-app environment variables needed to enable SMS OTP.
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    --dry-run) DRY_RUN=true ;;
    --print-secrets) PRINT_SECRETS=true ;;
    --yes) YES=true ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage; exit 1 ;;
  esac
  shift
done

info() { printf '[info] %s\n' "$*" >&2; }
warn() { printf '[warn] %s\n' "$*" >&2; }
fail() { printf '[error] %s\n' "$*" >&2; exit 1; }

require_command() {
  command -v "$1" >/dev/null 2>&1 || fail "$1 is required but was not found."
}

require_env() {
  local name="$1"
  [ -n "${!name:-}" ] || fail "$name is required."
}

confirm() {
  if [ "$YES" = true ] || [ "$DRY_RUN" = true ]; then
    return
  fi

  printf 'Create a Twilio Verify Service in account %s? [y/N] ' "$TWILIO_ACCOUNT_SID"
  read -r answer
  case "$answer" in
    y|Y|yes|YES) ;;
    *) fail "Aborted." ;;
  esac
}

twilio_create_verify_service() {
  local friendly_name="$1"
  local code_length="$2"

  if [ "$DRY_RUN" = true ]; then
    printf '[dry-run] curl -fsS -u <twilio-credential> -X POST https://verify.twilio.com/v2/Services --data-urlencode FriendlyName=%q --data-urlencode CodeLength=%q\n' "$friendly_name" "$code_length" >&2
    printf 'VA_DRY_RUN_VERIFY_SERVICE_SID'
    return
  fi

  curl -fsS \
    -u "$TWILIO_ACCOUNT_SID:$TWILIO_AUTH_TOKEN" \
    -X POST "https://verify.twilio.com/v2/Services" \
    --data-urlencode "FriendlyName=$friendly_name" \
    --data-urlencode "CodeLength=$code_length"
}

print_sqlos_env() {
  local service_sid="$1"
  local auth_token_value="<auth-token>"

  if [ "$PRINT_SECRETS" = true ]; then
    auth_token_value="${TWILIO_AUTH_TOKEN:-}"
  fi

  cat <<EOF

SqlOS SMS OTP environment:

SqlOS__PhoneOtp__Enabled=true
SqlOS__PhoneOtp__TwilioAccountSid=$TWILIO_ACCOUNT_SID
SqlOS__PhoneOtp__TwilioAuthToken=$auth_token_value
SqlOS__PhoneOtp__TwilioVerifyServiceSid=$service_sid
SqlOS__PhoneOtp__DefaultRegion=${TWILIO_DEFAULT_REGION:-US}
EOF

  cat <<EOF

Todo sample flag:

TodoSample__EnablePhoneOtp=true

Portable TWILIO_* aliases accepted by the examples:

TWILIO_ACCOUNT_SID=$TWILIO_ACCOUNT_SID
TWILIO_AUTH_TOKEN=$auth_token_value
TWILIO_VERIFY_SERVICE_SID=$service_sid
TWILIO_DEFAULT_REGION=${TWILIO_DEFAULT_REGION:-US}
EOF

  cat <<'EOF'

Use --print-secrets only in a local terminal you trust. Avoid committing these values.
EOF
}

main() {
  require_command curl
  require_command jq
  require_env TWILIO_ACCOUNT_SID
  require_env TWILIO_AUTH_TOKEN

  local service_sid="${TWILIO_VERIFY_SERVICE_SID:-}"
  local service_name="${TWILIO_VERIFY_SERVICE_NAME:-SqlOS Auth}"
  local code_length="${TWILIO_VERIFY_CODE_LENGTH:-6}"

  if [ -z "$service_sid" ]; then
    confirm
    info "Creating Twilio Verify Service '$service_name'."
    local response
    response="$(twilio_create_verify_service "$service_name" "$code_length")"
    if [ "$DRY_RUN" = true ]; then
      service_sid="$response"
    else
      service_sid="$(printf '%s' "$response" | jq -r '.sid // empty')"
    fi

    [ -n "$service_sid" ] || fail "Twilio response did not include a Verify Service SID."
  else
    info "Using existing Verify Service $service_sid."
  fi

  print_sqlos_env "$service_sid"
}

main "$@"
