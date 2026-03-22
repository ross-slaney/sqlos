namespace SqlOS.Todo.Api.Configuration;

public sealed class TodoSampleOptions
{
    public string PublicOrigin { get; set; } = "http://localhost:5080";
    public string HostedClientId { get; set; } = "todo-web";
    public string HostedClientName { get; set; } = "Todo Web App";
    public string HeadlessUiPath { get; set; } = "/headless.html";
    public bool EnableHeadless { get; set; }
    public bool EnableDcr { get; set; }
    public string Resource { get; set; } = "http://localhost:5080/api/todos";
    public string LocalClientId { get; set; } = "todo-local";
    public string LocalRedirectUri { get; set; } = "http://localhost:8787/oauth/callback";
    public string PortableClientPath { get; set; } = "/clients/portable-client.json";
    public string PortableClientName { get; set; } = "SqlOS Todo Portable Client";
    public string PortableClientRedirectUri { get; set; } = "https://portable.todo.example.com/auth/callback";
    public string PortableClientUri { get; set; } = "https://portable.todo.example.com";
    public string PortableSoftwareId { get; set; } = "io.sqlos.todo";
    public string PortableSoftwareVersion { get; set; } = "2026.1";
    public List<string> AllowedScopes { get; set; } = ["openid", "profile", "email", "offline_access", "todos.read", "todos.write"];
    public List<string> CimdTrustedHosts { get; set; } = [];
}
