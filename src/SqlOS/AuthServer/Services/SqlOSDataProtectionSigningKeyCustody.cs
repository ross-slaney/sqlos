using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Tokens;
using SqlOS.AuthServer.Interfaces;

namespace SqlOS.AuthServer.Services;

/// <summary>
/// Built-in signing-key custody backed by a dedicated ASP.NET Core Data Protection purpose.
/// Private PKCS#8 material exists only transiently in process memory and is persisted solely as a
/// Data Protection ciphertext in the SqlOS signing-key row.
/// </summary>
internal sealed class SqlOSDataProtectionSigningKeyCustody : ISqlOSSigningKeyCustody
{
    public const string DataProtectionProviderId = "aspnet-data-protection:v1";
    private const string KeyReferencePrefix = "sqlos-dp-signing:v1:";
    private readonly IDataProtectionProvider? _dataProtectionProvider;

    public SqlOSDataProtectionSigningKeyCustody(
        IDataProtectionProvider? dataProtectionProvider = null)
    {
        _dataProtectionProvider = dataProtectionProvider;
    }

    public string ProviderId => DataProtectionProviderId;

    public Task<SqlOSSigningKeyCreationResult> CreateKeyAsync(
        string kid,
        string algorithm,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireReady();
        RequireKid(kid);
        RequireRs256(algorithm);

        using var rsa = RSA.Create(3072);
        var privateKeyBytes = rsa.ExportPkcs8PrivateKey();
        try
        {
            var protectedKeyBytes = CreateKeyProtector(kid).Protect(privateKeyBytes);
            var keyReference = $"{KeyReferencePrefix}{Base64UrlEncoder.Encode(protectedKeyBytes)}";
            return Task.FromResult(new SqlOSSigningKeyCreationResult(
                SecurityAlgorithms.RsaSha256,
                rsa.ExportRSAPublicKeyPem(),
                keyReference));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKeyBytes);
        }
    }

    public Task<byte[]> SignAsync(
        SqlOSSigningKeyDescriptor key,
        ReadOnlyMemory<byte> signingInput,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireReady();
        RequireDescriptor(key);

        byte[]? privateKeyBytes = null;
        try
        {
            var protectedKeyBytes = Base64UrlEncoder.DecodeBytes(
                key.KeyReference[KeyReferencePrefix.Length..]);
            privateKeyBytes = CreateKeyProtector(key.Kid).Unprotect(protectedKeyBytes);
            using var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(privateKeyBytes, out var bytesRead);
            if (bytesRead != privateKeyBytes.Length)
            {
                throw new CryptographicException("The signing-key payload contains trailing data.");
            }

            return Task.FromResult(rsa.SignData(
                signingInput.Span,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1));
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
        {
            throw new InvalidOperationException(
                "The active SqlOS signing key cannot be opened by this application instance. " +
                "Refusing to rotate or issue tokens. Verify that every instance uses the same persisted Data Protection key ring.",
                ex);
        }
        finally
        {
            if (privateKeyBytes != null)
            {
                CryptographicOperations.ZeroMemory(privateKeyBytes);
            }
        }
    }

    public Task DeleteKeyAsync(
        SqlOSSigningKeyDescriptor key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireDescriptor(key);
        return Task.CompletedTask;
    }

    private void RequireReady()
    {
        if (_dataProtectionProvider == null)
        {
            throw new InvalidOperationException(
                "SqlOS signing-key custody requires the ASP.NET Core Data Protection services registered by AddSqlOS.");
        }
    }

    private IDataProtector CreateKeyProtector(string kid)
        => _dataProtectionProvider!
            .CreateProtector("SqlOS.AuthServer.SigningKeys.v1")
            .CreateProtector(kid);

    private void RequireDescriptor(SqlOSSigningKeyDescriptor key)
    {
        RequireKid(key.Kid);

        if (!string.Equals(key.CustodyProvider, ProviderId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Signing key '{key.Kid}' belongs to custody provider '{key.CustodyProvider}', not '{ProviderId}'.");
        }

        RequireRs256(key.Algorithm);
        if (!key.KeyReference.StartsWith(KeyReferencePrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Signing key '{key.Kid}' does not contain a valid Data Protection custody reference.");
        }
    }

    private static void RequireRs256(string algorithm)
    {
        if (!string.Equals(algorithm, SecurityAlgorithms.RsaSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("SqlOS Data Protection signing-key custody supports RS256 only.");
        }
    }

    private static void RequireKid(string kid)
    {
        if (string.IsNullOrWhiteSpace(kid))
        {
            throw new InvalidOperationException("A signing-key custody descriptor must include a key ID.");
        }
    }
}
