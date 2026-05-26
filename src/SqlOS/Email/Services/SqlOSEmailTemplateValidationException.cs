namespace SqlOS.Email.Services;

public sealed class SqlOSEmailTemplateValidationException : InvalidOperationException
{
    public SqlOSEmailTemplateValidationException(IReadOnlyList<string> missingVariables)
        : base($"Missing email template variables: {string.Join(", ", missingVariables)}.")
    {
        MissingVariables = missingVariables;
    }

    public IReadOnlyList<string> MissingVariables { get; }
}
