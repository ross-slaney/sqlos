namespace SqlOS.Fga.Models;

/// <summary>
/// A live subject of the caller, as <c>fn_ActiveSubjects</c> returns it. <c>BuildFilterAsync</c> adds one
/// <c>EXISTS</c> over this set to the filter, evaluated once per query, so a caller deactivated after the
/// filter was built sees nothing when the query runs. Not mapped to a table.
/// </summary>
internal sealed class SqlOSFgaActiveSubject
{
    public string SubjectId { get; set; } = string.Empty;
}
