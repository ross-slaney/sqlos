# SqlOS

**Authentication and authorization for .NET applications, running in your process and your SQL Server or PostgreSQL database.**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![NuGet](https://img.shields.io/nuget/v/SqlOS)](https://www.nuget.org/packages/SqlOS)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com)

SqlOS gives your application hosted sign-in, enterprise SSO, organizations, sessions, an admin dashboard, and fine-grained authorization that filters EF Core queries in SQL. You own the accounts and data, and run the infrastructure.

Working copies of the one-app host, MCP + authorization, branding, and identity-provider setups are in the [hosting quick reference](docs/QUICK_REFERENCE.md).

## Build one screen for every role

A company administrator, a team member, and an outside collaborator can use the same endpoint and see different records. SqlOS's **scoped hierarchical role-based access control (SHRBAC)** models resources as a tree, and `ToPageAsync` reads the page of rows each caller may access.

![An application root contains Workspace A and Workspace B. A team grant on A reaches its two documents. The same query shows all four documents to a root administrator, A and B to the team, and only D to a guest granted that document.](web/public/docs/fga-resource-tree.png)

For a document editor, create a workspace when someone signs up and grant them a role containing `document.read` on it. Place their documents underneath. New documents inherit access automatically; you do not create another grant for every document. Grant a team access through a group, or share an individual document with a guest. Users, groups, service accounts, and agents use the same model.

```csharp
var page = await db.Documents
    .OrderBy(d => d.Name)
    .ToPageAsync(subjectId, "document.read", pageSize: 20, cursor);
```

The endpoint needs no `where tenantId` or `where userId` to discover authorized records across the caller's grants. Add those conditions when the screen itself is scoped to a tenant or owner. Index the order the screen pages by (`HasIndex(d => new { d.Name, d.Id })`), as for any database; SqlOS keeps a copy of every index under every node of the resource tree. Pass `NextCursor` back for the next page. Point checks (`CheckAccessAsync`) protect one record, individual actions, and writes.

### Design the hierarchy around access

- **Grant where access belongs.** Workspace → document, team → inspection → item, or department → case → record. Choose the highest node that matches the intended access. A grant covers the relevant descendants, including new ones; do not broaden access just to make queries faster.
- **Use groups for shared responsibilities.** One group grant replaces duplicate grants for its members. Use direct grants for individual shares. Membership or ownership alone does not grant access.
- **Index the columns your lists sort by.** SqlOS keeps a copy of every index of a protected table under every node of the resource tree, in its own tables. `ToPageAsync` then costs about a page for any caller: one who sees everything, one who sees three rows of a million, one whose rows sit at the far end of the order. A sort with no index still pages correctly but reads the table in that order for every caller, as in any database, and SqlOS logs the index to add.
- **Keep application indexes deliberate.** Index the filters and orders your screens use, and no more: every index is copied under every node and written on every save. SqlOS adds nothing to your tables; a page order is a table in SqlOS's own schema, kept current in the same transaction as each save.
- **Treat hierarchy changes as permission changes.** Moving or deactivating a branch changes which ancestor grants apply to its descendants, and rewrites that branch's page-order entries.
- **Pages and point checks are the whole read API.** SqlOS has no authorized count, join, or unpaged list: each of those costs every row the caller can see. Show a page and whether there is a next one.

Application tables connect to SqlOS resources through `ResourceId`. SQL Server or PostgreSQL evaluates the recursive authorization function in the query; there is no preliminary visibility count or external policy service.

→ **[Designing your resource tree and its tradeoffs](https://sqlos.dev/docs/fga/designing-your-resource-tree)** · [Authorize EF Core queries](https://sqlos.dev/docs/quickstarts/ef-authorization) · [Page shapes and indexes](https://sqlos.dev/docs/guides/paginating-authorized-lists)

The retail sample uses the same endpoints for both callers:

<table>
  <tr>
    <td width="50%" valign="top">
      <p><strong>Company Admin</strong> — five chains visible</p>
      <img src="https://sqlos.dev/docs/retail-app-admin-dashboard.png" alt="Retail app as Company Admin with five chains and multi-store inventory" />
    </td>
    <td width="50%" valign="top">
      <p><strong>Store Clerk</strong> — one store, filtered in SQL</p>
      <img src="https://sqlos.dev/docs/retail-app-clerk-dashboard.png" alt="Retail app as Store Clerk with zero chains and one store visible" />
    </td>
  </tr>
</table>

## Authentication and identity

### 1. An auth server

A full OAuth 2.0 authorization server and OpenID Connect Provider mounted inside your app: authorization code + PKCE, refresh tokens, device flow, client credentials, discovery, ID tokens, UserInfo, and a consent screen for third-party clients — verified against the OpenID Foundation conformance suite in CI.

Sign-in methods are configuration, not projects: passwords, email OTP, magic links, SMS, social login (Google, Microsoft, GitHub, Apple, any OIDC provider), SAML enterprise SSO, SCIM directory sync, and TOTP MFA. B2B primitives — organizations, memberships, invitations, per-application access rules — are built in. Other apps can even use yours as their identity provider ([Sign in with X](https://sqlos.dev/docs/guides/sign-in-with-x)).

<p align="center">
  <img src="https://sqlos.dev/docs/dashboard-home.png" alt="SqlOS admin dashboard home showing Auth Server and Fine-Grained Auth counts" width="900" />
</p>

<p align="center">
  <img src="https://sqlos.dev/docs/guides-sign-in-with-x-consent.png" alt="SqlOS consent screen for Sign in with X, listing scopes by display name" width="560" />
</p>

→ [Auth server overview](https://sqlos.dev/docs/authserver/overview) · [OpenID Provider](https://sqlos.dev/docs/authserver/openid-provider) · [Organizations](https://sqlos.dev/docs/authserver/organizations)

### 2. AuthPage — hosted login UI

Login, signup, OTP entry, MFA, organization selection, and consent ship as hosted pages, ready on day one. Brand them with your name, logo, and colors — from code seeds, the Admin API, or the dashboard — and the same identity carries into the built-in OTP, invitation, and password-reset emails.

<p align="center">
  <img src="https://sqlos.dev/docs/guides-social-sign-in.png" alt="Hosted AuthPage with email continue plus GitHub and Microsoft social providers" width="560" />
</p>

→ [Brand hosted auth and email](https://sqlos.dev/docs/guides/auth-branding) · [Hosted vs. headless](https://sqlos.dev/docs/authserver/hosted-vs-headless)

### 3. Headless auth — bring your own UI

If the hosted pages don't fit your product, keep SqlOS as the protocol engine and draw every screen yourself: `app.Headless("/auth/authorize")` in the one-call setup, or `AuthServer.UseHeadlessAuthPage` on a multi-app host. Your frontend talks to a typed state machine — login, signup, OTP, MFA, consent — while SqlOS still owns OAuth, PKCE, sessions, and tokens. Extra signup fields, A/B tests, and native-feeling popups all become your UI's decisions.

<p align="center">
  <img src="https://sqlos.dev/docs/guides-custom-login-ui.svg" alt="Product-owned login UI connected to the SqlOS headless authentication state machine" width="900" />
</p>

→ [Build your own login and signup UI](https://sqlos.dev/docs/guides/custom-login-ui) · [Headless auth reference](https://sqlos.dev/docs/guides/custom-login-ui)

## See it running in 2 minutes

The Todo sample gives you a working login flow and authorized EF Core queries without touching your own code:

```bash
dotnet run --project examples/SqlOS.Todo.AppHost/SqlOS.Todo.AppHost.csproj
```

Then open `http://localhost:5090/`. The Aspire AppHost starts PostgreSQL, the Todo API with SqlOS at `http://localhost:5080`, and a Razor Pages client at `http://localhost:5090`. Set `SqlOS:DatabaseProvider=SqlServer` to start SQL Server instead.

<p align="center">
  <img src="https://sqlos.dev/docs/guides-password-login.png" alt="Hosted AuthPage password step from the SqlOS Todo sample" width="560" />
</p>

[Todo sample walkthrough](https://sqlos.dev/docs/quickstarts/run-todo) · [Hosting quick reference](docs/QUICK_REFERENCE.md) · [All documentation](https://sqlos.dev/docs)

## Contributing

```bash
dotnet build SqlOS.sln
./scripts/unit-tests.sh
./scripts/integration-tests.sh
./scripts/docs-check.sh
```

SqlOS is MIT licensed. Issues and pull requests are welcome.
