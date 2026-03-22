namespace SqlOS.Configuration;

internal static class SqlOSPathDefaults
{
    /// <summary>
    /// Align auth and FGA dashboard URLs with <see cref="SqlOSOptions.DashboardBasePath"/>.
    /// </summary>
    public static void Apply(SqlOSOptions options)
    {
        var root = options.DashboardBasePath.TrimEnd('/');
        options.AuthServer.BasePath = $"{root}/auth";
        options.Fga.DashboardPathPrefix = $"{root}/admin/fga";
    }
}
