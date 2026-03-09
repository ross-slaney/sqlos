using System.Globalization;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using SqlOS.Example.Api.FgaRetail.Models;
using SqlOS.Example.Api.FgaRetail.Seeding;
using SqlOS.Fga.Specifications;

namespace SqlOS.Example.Api.FgaRetail.Specifications;

public class GetInventoryItemsSpecification : SortablePagedSpecification<InventoryItem>
{
    public GetInventoryItemsSpecification(
        int pageSize, string? search = null, string? locationId = null,
        string? sortBy = null, bool descending = false)
    {
        PageSize = pageSize;

        RegisterStringSort("name", i => i.Name, isDefault: true);
        RegisterStringSort("sku", i => i.Sku);
        RegisterSort("price", i => i.Price,
            serialize: v => v.ToString(CultureInfo.InvariantCulture),
            deserialize: s => decimal.Parse(s, CultureInfo.InvariantCulture));

        SetActiveSort(sortBy, descending);

        if (locationId != null)
            AddFilter(i => i.LocationId == locationId);

        Search(search, i => i.Name, i => i.Sku);
    }

    public override string? RequiredPermission => RetailPermissionKeys.InventoryView;
    protected override Expression<Func<InventoryItem, string>> IdSelector => i => i.Id;

    public override IQueryable<InventoryItem> ConfigureQuery(IQueryable<InventoryItem> query)
        => query.Include(i => i.Location);
}
