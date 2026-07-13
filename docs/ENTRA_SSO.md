# Entra SAML Testing

This guide covers the manual end-to-end test flow for a customer organization that uses Microsoft Entra ID as its SAML identity provider.

Start with the public [customer-managed enterprise SSO guide](../web/content/docs/guides/customer-managed-sso.mdx) for the platform/customer trust boundary, one-time setup link, DNS ownership, enrollment policy, and revocation model. This repository supplement focuses on the Entra-specific manual test.

It assumes you are running the shared example stack through the Aspire AppHost and want to validate:

- org-level home realm discovery by email domain
- SAML redirect to the customer's Entra tenant
- callback back into the example app
- PKCE-style code exchange through the example backend
- final session/token issuance in the example web app

## Prerequisites

- Start the shared example stack:

```bash
cd examples/SqlOS.Example.Web
npm install

cd /path/to/SqlOS
dotnet run --project examples/SqlOS.Example.AppHost/SqlOS.Example.AppHost.csproj
```

- Open:
  - dashboard: `http://localhost:5062/sqlos/`
  - example web app: `http://localhost:3010/`

- The example backend seeds the `example-web` client with this callback URL:
  - `http://localhost:3010/auth/callback`

## What SqlOS Expects

For the delegated setup flow, the SqlOS admin creates the organization and a portal setup link. The customer Entra admin opens the portal, copies the SqlOS service provider values, imports Entra federation metadata XML, reviews the connection, and activates it.

The platform-admin dashboard can still create an SSO draft and import metadata directly when your own operators manage the setup.

SqlOS stores and validates:

- verified org domain claim or operator-managed primary domain
- IdP entity ID
- IdP SSO URL
- IdP signing certificate
- redirect URI against the configured client

Current auth defaults for SAML drafts:

- email attribute: `email`
- first name attribute: `first_name`
- last name attribute: `last_name`

If those names do not match the claims coming from Entra, either:

- configure Entra to emit matching claim names, or
- adjust the connection later if you extend the dashboard/API for custom attribute names

For the cleanest first test, configure NameID and email so they line up with the user's real email address.

## Step 1: Create The Customer Organization In SqlOS

In the dashboard:

1. Create the organization.
2. For platform-managed setup, set the organization's `Primary domain` to the customer's login domain, for example `customer.com`.
3. For self-serve delegated setup, leave the primary domain empty and let the customer admin verify their domain in the portal through DNS TXT.
4. Open the `SSO` section.
5. Create a delegated setup link for the customer IT admin, or create an SSO draft if your platform team is configuring Entra directly.

Recommended draft settings:

- `Auto provision users`: enabled
- `Auto link by email`: disabled for the first test unless you intentionally want linking behavior

After draft or setup-link creation, SqlOS shows:

- `SP Entity ID`
- `ACS URL`
- `Org primary domain`

These are the values you give to the customer's Entra admin.

For delegated onboarding, send the setup URL to the customer IT admin through your own mailer or ticketing system. The first open consumes the URL token and stores a hardened server-side portal session in an HttpOnly cookie scoped to `/sqlos/admin/auth/sso-portal`.

If the customer admin claims a domain in the delegated portal, SqlOS shows a TXT record such as:

- Type: `TXT`
- Name: `_sqlos-verify.customer.com`
- Value: `sqlos-domain-verification=...`

The connection cannot be activated for self-serve HRD until that TXT record is found, unless you disable `SsoPortal.RequireVerifiedDomainForActivation` because your host verifies ownership elsewhere.

## Step 2: What The Customer Entra Admin Configures

In Microsoft Entra admin center, the customer admin should:

1. Create or open an Enterprise Application for your app.
2. Choose `Single sign-on`.
3. Choose `SAML`.
4. Set:
   - `Identifier (Entity ID)` = the `SP Entity ID` from the SqlOS dashboard
   - `Reply URL (Assertion Consumer Service URL)` = the `ACS URL` from the SqlOS dashboard
