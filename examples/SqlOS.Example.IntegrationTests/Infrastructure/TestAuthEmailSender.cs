using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using SqlOS.AuthServer.Interfaces;

namespace SqlOS.Example.IntegrationTests.Infrastructure;

public sealed class TestAuthEmailSender : ISqlOSAuthEmailSender
{
    private static readonly Regex CodeRegex = new(@"\b\d{4,8}\b", RegexOptions.Compiled);
    private readonly ConcurrentQueue<SqlOSAuthEmailMessage> _messages = new();

    public bool IsConfigured => true;

    public Task SendAsync(SqlOSAuthEmailMessage message, CancellationToken cancellationToken = default)
    {
        _messages.Enqueue(message);
        return Task.CompletedTask;
    }

    public string GetLatestCode(string email)
    {
        var match = _messages
            .Where(message => string.Equals(message.To, email, StringComparison.OrdinalIgnoreCase))
            .Select(message => CodeRegex.Match(message.TextBody ?? message.HtmlBody))
            .LastOrDefault(result => result.Success);

        if (match == null || !match.Success)
        {
            throw new InvalidOperationException($"No OTP email was captured for '{email}'.");
        }

        return match.Value;
    }
}
