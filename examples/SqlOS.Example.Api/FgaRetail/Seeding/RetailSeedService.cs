using Microsoft.EntityFrameworkCore;
using SqlOS.Example.Api.Data;
using SqlOS.Example.Api.FgaRetail.Models;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;
using SqlOS.Fga.Services;

namespace SqlOS.Example.Api.FgaRetail.Seeding;

public class RetailSeedService
{
    private readonly ExampleAppDbContext _context;
    private readonly SqlOSFgaSeedService _seedService;
    private readonly ISqlOSFgaSubjectService _subjectService;

    // Well-known subject IDs for the example
    public const string CompanyAdminSubjectId = "subj_company_admin";
    public const string ChainManagerWalmartSubjectId = "subj_chain_mgr_walmart";
    public const string ChainManagerTargetSubjectId = "subj_chain_mgr_target";
    public const string StoreManager001SubjectId = "subj_store_mgr_001";
    public const string StoreManager002SubjectId = "subj_store_mgr_002";
    public const string StoreClerk001SubjectId = "subj_store_clerk_001";
    public const string NoGrantsSubjectId = "subj_no_grants";

    // User group: "Walmart Regional Managers" — members inherit ChainManager on Walmart
    public const string WalmartRegionalGroupSubjectId = "subj_walmart_regional_group";
    public const string WalmartRegionalGroupId = "grp_walmart_regional";
    public const string RegionalUserAliceSubjectId = "subj_regional_alice";
    public const string RegionalUserBobSubjectId = "subj_regional_bob";

    // Agent and service account subject IDs (set during seeding via CreateAgentAsync/CreateServiceAccountAsync)
    public static string? InventorySyncAgentSubjectId { get; private set; }
    public static string? ApiIntegrationServiceAccountSubjectId { get; private set; }

    // Well-known resource IDs
    public const string WalmartChainResourceId = "res_chain_walmart";
    public const string TargetChainResourceId = "res_chain_target";
    public const string CostcoChainResourceId = "res_chain_costco";
    public const string KrogerChainResourceId = "res_chain_kroger";
    public const string AldiChainResourceId = "res_chain_aldi";
    public const string Store001ResourceId = "res_location_001";
    public const string Store002ResourceId = "res_location_002";
    public const string Store100ResourceId = "res_location_100";

