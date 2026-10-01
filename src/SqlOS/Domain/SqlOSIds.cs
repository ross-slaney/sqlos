namespace SqlOS.Domain;

/// <summary>
/// Generates SqlOS entity IDs in the one format every 7.x ID has: the prefix, an underscore, then
/// the first 24 lowercase hex characters of a random (version 4) GUID, for example
/// <c>usr_3f2b8c1d9e7a4b60a1c2d3e4</c>.
/// </summary>
/// <remarks>
/// <c>SqlOSCryptoService.GenerateId</c> delegates here. Hosts store and parse these IDs, so the
/// format is part of the public contract and must not change.
/// </remarks>
internal static class SqlOSIds
{
    /// <summary>The number of random hex characters after the underscore.</summary>
    public const int RandomLength = 24;

    public static string New(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        return $"{prefix}_{Guid.NewGuid():N}"[..(prefix.Length + 1 + RandomLength)];
    }
}
