export const authHighlights = [
  {
    title: "Guided provider setup",
    body: "The dashboard walks you through Google, Microsoft, Apple, and custom OIDC configuration with provider-specific instructions and copy-ready callback URIs.",
  },
  {
    title: "Enterprise SSO in minutes",
    body: "Create a SAML draft, hand your customer the Entity ID and ACS URL, import their federation metadata. Home realm discovery routes users by email domain automatically.",
  },
  {
    title: "Sessions, keys, and audit",
    body: "Refresh token rotation, automatic RS256 key rotation with grace windows, session revocation, and a full audit log all visible in the dashboard.",
  },
] as const;

export const authStackFeatures = [
  {
    title: "SSO for any provider",
    description:
      "Support SAML and OIDC identity providers with a single integration. Configure per-org from the embedded dashboard.",
  },
  {
    title: "User and org management",
    description:
      "Manage users, organizations, memberships, and sessions from the dashboard or programmatically via APIs.",
  },
  {
    title: "Social authentication",
    description:
      "Google, Microsoft, Apple, or custom OIDC. Guided setup with provider-specific instructions and copy-ready callback URIs.",
  },
  {
    title: "Hosted UI or headless APIs",
    description:
      "Use the branded AuthPage to ship fast, or build your own frontend and call the OAuth and session APIs directly.",
  },
] as const;

export const fgaConcepts = [
  {
    label: "Resources",
    description: "Define types and nest them into a hierarchy that matches your product.",
  },
  {
    label: "Grants",
    description: "Assign a role at any node and permissions inherit downward automatically.",
  },
  {
    label: "Queries",
    description: "Access checks fold into EF Core LINQ as a WHERE clause, not a service call.",
  },
] as const;

export const performanceStats = [
  { value: "3.47ms", label: "per page at 1.2M rows" },
  { value: "<1.5ms", label: "point checks, D=10" },
  { value: "O(k·D)", label: "bounded, N-free" },
] as const;

export const productFeatures = [
  {
    title: "OAuth 2.0 + PKCE",
    description: "/authorize, /token, JWKS, and discovery endpoints in your ASP.NET pipeline.",
  },
  {
    title: "Branded AuthPage",
    description: "Server-rendered login, signup, and logout with your logo, your colors, and your domain.",
  },
  {
    title: "Social + OIDC",
    description: "Google, Microsoft, Apple, and custom providers with guided setup and copy-ready callbacks.",
  },
  {
    title: "SAML SSO",
    description: "Org-scoped enterprise SSO with home realm discovery by email domain.",
  },
  {
    title: "FGA engine",
    description: "Hierarchical resources, role grants, time-windowed access, and EF Core query filters.",
  },
  {
    title: "Admin dashboard",
    description: "Embedded UI for orgs, users, providers, grants, sessions, and audit with optional password protection.",
  },
  {
    title: "Key rotation",
    description: "Automatic RS256 signing key rotation with configurable intervals and grace windows.",
  },
  {
    title: "Orgs and users",
    description: "Multi-tenant user management with memberships, sessions, refresh tokens, and audit log.",
  },
  {
    title: "Example stack",
    description: "Aspire AppHost plus a .NET API and Next.js frontend exercising every flow so you can run it and fork it.",
  },
] as const;

export const exampleApps = [
  {
    title: "Todo sample",
    description: "Hosted auth, per-user FGA, and MCP-oriented client onboarding in about five minutes.",
    href: "/docs/getting-started#run-the-right-sample",
    cta: "Run Todo sample",
  },
  {
    title: "Example stack",
    description: "Full Aspire host with .NET API, Next.js, Angular, and Expo clients.",
    href: "/docs/authserver/todo-sample",
    cta: "Explore example stack",
  },
  {
    title: "Retail FGA",
    description: "Multi-level retail hierarchy with chain, store, and inventory authorization.",
    href: "/docs/fga/overview",
    cta: "See FGA in action",
  },
] as const;

export const fgaCode = `// Authorization is a WHERE clause, not a service call
var filter = await fga.BuildFilterAsync<Project>(
    subjectId: user.Id,
    permissionKey: "projects.read");

var projects = await db.Projects
    .Where(filter)          // TVF folds into the query plan
    .Where(p => p.IsActive)
    .OrderBy(p => p.Name)
    .Take(20)
    .ToListAsync();         // One query. One round-trip.`;
