namespace SqlOS.Configuration;

internal static class SqlOSPathDefaults
{
    /// <summary>
    /// Align auth URLs with <see cref="SqlOSOptions.DashboardBasePath"/>.
    /// </summary>
    public static void Apply(SqlOSOptions options)
    {
        var root = options.DashboardBasePath.TrimEnd('/');
        options.AuthServer.BasePath = $"{root}/auth";

        if (string.Equals(options.AuthServer.Issuer, "https://localhost/sqlos/auth", StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(options.AuthServer.PublicOrigin)
            && Uri.TryCreate(options.AuthServer.SingleApplication?.Origin, UriKind.Absolute, out var applicationOrigin))
        {
            options.AuthServer.Issuer = $"{applicationOrigin.GetLeftPart(UriPartial.Authority).TrimEnd('/')}{options.AuthServer.BasePath}";
        }
    }
}
