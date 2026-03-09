using SqlOS.Fga.Interfaces;

namespace SqlOS.Example.Api.FgaRetail.Models;

public class Location : IHasResourceId
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ResourceId { get; set; } = string.Empty;
    public string ChainId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? StoreNumber { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? ZipCode { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Chain? Chain { get; set; }
    public ICollection<InventoryItem> InventoryItems { get; set; } = new List<InventoryItem>();
}