    public RetailSeedService(
        ExampleAppDbContext context,
        SqlOSFgaSeedService seedService,
        ISqlOSFgaSubjectService subjectService)
    {
        _context = context;
        _seedService = seedService;
        _subjectService = subjectService;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        // Check if already seeded
        if (await _context.Chains.AnyAsync(ct))
            return;

        // 1. Seed authorization schema
        await _seedService.SeedAuthorizationDataAsync(new SqlOSFgaSeedData
        {
            ResourceTypes =
            [
                new() { Id = RetailResourceTypeIds.Chain, Name = "Chain" },
                new() { Id = RetailResourceTypeIds.Location, Name = "Location" },
                new() { Id = RetailResourceTypeIds.InventoryItem, Name = "Inventory Item" },
            ],
            Roles =
            [
                new() { Id = "role_company_admin", Key = RetailRoleKeys.CompanyAdmin, Name = "Company Admin" },
                new() { Id = "role_chain_manager", Key = RetailRoleKeys.ChainManager, Name = "Chain Manager" },
                new() { Id = "role_store_manager", Key = RetailRoleKeys.StoreManager, Name = "Store Manager" },
                new() { Id = "role_store_clerk", Key = RetailRoleKeys.StoreClerk, Name = "Store Clerk" },
            ],
            Permissions =
            [
                new() { Id = "perm_chain_view", Key = RetailPermissionKeys.ChainView, Name = "View Chains" },
                new() { Id = "perm_chain_edit", Key = RetailPermissionKeys.ChainEdit, Name = "Edit Chains" },
                new() { Id = "perm_location_view", Key = RetailPermissionKeys.LocationView, Name = "View Locations" },
                new() { Id = "perm_location_edit", Key = RetailPermissionKeys.LocationEdit, Name = "Edit Locations" },
                new() { Id = "perm_inventory_view", Key = RetailPermissionKeys.InventoryView, Name = "View Inventory" },
                new() { Id = "perm_inventory_edit", Key = RetailPermissionKeys.InventoryEdit, Name = "Edit Inventory" },
            ],
            RolePermissions =
            [
                (RetailRoleKeys.CompanyAdmin, new[] { RetailPermissionKeys.ChainView, RetailPermissionKeys.ChainEdit, RetailPermissionKeys.LocationView, RetailPermissionKeys.LocationEdit, RetailPermissionKeys.InventoryView, RetailPermissionKeys.InventoryEdit }),
                (RetailRoleKeys.ChainManager, new[] { RetailPermissionKeys.ChainView, RetailPermissionKeys.LocationView, RetailPermissionKeys.LocationEdit, RetailPermissionKeys.InventoryView, RetailPermissionKeys.InventoryEdit }),
                (RetailRoleKeys.StoreManager, new[] { RetailPermissionKeys.LocationView, RetailPermissionKeys.LocationEdit, RetailPermissionKeys.InventoryView, RetailPermissionKeys.InventoryEdit }),
                (RetailRoleKeys.StoreClerk, new[] { RetailPermissionKeys.LocationView, RetailPermissionKeys.InventoryView }),
            ],
        }, ct);

        // 2. Create subjects (users + group subject)
        _context.Set<SqlOSFgaSubject>().AddRange(
            new SqlOSFgaSubject { Id = CompanyAdminSubjectId, SubjectTypeId = "user", DisplayName = "Company Admin" },
            new SqlOSFgaSubject { Id = ChainManagerWalmartSubjectId, SubjectTypeId = "user", DisplayName = "Walmart Chain Manager" },
            new SqlOSFgaSubject { Id = ChainManagerTargetSubjectId, SubjectTypeId = "user", DisplayName = "Target Chain Manager" },
            new SqlOSFgaSubject { Id = StoreManager001SubjectId, SubjectTypeId = "user", DisplayName = "Store 001 Manager" },
            new SqlOSFgaSubject { Id = StoreManager002SubjectId, SubjectTypeId = "user", DisplayName = "Store 002 Manager" },
            new SqlOSFgaSubject { Id = StoreClerk001SubjectId, SubjectTypeId = "user", DisplayName = "Store 001 Clerk" },
            new SqlOSFgaSubject { Id = NoGrantsSubjectId, SubjectTypeId = "user", DisplayName = "No Grants User" },
            // Group subject + group member users
            new SqlOSFgaSubject { Id = WalmartRegionalGroupSubjectId, SubjectTypeId = "group", DisplayName = "Walmart Regional Managers" },
            new SqlOSFgaSubject { Id = RegionalUserAliceSubjectId, SubjectTypeId = "user", DisplayName = "Alice (Regional)" },
            new SqlOSFgaSubject { Id = RegionalUserBobSubjectId, SubjectTypeId = "user", DisplayName = "Bob (Regional)" }
        );
        await _context.SaveChangesAsync(ct);

        // 2b. Create user group and memberships
        _context.Set<SqlOSFgaUserGroup>().Add(
            new SqlOSFgaUserGroup { Id = WalmartRegionalGroupId, Name = "Walmart Regional Managers", SubjectId = WalmartRegionalGroupSubjectId }
        );
        await _context.SaveChangesAsync(ct);

        _context.Set<SqlOSFgaUserGroupMembership>().AddRange(
            new SqlOSFgaUserGroupMembership { SubjectId = RegionalUserAliceSubjectId, UserGroupId = WalmartRegionalGroupId },
            new SqlOSFgaUserGroupMembership { SubjectId = RegionalUserBobSubjectId, UserGroupId = WalmartRegionalGroupId }
        );
        await _context.SaveChangesAsync(ct);

        // 2c. Add SqlOSFgaUser records for user subjects (for Users dashboard)
        _context.Set<SqlOSFgaUser>().AddRange(
            new SqlOSFgaUser { Id = "usr_company_admin", SubjectId = CompanyAdminSubjectId, Email = "admin@retail.example.com", IsActive = true },
            new SqlOSFgaUser { Id = "usr_chain_mgr_walmart", SubjectId = ChainManagerWalmartSubjectId, Email = "walmart.manager@retail.example.com", IsActive = true },
            new SqlOSFgaUser { Id = "usr_chain_mgr_target", SubjectId = ChainManagerTargetSubjectId, Email = "target.manager@retail.example.com", IsActive = true },
            new SqlOSFgaUser { Id = "usr_store_mgr_001", SubjectId = StoreManager001SubjectId, Email = "store001.mgr@retail.example.com", IsActive = true },
            new SqlOSFgaUser { Id = "usr_store_mgr_002", SubjectId = StoreManager002SubjectId, Email = "store002.mgr@retail.example.com", IsActive = true },
            new SqlOSFgaUser { Id = "usr_store_clerk_001", SubjectId = StoreClerk001SubjectId, Email = "store001.clerk@retail.example.com", IsActive = true },
            new SqlOSFgaUser { Id = "usr_no_grants", SubjectId = NoGrantsSubjectId, IsActive = true },
            new SqlOSFgaUser { Id = "usr_regional_alice", SubjectId = RegionalUserAliceSubjectId, Email = "alice@retail.example.com", IsActive = true },
            new SqlOSFgaUser { Id = "usr_regional_bob", SubjectId = RegionalUserBobSubjectId, Email = "bob@retail.example.com", IsActive = true }
        );
        await _context.SaveChangesAsync(ct);

        // 2d. Create agent and service account via service (demonstrates all 4 subject types)
        var inventorySyncAgent = await _subjectService.CreateAgentAsync(
            "Inventory Sync Agent",
            agentType: "background_job",
            description: "Nightly inventory synchronization");
        InventorySyncAgentSubjectId = inventorySyncAgent.SubjectId;

        var apiServiceAccount = await _subjectService.CreateServiceAccountAsync(
            "API Integration",
            clientId: "retail_api_client",
            clientSecretHash: "hashed_secret_placeholder",
            description: "External API integration service");
        ApiIntegrationServiceAccountSubjectId = apiServiceAccount.SubjectId;

        await _subjectService.AddToGroupAsync(inventorySyncAgent.SubjectId, WalmartRegionalGroupId);

        await _context.SaveChangesAsync(ct);

        // 3. Create resources in the hierarchy
        _context.Set<SqlOSFgaResource>().Add(new SqlOSFgaResource
        {
            Id = "retail_root",
            ParentId = "root",
            Name = "Retail Root",
            ResourceTypeId = "root"
        });
        await _context.SaveChangesAsync(ct);

        // Chains
        _context.Set<SqlOSFgaResource>().AddRange(
            new SqlOSFgaResource { Id = WalmartChainResourceId, ParentId = "retail_root", Name = "Walmart", ResourceTypeId = RetailResourceTypeIds.Chain },
            new SqlOSFgaResource { Id = TargetChainResourceId, ParentId = "retail_root", Name = "Target", ResourceTypeId = RetailResourceTypeIds.Chain },
            new SqlOSFgaResource { Id = CostcoChainResourceId, ParentId = "retail_root", Name = "Costco", ResourceTypeId = RetailResourceTypeIds.Chain },
            new SqlOSFgaResource { Id = KrogerChainResourceId, ParentId = "retail_root", Name = "Kroger", ResourceTypeId = RetailResourceTypeIds.Chain },
            new SqlOSFgaResource { Id = AldiChainResourceId, ParentId = "retail_root", Name = "Aldi", ResourceTypeId = RetailResourceTypeIds.Chain }
        );
        await _context.SaveChangesAsync(ct);

        // Locations
        _context.Set<SqlOSFgaResource>().AddRange(
            new SqlOSFgaResource { Id = Store001ResourceId, ParentId = WalmartChainResourceId, Name = "Store 001", ResourceTypeId = RetailResourceTypeIds.Location },
            new SqlOSFgaResource { Id = Store002ResourceId, ParentId = WalmartChainResourceId, Name = "Store 002", ResourceTypeId = RetailResourceTypeIds.Location },
            new SqlOSFgaResource { Id = Store100ResourceId, ParentId = TargetChainResourceId, Name = "Store 100", ResourceTypeId = RetailResourceTypeIds.Location }
        );
        await _context.SaveChangesAsync(ct);

        // Inventory item resources
        var laptopResourceId = "res_inv_laptop";
        var phoneResourceId = "res_inv_phone";
        var tabletResourceId = "res_inv_tablet";
        var headphonesResourceId = "res_inv_headphones";
        _context.Set<SqlOSFgaResource>().AddRange(
            new SqlOSFgaResource { Id = laptopResourceId, ParentId = Store001ResourceId, Name = "Laptop", ResourceTypeId = RetailResourceTypeIds.InventoryItem },
            new SqlOSFgaResource { Id = phoneResourceId, ParentId = Store001ResourceId, Name = "Phone", ResourceTypeId = RetailResourceTypeIds.InventoryItem },
            new SqlOSFgaResource { Id = tabletResourceId, ParentId = Store002ResourceId, Name = "Tablet", ResourceTypeId = RetailResourceTypeIds.InventoryItem },
            new SqlOSFgaResource { Id = headphonesResourceId, ParentId = Store100ResourceId, Name = "Headphones", ResourceTypeId = RetailResourceTypeIds.InventoryItem }
        );
        await _context.SaveChangesAsync(ct);

        // 4. Create domain entities paired with their resources
        var walmartChain = new Chain
        {
            Id = "chain_walmart",
            ResourceId = WalmartChainResourceId,
            Name = "Walmart",
            Description = "Walmart Inc.",
            HeadquartersAddress = "702 SW 8th St, Bentonville, AR 72716"
        };
        var targetChain = new Chain
        {
            Id = "chain_target",
            ResourceId = TargetChainResourceId,
            Name = "Target",
            Description = "Target Corporation",
            HeadquartersAddress = "1000 Nicollet Mall, Minneapolis, MN 55403"
        };
        var costcoChain = new Chain
        {
            Id = "chain_costco",
            ResourceId = CostcoChainResourceId,
            Name = "Costco",
            Description = "Costco Wholesale Corporation",
            HeadquartersAddress = "999 Lake Dr, Issaquah, WA 98027"
        };
        var krogerChain = new Chain
        {
            Id = "chain_kroger",
            ResourceId = KrogerChainResourceId,
            Name = "Kroger",
            Description = "The Kroger Co.",
            HeadquartersAddress = "1014 Vine St, Cincinnati, OH 45202"
        };
        var aldiChain = new Chain
        {
            Id = "chain_aldi",
            ResourceId = AldiChainResourceId,
            Name = "Aldi",
            Description = "Aldi Inc.",
            HeadquartersAddress = "1200 N Kirk Rd, Batavia, IL 60510"
        };
        _context.Chains.AddRange(walmartChain, targetChain, costcoChain, krogerChain, aldiChain);

        var store001 = new Location
        {
            Id = "loc_001",
            ResourceId = Store001ResourceId,
            ChainId = walmartChain.Id,
            Name = "Walmart Supercenter #001",
            StoreNumber = "001",
            Address = "123 Main St",
            City = "Springfield",
            State = "MO",
            ZipCode = "65801"
        };
        var store002 = new Location
        {
            Id = "loc_002",
            ResourceId = Store002ResourceId,
            ChainId = walmartChain.Id,
            Name = "Walmart Neighborhood Market #002",
            StoreNumber = "002",
            Address = "456 Oak Ave",
            City = "Joplin",
            State = "MO",
            ZipCode = "64801"
        };
        var store100 = new Location
        {
            Id = "loc_100",
            ResourceId = Store100ResourceId,
            ChainId = targetChain.Id,
            Name = "Target Store #100",
            StoreNumber = "100",
            Address = "789 Elm Blvd",
            City = "Minneapolis",
            State = "MN",
            ZipCode = "55401"
        };
        _context.Locations.AddRange(store001, store002, store100);

        _context.InventoryItems.AddRange(
            new InventoryItem { Id = "inv_laptop", ResourceId = laptopResourceId, LocationId = store001.Id, Sku = "ELEC-LAPTOP-001", Name = "ProBook Laptop", Description = "15-inch business laptop", Price = 799.99m, QuantityOnHand = 25 },
            new InventoryItem { Id = "inv_phone", ResourceId = phoneResourceId, LocationId = store001.Id, Sku = "ELEC-PHONE-001", Name = "SmartPhone X", Description = "Latest smartphone model", Price = 999.99m, QuantityOnHand = 50 },
            new InventoryItem { Id = "inv_tablet", ResourceId = tabletResourceId, LocationId = store002.Id, Sku = "ELEC-TABLET-001", Name = "TabPro 11", Description = "11-inch tablet", Price = 499.99m, QuantityOnHand = 30 },
            new InventoryItem { Id = "inv_headphones", ResourceId = headphonesResourceId, LocationId = store100.Id, Sku = "AUDIO-HP-001", Name = "BassMax Headphones", Description = "Noise-canceling headphones", Price = 149.99m, QuantityOnHand = 75 }
        );

        await _context.SaveChangesAsync(ct);

        // 5. Create grants
        var companyAdminRole = await _context.Set<SqlOSFgaRole>().FirstAsync(r => r.Key == RetailRoleKeys.CompanyAdmin, ct);
        var chainManagerRole = await _context.Set<SqlOSFgaRole>().FirstAsync(r => r.Key == RetailRoleKeys.ChainManager, ct);
        var storeManagerRole = await _context.Set<SqlOSFgaRole>().FirstAsync(r => r.Key == RetailRoleKeys.StoreManager, ct);
        var storeClerkRole = await _context.Set<SqlOSFgaRole>().FirstAsync(r => r.Key == RetailRoleKeys.StoreClerk, ct);

        _context.Set<SqlOSFgaGrant>().AddRange(
            new SqlOSFgaGrant { Id = "grant_company_admin", SubjectId = CompanyAdminSubjectId, ResourceId = "retail_root", RoleId = companyAdminRole.Id },
            new SqlOSFgaGrant { Id = "grant_chain_mgr_walmart", SubjectId = ChainManagerWalmartSubjectId, ResourceId = WalmartChainResourceId, RoleId = chainManagerRole.Id },
            new SqlOSFgaGrant { Id = "grant_chain_mgr_target", SubjectId = ChainManagerTargetSubjectId, ResourceId = TargetChainResourceId, RoleId = chainManagerRole.Id },
            new SqlOSFgaGrant { Id = "grant_store_mgr_001", SubjectId = StoreManager001SubjectId, ResourceId = Store001ResourceId, RoleId = storeManagerRole.Id },
            new SqlOSFgaGrant { Id = "grant_store_mgr_002", SubjectId = StoreManager002SubjectId, ResourceId = Store002ResourceId, RoleId = storeManagerRole.Id },
            new SqlOSFgaGrant { Id = "grant_store_clerk_001", SubjectId = StoreClerk001SubjectId, ResourceId = Store001ResourceId, RoleId = storeClerkRole.Id },
            // Group grant: Walmart Regional Managers group gets ChainManager on Walmart chain
            new SqlOSFgaGrant { Id = "grant_walmart_regional_group", SubjectId = WalmartRegionalGroupSubjectId, ResourceId = WalmartChainResourceId, RoleId = chainManagerRole.Id },
            // Agent grant: Inventory Sync Agent gets StoreManager on Walmart (also inherits via group)
            new SqlOSFgaGrant { Id = "grant_inventory_sync", SubjectId = InventorySyncAgentSubjectId!, ResourceId = WalmartChainResourceId, RoleId = storeManagerRole.Id },
            // Service account grant: API Integration gets read-only (StoreClerk) at root
            new SqlOSFgaGrant { Id = "grant_api_integration", SubjectId = ApiIntegrationServiceAccountSubjectId!, ResourceId = "retail_root", RoleId = storeClerkRole.Id }
            // NoGrantsSubjectId, RegionalUserAlice, RegionalUserBob have no direct grants
            // — Alice and Bob inherit access via their group membership
        );

        await _context.SaveChangesAsync(ct);
    }
}
