namespace SqlOS.Example.Api.FgaRetail.Dtos;

public class ChainDto
{
    public string Id { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int LocationCount { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ChainDetailDto
{
    public string Id { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? HeadquartersAddress { get; set; }
    public int LocationCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class CreateChainRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? HeadquartersAddress { get; set; }
}
