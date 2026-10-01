namespace SqlOS.Fga.Models;

/// <summary>
/// One page of entities a subject may see, in resource creation order, from
/// <see cref="Interfaces.ISqlOSFgaAuthService.ListVisibleAsync{T}"/>.
/// </summary>
/// <typeparam name="T">The entity type.</typeparam>
/// <param name="Items">The entities, oldest resource first.</param>
/// <param name="NextCursor">
/// The cursor for the next page, or <see langword="null"/> when this page ended the list. Pass it back
/// unchanged; it is opaque.
/// </param>
public sealed record SqlOSFgaVisiblePage<T>(IReadOnlyList<T> Items, string? NextCursor)
{
    /// <summary>Whether a further page may exist.</summary>
    public bool HasMore => NextCursor is not null;
}
