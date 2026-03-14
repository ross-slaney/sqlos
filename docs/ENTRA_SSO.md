# Entra SAML Testing

This guide covers the manual end-to-end test flow for a customer organization that uses Microsoft Entra ID as its SAML identity provider.

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

For the intended setup flow, the SqlOS admin creates an SSO draft first, then imports the Entra federation metadata XML.

SqlOS stores and validates:

- org primary domain
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
2. Set the organization's `Primary domain` to the customer's login domain, for example `customer.com`.
3. Open the `SSO` section and create an SSO draft for that organization.

Recommended draft settings:

- `Auto provision users`: enabled
- `Auto link by email`: disabled for the first test unless you intentionally want linking behavior

After draft creation, the dashboard shows:

- `SP Entity ID`
- `ACS URL`
- `Org primary domain`

These are the values you give to the customer's Entra admin.

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

Back in the SqlOS dashboard:

1. Open the `Import Entra Metadata` form.
2. Paste the SSO connection ID from the draft you created.
3. Paste the full federation metadata XML from Entra.
4. Submit the form.

After import:

- the SSO connection should be enabled
- the organization should still have the intended primary domain

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

- confirm the org `Primary domain` exactly matches the email domain
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
