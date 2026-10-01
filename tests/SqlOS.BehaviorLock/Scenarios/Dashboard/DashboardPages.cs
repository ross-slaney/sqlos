using AngleSharp.Html.Parser;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Dashboard;

/// <summary>
/// Reads values out of dashboard responses for the dashboard scenarios. The transcript already
/// locks the shell HTML (its inline bootstrap script as a digest); these helpers only make the
/// values the server injected readable next to it.
/// </summary>
internal static class DashboardPages
{
    /// <summary>The paths every dashboard shell deep link serves, one per dashboard area.</summary>
    public static readonly IReadOnlyList<(string Path, string Caption)> DeepLinks =
    [
        ("/sqlos/admin/auth/users", "an auth admin deep link serves the shell"),
        ("/sqlos/admin/audit/events", "an audit deep link serves the shell"),
        ("/sqlos/admin/email/templates", "an email deep link serves the shell"),
        ("/sqlos/admin/calendar/connections", "a calendar deep link serves the shell"),
        ("/sqlos/admin/fga/resources", "an FGA deep link serves the shell")
    ];

    /// <summary>
    /// The text of the shell's first inline script: the base path and capabilities the dashboard
    /// middleware writes into <c>index.html</c> for the browser code.
    /// </summary>
    public static string BootstrapScript(HttpExchange shell)
    {
        var parser = new HtmlParser();
        using var document = parser.ParseDocument(shell.ResponseBody);
        var script = document.Scripts.FirstOrDefault(element => !element.HasAttribute("src"))
            ?? throw new InvalidOperationException($"Exchange {shell.Describe()} has no inline script.");
        var lines = script.TextContent
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0);
        return string.Join('\n', lines);
    }
}
