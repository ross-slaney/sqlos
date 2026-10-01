using SqlOS.Domain;
using SqlOS.Domain.Events;

namespace SqlOS.Fga.Models;

/// <summary>
/// Type of resource in the hierarchy (e.g., root, agency, team, project).
/// </summary>
public sealed class SqlOSFgaResourceType : ISqlOSAggregate
{
    private readonly DomainEventBuffer _events = new();

    private SqlOSFgaResourceType()
    {
    }

    public string Id { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }

    // Navigation
    public ICollection<SqlOSFgaResource> Resources { get; private set; } = new List<SqlOSFgaResource>();
    public ICollection<SqlOSFgaPermission> Permissions { get; private set; } = new List<SqlOSFgaPermission>();

    DomainEventBuffer ISqlOSAggregate.Events => _events;

    internal static SqlOSFgaResourceType Define(string id, string name, string? description = null)
    {
        var type = new SqlOSFgaResourceType { Id = id, Name = name, Description = description };
        type._events.Raise(new FgaResourceTypeDefined(id));
        return type;
    }

    internal void Redefine(string name, string? description)
    {
        if (Name == name && Description == description)
        {
            return;
        }

        (Name, Description) = (name, description);
        _events.Raise(new FgaResourceTypeDefined(Id));
    }
}
