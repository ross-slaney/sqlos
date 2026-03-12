namespace SqlOS.Example.Api.FgaRetail.Dtos;

public class LocationDto
{
    public string Id { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string ChainId { get; set; } = string.Empty;
    public string? ChainName { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? StoreNumber { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class LocationDetailDto
{
    public string Id { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string ChainId { get; set; } = string.Empty;
    public string? ChainName { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? StoreNumber { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? ZipCode { get; set; }
    public int InventoryItemCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class CreateLocationRequest
{
    public string Name { get; set; } = string.Empty;
    public string? StoreNumber { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? ZipCode { get; set; }
}

public class UpdateLocationRequest
{
    public string Name { get; set; } = string.Empty;
    public string? StoreNumber { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? ZipCode { get; set; }
}
