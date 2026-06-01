using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Interfaces;
using Twilio.Clients;
using Twilio.Exceptions;
using Twilio.Rest.Verify.V2.Service;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSTwilioVerifyOtpChannel : ISqlOSOtpDeliveryChannel
{
    private const string ProviderName = "twilio_verify";
    private readonly SqlOSPhoneOtpOptions _options;

    public SqlOSTwilioVerifyOtpChannel(IOptions<SqlOSAuthServerOptions> options)
    {
        _options = options.Value.PhoneOtp;
    }

    public async Task<SqlOSOtpDeliveryStartResult> StartAsync(
        string e164PhoneNumber,
        SqlOSOtpDeliveryContext context,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        try
        {
            var request = new CreateVerificationOptions(
                _options.TwilioVerifyServiceSid!.Trim(),
                e164PhoneNumber,
                "sms");

            var verification = await VerificationResource.CreateAsync(request, CreateClient());
            return new SqlOSOtpDeliveryStartResult(
                Accepted: true,
                ProviderName,
                verification.Sid,
                verification.Status?.ToString());
        }
        catch (ApiException ex)
        {
            return new SqlOSOtpDeliveryStartResult(
                Accepted: false,
                ProviderName,
                ProviderChallengeId: null,
                ProviderStatus: ex.Code.ToString(),
                SanitizedError: "Twilio Verify rejected the send request.");
        }
    }

    public async Task<SqlOSOtpDeliveryCheckResult> CheckAsync(
        string e164PhoneNumber,
        string code,
        SqlOSOtpDeliveryContext context,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        try
        {
            var request = new CreateVerificationCheckOptions(_options.TwilioVerifyServiceSid!.Trim())
            {
                To = e164PhoneNumber,
                Code = code
            };

            var check = await VerificationCheckResource.CreateAsync(request, CreateClient());
            var approved = check.Valid == true
                || string.Equals(check.Status?.ToString(), "approved", StringComparison.OrdinalIgnoreCase);
            return new SqlOSOtpDeliveryCheckResult(
                approved,
                ProviderName,
                check.Sid,
                check.Status?.ToString());
        }
        catch (ApiException ex)
        {
            return new SqlOSOtpDeliveryCheckResult(
                Approved: false,
                ProviderName,
                ProviderChallengeId: null,
                ProviderStatus: ex.Code.ToString(),
                SanitizedError: "Twilio Verify rejected the check request.");
        }
    }

    private ITwilioRestClient CreateClient()
    {
        var accountSid = _options.TwilioAccountSid!.Trim();
        return new TwilioRestClient(
            accountSid,
            _options.TwilioAuthToken!.Trim(),
            accountSid);
    }

    private void EnsureConfigured()
    {
        if (!_options.IsConfigured)
        {
            throw new InvalidOperationException("Phone OTP is enabled but Twilio Verify is not configured.");
        }
    }
}
