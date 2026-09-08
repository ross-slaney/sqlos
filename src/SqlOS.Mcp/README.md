# SqlOS.Mcp

Hosts a [Model Context Protocol](https://modelcontextprotocol.io) server on the MCP surface declared by
`UseSingleApplication` or `ConfigureApplication`. Companion package for [SqlOS](https://www.nuget.org/packages/SqlOS);
ships on the same version line.

```csharp
builder.AddSqlOS<AppDbContext>(options =>
{
    options.UseSingleApplication("PetalPal", app =>
    {
        app.Origin = "https://petalpal.example.com";
        app.Api = "/api";
        app.Mcp("/mcp", mcp => mcp.WithTools<GardenMcpTools>());
    });
});

var app = builder.Build();
app.MapGet("/api/gardens", ...);   // already protected: SqlOS validates tokens under /api
app.Run();
```

Declaring the surface makes SqlOS:

- validate bearer tokens for the audience `{Origin}/mcp` on the mapped MCP endpoints,
- serve the RFC 9728 document at `/.well-known/oauth-protected-resource/mcp`,
- enable client ID metadata documents and resource indicators so portable MCP clients can connect,
- register the MCP SDK server (stateless Streamable HTTP), apply your `configure` callback to the
  SDK builder unchanged, and map it on the protected branch at startup,
- record a SqlOS audit event per tool call (tool name, subject, client, outcome; never arguments or tokens).

`AddSqlOS` attaches that validation to the mapped endpoints; nothing is placed or ordered by application code. `ConfigureApplication` retains this hosting setup while you register multiple clients explicitly.

Tools can inject `ISqlOSMcpUserContext` to act as the connecting user without touching the raw token. Tools must still call application services that enforce permissions; hosting and authentication do not grant access to application data.

Documentation: https://sqlos.dev/docs/authserver/mcp-server
