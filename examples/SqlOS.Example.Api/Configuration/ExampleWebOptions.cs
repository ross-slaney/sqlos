namespace SqlOS.Example.Api.Configuration;

public sealed class ExampleWebOptions
{
    public string Origin { get; set; } = "http://localhost:3000";
    public string CallbackUrl { get; set; } = "http://localhost:3000/auth/callback";
    public string ClientId { get; set; } = "example-web";
}
