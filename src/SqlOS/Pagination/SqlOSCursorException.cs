namespace SqlOS.Pagination;

/// <summary>
/// Typed failure for malformed, unsupported, or context-mismatched admin cursors.
/// Maps to HTTP 400 without exposing decoded cursor internals.
/// </summary>
public sealed class SqlOSCursorException : InvalidOperationException
{
    public const string ErrorCode = "invalid_cursor";

    public SqlOSCursorException(string message)
        : base(message)
    {
    }

    public string Error => ErrorCode;
}
