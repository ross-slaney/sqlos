using System.Text;

namespace SqlOS.Example.Api.FgaRetail.Endpoints;

/// <summary>
/// Opaque cursor codec for the retail list endpoints. Encodes the active sort value plus the
/// unique Id tiebreaker of the last row on a page. The cursor format is an implementation
/// detail of each endpoint — clients treat it as an opaque string.
/// </summary>
internal static class RetailCursor
{
    public static string Encode(string sortValue, string id)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{sortValue}\n{id}"));

    public static (string SortValue, string Id) Decode(string cursor)
    {
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
        var parts = decoded.Split('\n', 2);
        return (parts[0], parts.Length > 1 ? parts[1] : "");
    }
}