5. Configure NameID so it resolves to the user's login email or UPN.
6. Ensure the application is assigned to at least one Entra user you plan to test with.
7. Download or copy the `Federation Metadata XML`.

The one artifact you want back from the Entra admin is:

- `Federation Metadata XML`

That is what SqlOS imports.

## Step 3: Import The Entra Metadata Into SqlOS

In the delegated portal:

1. Choose `Microsoft Entra`.
2. Enter the customer email domain and publish the TXT ownership record.
3. Confirm the TXT record after DNS has propagated.
4. Paste or upload the full federation metadata XML from Entra.
5. Click `Validate metadata`.
6. Click `Save metadata`, review the parsed IdP Entity ID and SSO URL, then click `Activate connection`.
7. Optionally enter the test client id `example-web` and redirect URI `https://client.example.local/callback`, then run the test to generate a SAML redirect.

If the platform admin is doing the setup directly in the dashboard:

1. Open the `Import Entra Metadata` form.
2. Paste the SSO connection ID from the draft you created.
3. Paste the full federation metadata XML from Entra.
4. Submit the form.

After import:

- portal setup shows the connection as `ready_to_activate` until activation
- dashboard direct import enables the connection immediately
- home realm discovery will use the active verified domain first, and fall back to the intended primary domain for operator-managed setups

At this point the org is ready for SSO testing.

## Step 4: Test The User Login Flow

Go to the example web app:

- `http://localhost:3010/login`

Enter an email at the customer's domain, for example:

- `alice@customer.com`

Expected behavior:

1. The example frontend sends the email to the example backend `discover` flow.
2. SqlOS matches the domain to the organization primary domain.
3. Because the org has enabled SSO, the login flow should not continue with password entry.
4. The example backend starts the SSO authorization flow and redirects the browser to Entra.
5. The user signs in with Entra.
6. Entra posts the SAML response to the SqlOS ACS endpoint.
7. SqlOS validates the SAML response and redirects back to:
   - `http://localhost:3010/auth/callback`
8. The example frontend completes the exchange through the example backend.
9. The example app lands on `/app` with a valid session.

## Step 5: What Success Looks Like

After a successful SSO login:

- the web app `/app` page should render normally
- the page should show:
  - NextAuth session data
  - decoded access token claims
  - backend session debug data
- the access token should include:
  - `sub`
  - `sid`
  - `client_id`
  - `org_id`
  - `amr` set to `saml`

In the dashboard:

- `Sessions` should show a new SAML-authenticated session
- `Audit Events` should include the SAML login
- if auto-provisioning is enabled and the user did not already exist, the user should now appear in `Users`
- if the user was not already a member, SqlOS will create a membership for the org during the SAML flow

## Common Failure Cases

If the login does not redirect to Entra:

- confirm the domain is active in delegated setup, or that the org `Primary domain` exactly matches the email domain for operator-managed setup
- confirm the SSO connection is enabled

If Entra redirects but the ACS step fails:

- confirm the imported metadata XML matches the current Entra app configuration
- confirm the `Identifier (Entity ID)` and `Reply URL` in Entra match the values shown by SqlOS

If the user reaches Entra but SqlOS cannot resolve a user:

- make sure the SAML assertion includes a usable email
- for easiest testing, use auto-provisioning

If callback exchange fails:

- confirm the example app is still running on `http://localhost:3010`
- confirm the example backend seeded client still allows `http://localhost:3010/auth/callback`

## Recommended First Test

For the simplest customer-tenant validation:

1. Create a new org with primary domain set.
2. Create an SSO draft with:
   - auto provision users = on
   - auto link by email = off
3. Have the Entra admin configure SAML and return federation metadata XML.
4. Import the metadata.
5. Test with a real assigned Entra user at that domain.

That path avoids pre-creating users and avoids ambiguous email linking.
