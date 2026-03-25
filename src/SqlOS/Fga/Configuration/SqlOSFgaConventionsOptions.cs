using SqlOS.Fga.Interfaces;

namespace SqlOS.Fga.Configuration;

/// <summary>
/// Options that control how SqlOS FGA conventions are applied to consumer entities.
/// </summary>
public class SqlOSFgaConventionsOptions
{
    /// <summary>
    /// When <c>true</c> (the default), a database index on <c>ResourceId</c> is automatically
    /// added to every entity implementing <see cref="IHasResourceId"/> that does not already
    /// have a compatible index (i.e. an index whose leading column is <c>ResourceId</c>).
    /// Set to <c>false</c> to take full manual control of index configuration.
    /// </summary>
    public bool AutoIndexResourceIds { get; set; } = true;

    /// <summary>
    /// When <c>true</c>, <see cref="SqlOS.Fga.Extensions.ModelBuilderExtensions.ApplySqlOSFgaConventions"/>
    /// throws an <see cref="InvalidOperationException"/> for every entity implementing
    /// <see cref="IHasResourceId"/> that still has no index on <c>ResourceId</c> after conventions
    /// have been applied.
    /// Defaults to <c>false</c>.
    /// </summary>
    public bool ValidateResourceIdIndexes { get; set; } = false;
}
