# Notes: one `AddSqlOS` call, a protected API, and an MCP server

This is the smallest complete SqlOS application. `Program.cs` describes the app once and SqlOS derives the rest: the auth server and hosted sign-in, bearer validation for `/api` and `/mcp`, the protected-resource documents, the MCP server, the branding, and the permission model. Application code contains no `MapSqlOS`, `RequireSqlOSAccessToken`, `AddMcpServer`, `MapMcp`, hand-written metadata document, or middleware placement.

```csharp
builder.AddSqlOS<NotesDbContext>(db => db.UseSqlServer(connectionString), options =>
    options.UseSingleApplication("Notes", app =>
    {
        app.Origin = origin;
        app.Api = "/api";
        app.Mcp("/mcp", mcp => mcp.WithTools<NotesMcpTools>());
        app.Brand(page => { page.PageTitle = "Notes"; page.PrimaryColor = "#14532d"; });
        app.Authorization(fga => fga
            .ResourceType("notebook", "Notebook")
            .Permission("NOTES_READ", "Read notes", "notebook")
            .Permission("NOTES_WRITE", "Write notes", "notebook")
            .Role("notebook_owner", "Notebook owner").Can("NOTES_READ", "NOTES_WRITE"));
    }));

var app = builder.Build();
var api = app.MapGroup("/api");   // already protected
api.MapGet("/notes", ...);
api.MapPost("/notes", ...);
app.Run();
```

Like the other SqlOS examples, this host is bearer-only. Clients (a browser SPA, a native app, or an MCP agent) sign in through the hosted pages as the derived `notes` client and call the API with the resulting token; the host does not run a browser session of its own.

## Run

Use .NET 9 and a SQL Server or PostgreSQL database. From the repository root, supply the connection through your environment or secret store:

```bash
export ConnectionStrings__DefaultConnection="Server=localhost,1434;Database=sqlos-notes;User Id=sa;Password=<your-password>;TrustServerCertificate=True"
dotnet run --project examples/SqlOS.OneCall.Api
```

For PostgreSQL instead:

```bash
export Notes__DatabaseProvider=PostgreSQL
export ConnectionStrings__DefaultConnection="Host=localhost;Port=5432;Database=sqlos_notes;Username=postgres;Password=<your-password>"
dotnet run --project examples/SqlOS.OneCall.Api
```

The launch profile uses Development and `http://localhost:5085`. Use `Notes:Origin` to change the public origin, and make the listening URL match. This sample creates its application tables with `EnsureCreatedAsync` on a fresh database; production applications use EF migrations for their tables. SqlOS initializes its own schema separately.

## Try it

```bash
curl -i http://localhost:5085/api/notes
curl -i -X POST http://localhost:5085/mcp -H 'content-type: application/json' -d '{}'
curl http://localhost:5085/.well-known/oauth-protected-resource
curl http://localhost:5085/.well-known/oauth-protected-resource/mcp
curl http://localhost:5085/sqlos/auth/.well-known/oauth-authorization-server
```

The first two return `401` with a Bearer challenge that names the realm and the `resource_metadata` URL. The next two describe the two distinct resources (`{origin}/api` and `{origin}/mcp`) and their shared authorization server. The last advertises client ID metadata documents and resource indicators, which the MCP declaration turned on; dynamic client registration stays off.

To call the API as a user, complete the authorization-code flow for client `notes` with callback `http://localhost:5085/auth/callback` and `resource=http://localhost:5085/api` using any OIDC library, then send the access token as a bearer. Connect an MCP client (Codex, Claude, Cursor, ChatGPT desktop) to `http://localhost:5085/mcp`; it discovers the authorization server, signs in through the hosted pages, and calls `list_notes` and `add_note` as that user. Internet-hosted clients need a publicly reachable HTTPS deployment. See [MCP server](https://sqlos.dev/docs/authserver/mcp-server).

The dashboard at `/sqlos` is open without a password in Development only. Configure dashboard authentication before enabling it in production.

## What each piece does

| Configuration or file | Effect |
| --- | --- |
| [NotesApplication.cs](NotesApplication.cs) | The complete host: one `AddSqlOS` call, the `/api` handlers, and sample database setup |
| `app.Api = "/api"` | Requires a token for `http://localhost:5085/api` on the mapped `/api` endpoints before the handler runs |
| `app.Mcp("/mcp", mcp => mcp.WithTools<NotesMcpTools>())` | Hosts and audits the MCP tools, protects the distinct MCP audience, and enables CIMD/resource indicators |
| `app.Brand(...)` | Seeds code-owned title, copy, and colors for the hosted pages |
| `app.Authorization(...)` | Seeds notebook resource type, read/write permissions, and the owner role; it grants nobody access by itself |
| [Notes.cs](Notes.cs) | Domain entities, transactional first-use provisioning, FGA enforcement, and MCP tools |
| [AUTHORIZATION.md](AUTHORIZATION.md) | Intended resource/subject/grant model and revocation behavior |

Both surfaces call the same `NotesService`. First use commits the unique notebook row, subject, resource, and initial grant in one transaction. Every subsequent operation checks the current grant, so removing `notebook_owner` in the dashboard makes both API and MCP calls fail until an administrator restores it. Concurrent first requests cannot duplicate the notebook or its owner grant.

`NotesJourneyIntegrationTests` runs this host against SQL Server and PostgreSQL in CI: hosted sign-in for the derived client, API and MCP success, cross-audience rejection, two-user isolation, revocation and explicit restoration, tool-call audit events, and concurrent provisioning. See [multiple applications](https://sqlos.dev/docs/authserver/multiple-applications) for keeping this host while adding explicit clients.
