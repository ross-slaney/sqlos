using SqlOS.Fga.Interfaces;

namespace SqlOS.Example.Api.FgaRetail.Models;

public class Chain : IHasResourceId
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ResourceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? HeadquartersAddress { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public ICollection<Location> Locations { get; set; } = new List<Location>();
}
