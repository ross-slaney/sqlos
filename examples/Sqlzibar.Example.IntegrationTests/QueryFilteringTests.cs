using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sqlzibar.Example.IntegrationTests.Infrastructure;

namespace Sqlzibar.Example.IntegrationTests;

/// <summary>
/// Tests that validate TVF-based list filtering via the specification executor.
/// Each test arranges a specific subject (with known grants at specific hierarchy levels),
/// calls a list endpoint, and asserts that only authorized data is returned.
///
/// "Has access" tests use HaveCountGreaterThanOrEqualTo + Contain to verify expected seeded items
/// are present (resilient to items created by other test classes on the shared DB).
/// "No access" tests use BeEmpty — these are the critical security regression tests
/// ensuring the TVF never leaks unauthorized data.
///
/// Resource hierarchy:
///   retail_root (CompanyAdmin)
///     ├── res_chain_walmart (ChainManager Walmart)
///     │     ├── res_location_001 (StoreManager 001, StoreClerk 001)
///     │     │     ├── inv_laptop  (ProBook Laptop, SKU: ELEC-LAPTOP-001)
///     │     │     └── inv_phone   (SmartPhone X, SKU: ELEC-PHONE-001)
///     │     └── res_location_002 (StoreManager 002)
///     │           └── inv_tablet  (TabPro 11, SKU: ELEC-TABLET-001)
///     ├── res_chain_target (ChainManager Target)
///     │     └── res_location_100
///     │           └── inv_headphones (BassMax Headphones, SKU: AUDIO-HP-001)
///     ├── res_chain_costco
///     ├── res_chain_kroger
///     └── res_chain_aldi
/// </summary>
[TestClass]
public class QueryFilteringTests
{
    private HttpClient _client = null!;

    // Subjects
    private const string CompanyAdmin = "subj_company_admin";
    private const string ChainMgrWalmart = "subj_chain_mgr_walmart";
    private const string ChainMgrTarget = "subj_chain_mgr_target";
    private const string StoreMgr001 = "subj_store_mgr_001";
    private const string StoreMgr002 = "subj_store_mgr_002";
    private const string StoreClerk001 = "subj_store_clerk_001";
    private const string NoGrants = "subj_no_grants";

    [TestInitialize]
    public void TestInit()
    {
        _client = ExampleApiFixture.Client;
    }

    // ====================================================================
    // CHAIN LIST FILTERING (requires CHAIN_VIEW)
    // CompanyAdmin: all roles. ChainManager: has CHAIN_VIEW at their chain.
    // StoreManager/StoreClerk: no CHAIN_VIEW. NoGrants: nothing.
    // ====================================================================

    [TestMethod]
    public async Task Chains_CompanyAdmin_SeesAllChains()
    {
        var result = await GetChainsAsync(CompanyAdmin);

        result.Data.Should().HaveCountGreaterThanOrEqualTo(2);
        result.Data.Select(c => c.Name).Should().Contain("Walmart").And.Contain("Target");
    }

    [TestMethod]
    public async Task Chains_ChainManagerWalmart_SeesOnlyWalmart()
    {
        var result = await GetChainsAsync(ChainMgrWalmart);

        result.Data.Should().ContainSingle()
            .Which.Name.Should().Be("Walmart");
    }

    [TestMethod]
    public async Task Chains_ChainManagerTarget_SeesOnlyTarget()
    {
        var result = await GetChainsAsync(ChainMgrTarget);

        result.Data.Should().ContainSingle()
            .Which.Name.Should().Be("Target");
    }

    [TestMethod]
    public async Task Chains_StoreManager_SeesNoChains()
    {
        // StoreManager role has no CHAIN_VIEW permission
        var result = await GetChainsAsync(StoreMgr001);

        result.Data.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Chains_StoreClerk_SeesNoChains()
    {
        var result = await GetChainsAsync(StoreClerk001);

        result.Data.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Chains_NoGrants_SeesNoChains()
    {
        var result = await GetChainsAsync(NoGrants);

        result.Data.Should().BeEmpty();
    }

    // ====================================================================
    // LOCATION LIST FILTERING (requires LOCATION_VIEW)
    // ====================================================================

    [TestMethod]
    public async Task Locations_CompanyAdmin_SeesAllWalmartLocations()
    {
        var result = await GetLocationsAsync(CompanyAdmin, "chain_walmart");

        result.Data.Should().HaveCountGreaterThanOrEqualTo(2);
        result.Data.Select(l => l.StoreNumber).Should().Contain("001").And.Contain("002");
    }

    [TestMethod]
    public async Task Locations_CompanyAdmin_SeesAllTargetLocations()
    {
        var result = await GetLocationsAsync(CompanyAdmin, "chain_target");

        result.Data.Should().HaveCountGreaterThanOrEqualTo(1);
        result.Data.Select(l => l.StoreNumber).Should().Contain("100");
    }

    [TestMethod]
    public async Task Locations_ChainManagerWalmart_SeesWalmartLocations()
    {
        var result = await GetLocationsAsync(ChainMgrWalmart, "chain_walmart");

        result.Data.Should().HaveCountGreaterThanOrEqualTo(2);
        result.Data.Select(l => l.StoreNumber).Should().Contain("001").And.Contain("002");
    }

    [TestMethod]
    public async Task Locations_ChainManagerWalmart_SeesNoTargetLocations()
    {
        var result = await GetLocationsAsync(ChainMgrWalmart, "chain_target");

        result.Data.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Locations_ChainManagerTarget_SeesTargetLocations()
    {
        var result = await GetLocationsAsync(ChainMgrTarget, "chain_target");

        result.Data.Should().HaveCountGreaterThanOrEqualTo(1);
        result.Data.Select(l => l.StoreNumber).Should().Contain("100");
    }

    [TestMethod]
    public async Task Locations_ChainManagerTarget_SeesNoWalmartLocations()
    {
        var result = await GetLocationsAsync(ChainMgrTarget, "chain_walmart");

        result.Data.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Locations_StoreManager001_SeesOnlyTheirStore()
    {
        var result = await GetLocationsAsync(StoreMgr001, "chain_walmart");

        result.Data.Should().ContainSingle()
            .Which.StoreNumber.Should().Be("001");
    }

    [TestMethod]
    public async Task Locations_StoreManager002_SeesOnlyTheirStore()
    {
        var result = await GetLocationsAsync(StoreMgr002, "chain_walmart");

        result.Data.Should().ContainSingle()
            .Which.StoreNumber.Should().Be("002");
    }

    [TestMethod]
    public async Task Locations_StoreClerk001_SeesOnlyTheirStore()
    {
        var result = await GetLocationsAsync(StoreClerk001, "chain_walmart");

        result.Data.Should().ContainSingle()
            .Which.StoreNumber.Should().Be("001");
    }

    [TestMethod]
    public async Task Locations_NoGrants_SeesNoLocations()
    {
        var result = await GetLocationsAsync(NoGrants, "chain_walmart");

        result.Data.Should().BeEmpty();
    }

    // ====================================================================
    // INVENTORY LIST FILTERING (requires INVENTORY_VIEW)
    // ====================================================================

    [TestMethod]
    public async Task Inventory_CompanyAdmin_SeesStore001Items()
    {
        var result = await GetInventoryAsync(CompanyAdmin, "loc_001");

        result.Data.Should().HaveCountGreaterThanOrEqualTo(2);
        var names = result.Data.Select(i => i.Name);
        names.Should().Contain("ProBook Laptop").And.Contain("SmartPhone X");
    }

    [TestMethod]
    public async Task Inventory_CompanyAdmin_SeesStore002Items()
    {
        var result = await GetInventoryAsync(CompanyAdmin, "loc_002");

        result.Data.Should().HaveCountGreaterThanOrEqualTo(1);
        result.Data.Select(i => i.Name).Should().Contain("TabPro 11");
    }

    [TestMethod]
    public async Task Inventory_CompanyAdmin_SeesStore100Items()
    {
        var result = await GetInventoryAsync(CompanyAdmin, "loc_100");

        result.Data.Should().HaveCountGreaterThanOrEqualTo(1);
        result.Data.Select(i => i.Name).Should().Contain("BassMax Headphones");
    }

    [TestMethod]
    public async Task Inventory_ChainManagerWalmart_SeesStore001Items()
    {
        var result = await GetInventoryAsync(ChainMgrWalmart, "loc_001");

        result.Data.Should().HaveCountGreaterThanOrEqualTo(2);
        var names = result.Data.Select(i => i.Name);
        names.Should().Contain("ProBook Laptop").And.Contain("SmartPhone X");
    }

    [TestMethod]
    public async Task Inventory_ChainManagerWalmart_SeesStore002Items()
    {
        var result = await GetInventoryAsync(ChainMgrWalmart, "loc_002");

        result.Data.Should().HaveCountGreaterThanOrEqualTo(1);
        result.Data.Select(i => i.Name).Should().Contain("TabPro 11");
    }

    [TestMethod]
    public async Task Inventory_ChainManagerWalmart_SeesNoTargetStoreItems()
    {
        var result = await GetInventoryAsync(ChainMgrWalmart, "loc_100");

        result.Data.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Inventory_ChainManagerTarget_SeesStore100Items()
    {
        var result = await GetInventoryAsync(ChainMgrTarget, "loc_100");

        result.Data.Should().HaveCountGreaterThanOrEqualTo(1);
        result.Data.Select(i => i.Name).Should().Contain("BassMax Headphones");
    }

    [TestMethod]
    public async Task Inventory_ChainManagerTarget_SeesNoWalmartStoreItems()
    {
        var result = await GetInventoryAsync(ChainMgrTarget, "loc_001");

        result.Data.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Inventory_StoreManager001_SeesTheirStoreItems()
    {
        var result = await GetInventoryAsync(StoreMgr001, "loc_001");

        result.Data.Should().HaveCountGreaterThanOrEqualTo(2);
        var names = result.Data.Select(i => i.Name);
        names.Should().Contain("ProBook Laptop").And.Contain("SmartPhone X");
    }

    [TestMethod]
    public async Task Inventory_StoreManager001_SeesNoStore002Items()
    {
        var result = await GetInventoryAsync(StoreMgr001, "loc_002");

        result.Data.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Inventory_StoreManager002_SeesTheirStoreItems()
    {
        var result = await GetInventoryAsync(StoreMgr002, "loc_002");

        result.Data.Should().HaveCountGreaterThanOrEqualTo(1);
        result.Data.Select(i => i.Name).Should().Contain("TabPro 11");
    }

    [TestMethod]
    public async Task Inventory_StoreManager002_SeesNoStore001Items()
    {
        var result = await GetInventoryAsync(StoreMgr002, "loc_001");

        result.Data.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Inventory_StoreClerk001_SeesTheirStoreItems()
    {
        var result = await GetInventoryAsync(StoreClerk001, "loc_001");

        result.Data.Should().HaveCountGreaterThanOrEqualTo(2);
        var names = result.Data.Select(i => i.Name);
        names.Should().Contain("ProBook Laptop").And.Contain("SmartPhone X");
    }

    [TestMethod]
    public async Task Inventory_StoreClerk001_SeesNoStore002Items()
    {
        var result = await GetInventoryAsync(StoreClerk001, "loc_002");

        result.Data.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Inventory_NoGrants_SeesNoItems()
    {
        var result = await GetInventoryAsync(NoGrants, "loc_001");

        result.Data.Should().BeEmpty();
    }

    // ====================================================================
    // SEARCH + AUTH FILTERING COMBINED
    // Validates that search narrows within the auth-filtered set, and
    // auth filtering doesn't leak data even when search would match.
    // ====================================================================

    [TestMethod]
    public async Task Search_CompanyAdmin_SearchLaptopInStore001_Finds1()
    {
        var result = await GetInventoryAsync(CompanyAdmin, "loc_001", search: "laptop");

        result.Data.Should().HaveCountGreaterThanOrEqualTo(1);
        result.Data.Select(i => i.Name).Should().Contain("ProBook Laptop");
    }

    [TestMethod]
    public async Task Search_StoreClerk_SearchLaptopInOwnStore_Finds1()
    {
        var result = await GetInventoryAsync(StoreClerk001, "loc_001", search: "laptop");

        result.Data.Should().HaveCountGreaterThanOrEqualTo(1);
        result.Data.Select(i => i.Name).Should().Contain("ProBook Laptop");
    }

    [TestMethod]
    public async Task Search_StoreClerk_SearchTabletInStore002_FindsNothing()
    {
        // Tablet exists in store 002 but StoreClerk 001 has no access to store 002
        var result = await GetInventoryAsync(StoreClerk001, "loc_002", search: "tablet");

        result.Data.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Search_ChainManagerWalmart_SearchWalmart_Finds1Chain()
    {
        var result = await GetChainsAsync(ChainMgrWalmart, search: "walmart");

        result.Data.Should().ContainSingle()
            .Which.Name.Should().Be("Walmart");
    }

    [TestMethod]
    public async Task Search_ChainManagerTarget_SearchWalmart_FindsNothing()
    {
        // "Walmart" matches a chain, but ChainManager Target has no access to it
        var result = await GetChainsAsync(ChainMgrTarget, search: "walmart");

        result.Data.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Search_ChainManagerWalmart_SearchTarget_FindsNothing()
    {
        // "Target" matches a chain, but ChainManager Walmart has no access to it
        var result = await GetChainsAsync(ChainMgrWalmart, search: "target");

        result.Data.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Search_CompanyAdmin_SearchLocationByStoreNumber()
    {
        var result = await GetLocationsAsync(CompanyAdmin, "chain_walmart", search: "002");

        result.Data.Should().HaveCountGreaterThanOrEqualTo(1);
        result.Data.Select(l => l.StoreNumber).Should().Contain("002");
    }

    [TestMethod]
    public async Task Search_StoreManager001_SearchLocationByStoreNumber002_FindsNothing()
    {
        // StoreManager 001 doesn't have access to store 002, even though it matches the search
        var result = await GetLocationsAsync(StoreMgr001, "chain_walmart", search: "002");

        result.Data.Should().BeEmpty();
    }

    // ====================================================================
    // CURSOR PAGINATION
    // Verifies that cursor pagination works correctly with auth filtering.
    // ====================================================================

    [TestMethod]
    public async Task Pagination_CompanyAdmin_CursorPaginationReturnsNextPage()
    {
        // Request with pageSize=1 — should have more chains available
        var page1 = await GetChainsAsync(CompanyAdmin, pageSize: 1);

        page1.Data.Should().HaveCount(1);
        page1.HasNextPage.Should().BeTrue();
        page1.NextCursor.Should().NotBeNullOrEmpty();

        // Follow the cursor to get page 2
        var page2 = await GetChainsAsync(CompanyAdmin, pageSize: 1, cursor: page1.NextCursor);

        page2.Data.Should().HaveCount(1);
        // Page 2 should have a different chain than page 1
        page2.Data[0].Name.Should().NotBe(page1.Data[0].Name);
    }

    [TestMethod]
    public async Task Pagination_ChainManagerWalmart_SingleChainNoCursor()
    {
        // ChainManager Walmart can only see 1 chain — no next page
        var result = await GetChainsAsync(ChainMgrWalmart, pageSize: 1);

        result.Data.Should().HaveCount(1);
        result.HasNextPage.Should().BeFalse();
        result.NextCursor.Should().BeNull();
    }

    [TestMethod]
    public async Task Pagination_StoreManager001_InventoryCursorPagination()
    {
        // StoreManager 001 sees at least 2 items at store 001
        var page1 = await GetInventoryAsync(StoreMgr001, "loc_001", pageSize: 1);

        page1.Data.Should().HaveCount(1);
        page1.HasNextPage.Should().BeTrue();
        page1.NextCursor.Should().NotBeNullOrEmpty();

        // Follow cursor
        var page2 = await GetInventoryAsync(StoreMgr001, "loc_001", pageSize: 1, cursor: page1.NextCursor);

        page2.Data.Should().HaveCount(1);
        page2.Data[0].Name.Should().NotBe(page1.Data[0].Name);
    }

    [TestMethod]
    public async Task Pagination_NoGrants_EmptyWithNoCursor()
    {
        var result = await GetChainsAsync(NoGrants, pageSize: 1);

        result.Data.Should().BeEmpty();
        result.HasNextPage.Should().BeFalse();
        result.NextCursor.Should().BeNull();
    }

    // ====================================================================
    // FULL TRAVERSAL — walk all pages, verify no duplicates/skips
    // and correct sort order across pages.
    // ====================================================================

    [TestMethod]
    public async Task FullTraversal_Chains_CompanyAdmin_AllPagesNoDuplicates()
    {
        // Walk through all chains one at a time, collect every item
        var allNames = new List<string>();
        string? cursor = null;
        for (var i = 0; i < 20; i++) // safety cap
        {
            var page = await GetChainsAsync(CompanyAdmin, pageSize: 1, cursor: cursor);
            allNames.AddRange(page.Data.Select(c => c.Name));
            if (!page.HasNextPage) break;
            cursor = page.NextCursor;
        }

        // Should have at least the 5 seeded chains
        allNames.Should().HaveCountGreaterThanOrEqualTo(5);
        allNames.Should().Contain("Walmart").And.Contain("Target")
            .And.Contain("Costco").And.Contain("Kroger").And.Contain("Aldi");
        // No duplicates
        allNames.Should().OnlyHaveUniqueItems();
        // Sorted alphabetically (Name is the sort key for the chains endpoint)
        allNames.Should().BeInAscendingOrder();
    }

    // ====================================================================
    // CHAIN SORT + FILTER (builder-based specification)
    // ====================================================================

    [TestMethod]
    public async Task Chains_SortByNameDesc_ReturnsDescendingOrder()
    {
        var result = await GetChainsAsync(CompanyAdmin, sortBy: "name", sortDir: "desc");
        var names = result.Data.Select(c => c.Name).ToList();

        names.Should().HaveCountGreaterThanOrEqualTo(5);
        names.Should().BeInDescendingOrder();
        names.First().Should().Be("Walmart"); // W comes last in desc
    }

    [TestMethod]
    public async Task Chains_SortByDescriptionAsc_ReturnsSortedByDescription()
    {
        var result = await GetChainsAsync(CompanyAdmin, sortBy: "description");
        var descriptions = result.Data.Select(c => c.Description).ToList();

        // Aldi Inc. < Costco Wholesale... < The Kroger Co. < Target Corp. < Walmart Inc.
        descriptions.Should().HaveCountGreaterThanOrEqualTo(5);
        descriptions.Should().BeInAscendingOrder();
    }

    [TestMethod]
    public async Task Chains_SortByDescriptionDesc_ReturnsReverseSortedByDescription()
    {
        var result = await GetChainsAsync(CompanyAdmin, sortBy: "description", sortDir: "desc");
        var descriptions = result.Data.Select(c => c.Description).ToList();

        descriptions.Should().HaveCountGreaterThanOrEqualTo(5);
        descriptions.Should().BeInDescendingOrder();
    }

    [TestMethod]
    public async Task Chains_SearchPlusSortByNameDesc_FiltersThenSorts()
    {
        // "co" matches: Costco (name), Target Corporation (desc), Costco Wholesale Corporation (desc)
        // Name desc among matches: Target, Costco
        var result = await GetChainsAsync(CompanyAdmin, search: "co", sortBy: "name", sortDir: "desc");
        var names = result.Data.Select(c => c.Name).ToList();

        names.Should().Contain("Costco");
        names.Should().BeInDescendingOrder();
        // "Aldi" and "Walmart" should NOT appear (no "co" in name or description)
        names.Should().NotContain("Aldi");
        names.Should().NotContain("Walmart");
    }

    [TestMethod]
    public async Task Chains_SearchPlusSortByDescription_FiltersThenSortsByDescription()
    {
        // "inc" matches: Walmart Inc. (desc), Aldi Inc. (desc)
        var result = await GetChainsAsync(CompanyAdmin, search: "inc", sortBy: "description");
        var descriptions = result.Data.Select(c => c.Description).ToList();

        descriptions.Should().HaveCountGreaterThanOrEqualTo(2);
        descriptions.Should().Contain("Aldi Inc.");
        descriptions.Should().Contain("Walmart Inc.");
        descriptions.Should().BeInAscendingOrder(); // Aldi Inc. before Walmart Inc.
    }

    [TestMethod]
    public async Task Chains_PaginationPlusSortByNameDesc_AllPagesCorrectOrder()
    {
        // Walk all pages one at a time, sorted by name descending
        var allNames = new List<string>();
        string? cursor = null;
        for (var i = 0; i < 20; i++)
        {
            var page = await GetChainsAsync(CompanyAdmin, pageSize: 2, cursor: cursor,
                sortBy: "name", sortDir: "desc");
            allNames.AddRange(page.Data.Select(c => c.Name));
            if (!page.HasNextPage) break;
            cursor = page.NextCursor;
        }

        allNames.Should().HaveCountGreaterThanOrEqualTo(5);
        allNames.Should().OnlyHaveUniqueItems();
        allNames.Should().BeInDescendingOrder();
    }

    [TestMethod]
    public async Task Chains_PaginationPlusSortByDescription_AllPagesCorrectOrder()
    {
        // Walk all pages one at a time, sorted by description ascending
        var allDescriptions = new List<string>();
        string? cursor = null;
        for (var i = 0; i < 20; i++)
        {
            var page = await GetChainsAsync(CompanyAdmin, pageSize: 2, cursor: cursor, sortBy: "description");
            allDescriptions.AddRange(page.Data.Select(c => c.Description!));
            if (!page.HasNextPage) break;
            cursor = page.NextCursor;
        }

        allDescriptions.Should().HaveCountGreaterThanOrEqualTo(5);
        allDescriptions.Should().OnlyHaveUniqueItems();
        allDescriptions.Should().BeInAscendingOrder();
    }

    [TestMethod]
    public async Task Chains_SortDoesNotBypassAuth_ChainManagerSeesOnlyTheirChain()
    {
        // ChainManager for Walmart only has access to Walmart, regardless of sort
        var nameAsc = await GetChainsAsync(ChainMgrWalmart, sortBy: "name");
        nameAsc.Data.Should().ContainSingle().Which.Name.Should().Be("Walmart");

        var nameDesc = await GetChainsAsync(ChainMgrWalmart, sortBy: "name", sortDir: "desc");
        nameDesc.Data.Should().ContainSingle().Which.Name.Should().Be("Walmart");

        var descSort = await GetChainsAsync(ChainMgrWalmart, sortBy: "description");
        descSort.Data.Should().ContainSingle().Which.Name.Should().Be("Walmart");
    }

    [TestMethod]
    public async Task FullTraversal_Inventory_CompanyAdmin_AllPagesNoDuplicates()
    {
        // Walk through all store 001 inventory one at a time
        var allNames = new List<string>();
        string? cursor = null;
        for (var i = 0; i < 20; i++)
        {
            var page = await GetInventoryAsync(CompanyAdmin, "loc_001", pageSize: 1, cursor: cursor);
            allNames.AddRange(page.Data.Select(x => x.Name));
            if (!page.HasNextPage) break;
            cursor = page.NextCursor;
        }

        allNames.Should().HaveCountGreaterThanOrEqualTo(2);
        allNames.Should().Contain("ProBook Laptop").And.Contain("SmartPhone X");
        allNames.Should().OnlyHaveUniqueItems();
        allNames.Should().BeInAscendingOrder();
    }

    [TestMethod]
    public async Task FullTraversal_Locations_ChainManagerWalmart_AllPagesNoDuplicates()
    {
        // Walk through all Walmart locations one at a time
        var allStoreNumbers = new List<string>();
        string? cursor = null;
        for (var i = 0; i < 20; i++)
        {
            var page = await GetLocationsAsync(ChainMgrWalmart, "chain_walmart", pageSize: 1, cursor: cursor);
            allStoreNumbers.AddRange(page.Data.Select(l => l.StoreNumber!));
            if (!page.HasNextPage) break;
            cursor = page.NextCursor;
        }

        allStoreNumbers.Should().HaveCountGreaterThanOrEqualTo(2);
        allStoreNumbers.Should().Contain("001").And.Contain("002");
        allStoreNumbers.Should().OnlyHaveUniqueItems();
        // Locations sort by StoreNumber
        allStoreNumbers.Should().BeInAscendingOrder();
    }

    [TestMethod]
    public async Task FullTraversal_AuthScoped_StoreManager001_OnlySeesOwnItems()
    {
        // Walk all pages — StoreManager 001 should only see items from their store
        var allNames = new List<string>();
        string? cursor = null;
        for (var i = 0; i < 20; i++)
        {
            var page = await GetInventoryAsync(StoreMgr001, "loc_001", pageSize: 1, cursor: cursor);
            allNames.AddRange(page.Data.Select(x => x.Name));
            if (!page.HasNextPage) break;
            cursor = page.NextCursor;
        }

        allNames.Should().HaveCountGreaterThanOrEqualTo(2);
        allNames.Should().Contain("ProBook Laptop").And.Contain("SmartPhone X");
        allNames.Should().OnlyHaveUniqueItems();
        // Should NOT contain items from other stores
        allNames.Should().NotContain("TabPro 11");
        allNames.Should().NotContain("BassMax Headphones");
    }

    // ====================================================================
    // CURSOR + SEARCH COMBINED
    // Verifies that cursor pagination works correctly when search
    // narrows the result set.
    // ====================================================================

    [TestMethod]
    public async Task CursorPlusSearch_CompanyAdmin_SearchAndPaginate()
    {
        // CompanyAdmin sees both seeded chains. Search for names containing "a"
        // (both "Walmart" and "Target" contain "a"), then paginate through them
        var allNames = new List<string>();
        string? cursor = null;
        for (var i = 0; i < 20; i++)
        {
            var page = await GetChainsAsync(CompanyAdmin, search: "a", pageSize: 1, cursor: cursor);
            allNames.AddRange(page.Data.Select(c => c.Name));
            if (!page.HasNextPage) break;
            cursor = page.NextCursor;
        }

        allNames.Should().HaveCountGreaterThanOrEqualTo(2);
        allNames.Should().Contain("Walmart").And.Contain("Target");
        allNames.Should().OnlyHaveUniqueItems();
        allNames.Should().BeInAscendingOrder();
    }

    [TestMethod]
    public async Task CursorPlusSearch_NarrowsToSingleResult_NoCursor()
    {
        // Search for "walmart" — should return exactly 1 result for CompanyAdmin, no next page
        var result = await GetChainsAsync(CompanyAdmin, search: "walmart", pageSize: 1);

        result.Data.Should().ContainSingle()
            .Which.Name.Should().Be("Walmart");
        result.HasNextPage.Should().BeFalse();
    }

    [TestMethod]
    public async Task CursorPlusSearch_AuthScopedSearch_ChainManagerOnlySeesOwnMatch()
    {
        // ChainManager Walmart searches for "a" — only Walmart matches (Target is auth-filtered out)
        var result = await GetChainsAsync(ChainMgrWalmart, search: "a", pageSize: 1);

        result.Data.Should().ContainSingle()
            .Which.Name.Should().Be("Walmart");
        result.HasNextPage.Should().BeFalse();
    }

    [TestMethod]
    public async Task CursorPlusSearch_InventorySearchAndPaginate()
    {
        // Search inventory for a term that matches multiple items at store 001
        // Both "ProBook Laptop" and "SmartPhone X" are electronics at store 001
        // Search by partial SKU "ELEC" which matches both
        var allNames = new List<string>();
        string? cursor = null;
        for (var i = 0; i < 20; i++)
        {
            var page = await GetInventoryAsync(CompanyAdmin, "loc_001", search: "ELEC", pageSize: 1, cursor: cursor);
            allNames.AddRange(page.Data.Select(x => x.Name));
            if (!page.HasNextPage) break;
            cursor = page.NextCursor;
        }

        allNames.Should().HaveCountGreaterThanOrEqualTo(2);
        allNames.Should().OnlyHaveUniqueItems();
        allNames.Should().BeInAscendingOrder();
    }

    [TestMethod]
    public async Task CursorPlusSearch_NoMatch_EmptyResults()
    {
        var result = await GetChainsAsync(CompanyAdmin, search: "nonexistent", pageSize: 1);

        result.Data.Should().BeEmpty();
        result.HasNextPage.Should().BeFalse();
        result.NextCursor.Should().BeNull();
    }

    // ====================================================================
    // CURSOR + PARENT FILTER (e.g., locations within a specific chain)
    // ====================================================================

    [TestMethod]
    public async Task CursorPlusParentFilter_LocationsPaginatedWithinChain()
    {
        // Paginate through Walmart locations (at least 2 seeded) one at a time
        var allStoreNumbers = new List<string>();
        string? cursor = null;
        for (var i = 0; i < 20; i++)
        {
            var page = await GetLocationsAsync(CompanyAdmin, "chain_walmart", pageSize: 1, cursor: cursor);
            allStoreNumbers.AddRange(page.Data.Select(l => l.StoreNumber!));
            if (!page.HasNextPage) break;
            cursor = page.NextCursor;
        }

        allStoreNumbers.Should().HaveCountGreaterThanOrEqualTo(2);
        allStoreNumbers.Should().Contain("001").And.Contain("002");
        allStoreNumbers.Should().OnlyHaveUniqueItems();
        // Should NOT contain Target's location
        allStoreNumbers.Should().NotContain("100");
    }

    [TestMethod]
    public async Task CursorPlusParentFilter_SingleLocationChain_NoCursor()
    {
        // Target has only 1 location — no next page
        var result = await GetLocationsAsync(CompanyAdmin, "chain_target", pageSize: 1);

        result.Data.Should().ContainSingle()
            .Which.StoreNumber.Should().Be("100");
        result.HasNextPage.Should().BeFalse();
    }

    [TestMethod]
    public async Task CursorPlusParentFilter_AuthScopedLocations()
    {
        // StoreManager 001 paginates locations in chain_walmart — should only see store 001
        var result = await GetLocationsAsync(StoreMgr001, "chain_walmart", pageSize: 1);

        result.Data.Should().ContainSingle()
            .Which.StoreNumber.Should().Be("001");
        result.HasNextPage.Should().BeFalse();
    }

    [TestMethod]
    public async Task CursorPlusParentFilterPlusSearch_LocationSearchAndPaginate()
    {
        // Search for "00" within Walmart locations — matches "001" and "002"
        var allStoreNumbers = new List<string>();
        string? cursor = null;
        for (var i = 0; i < 20; i++)
        {
            var page = await GetLocationsAsync(CompanyAdmin, "chain_walmart", search: "00", pageSize: 1, cursor: cursor);
            allStoreNumbers.AddRange(page.Data.Select(l => l.StoreNumber!));
            if (!page.HasNextPage) break;
            cursor = page.NextCursor;
        }

        allStoreNumbers.Should().HaveCountGreaterThanOrEqualTo(2);
        allStoreNumbers.Should().Contain("001").And.Contain("002");
        allStoreNumbers.Should().OnlyHaveUniqueItems();
    }

    // ====================================================================
    // HIERARCHY INHERITANCE
    // Grant at parent cascades to all descendants.
    // ====================================================================

    [TestMethod]
    public async Task Hierarchy_CompanyAdminAtRoot_SeesEverythingAtAllLevels()
    {
        // Chains — sees both seeded chains
        var chains = await GetChainsAsync(CompanyAdmin);
        chains.Data.Select(c => c.Name).Should().Contain("Walmart").And.Contain("Target");

        // Locations across both chains
        var walmartLocs = await GetLocationsAsync(CompanyAdmin, "chain_walmart");
        walmartLocs.Data.Select(l => l.StoreNumber).Should().Contain("001").And.Contain("002");

        var targetLocs = await GetLocationsAsync(CompanyAdmin, "chain_target");
        targetLocs.Data.Select(l => l.StoreNumber).Should().Contain("100");

        // Inventory at all stores
        var inv001 = await GetInventoryAsync(CompanyAdmin, "loc_001");
        inv001.Data.Select(i => i.Name).Should().Contain("ProBook Laptop").And.Contain("SmartPhone X");

        var inv002 = await GetInventoryAsync(CompanyAdmin, "loc_002");
        inv002.Data.Select(i => i.Name).Should().Contain("TabPro 11");

        var inv100 = await GetInventoryAsync(CompanyAdmin, "loc_100");
        inv100.Data.Select(i => i.Name).Should().Contain("BassMax Headphones");
    }

    [TestMethod]
    public async Task Hierarchy_ChainManagerGrant_CascadesToLocationsAndInventory()
    {
        // ChainManager Walmart: grant at chain level cascades to locations + inventory
        var locations = await GetLocationsAsync(ChainMgrWalmart, "chain_walmart");
        locations.Data.Select(l => l.StoreNumber).Should().Contain("001").And.Contain("002");

        var inv001 = await GetInventoryAsync(ChainMgrWalmart, "loc_001");
        inv001.Data.Select(i => i.Name).Should().Contain("ProBook Laptop").And.Contain("SmartPhone X");

        var inv002 = await GetInventoryAsync(ChainMgrWalmart, "loc_002");
        inv002.Data.Select(i => i.Name).Should().Contain("TabPro 11");
    }

    [TestMethod]
    public async Task Hierarchy_ChainManagerGrant_DoesNotCascadeToOtherChain()
    {
        // ChainManager Walmart: NO access to Target's tree
        var targetLocs = await GetLocationsAsync(ChainMgrWalmart, "chain_target");
        targetLocs.Data.Should().BeEmpty();

        var targetInv = await GetInventoryAsync(ChainMgrWalmart, "loc_100");
        targetInv.Data.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Hierarchy_StoreManagerGrant_CascadesToInventoryOnly()
    {
        // StoreManager 001: grant at location level cascades to inventory under that location
        var inventory = await GetInventoryAsync(StoreMgr001, "loc_001");
        inventory.Data.Select(i => i.Name).Should().Contain("ProBook Laptop").And.Contain("SmartPhone X");

        // But doesn't cascade to sibling store's inventory
        var otherInventory = await GetInventoryAsync(StoreMgr001, "loc_002");
        otherInventory.Data.Should().BeEmpty();

        // And doesn't grant access to parent level (chains)
        var chains = await GetChainsAsync(StoreMgr001);
        chains.Data.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Hierarchy_StoreManagerGrant_DoesNotGrantAccessUpward()
    {
        // StoreManager has LOCATION_VIEW at their location but no CHAIN_VIEW
        var chains = await GetChainsAsync(StoreMgr001);
        chains.Data.Should().BeEmpty();

        // They see their own location but not sibling locations
        var locations = await GetLocationsAsync(StoreMgr001, "chain_walmart");
        locations.Data.Should().ContainSingle()
            .Which.StoreNumber.Should().Be("001");
    }

    // ====================================================================
    // CROSS-CHAIN ISOLATION
    // ====================================================================

    [TestMethod]
    public async Task CrossChain_WalmartManagerCannotSeeTargetData()
    {
        var targetChains = await GetChainsAsync(ChainMgrWalmart, search: "target");
        targetChains.Data.Should().BeEmpty();

        var targetLocations = await GetLocationsAsync(ChainMgrWalmart, "chain_target");
        targetLocations.Data.Should().BeEmpty();

        var targetInventory = await GetInventoryAsync(ChainMgrWalmart, "loc_100");
        targetInventory.Data.Should().BeEmpty();
    }

    [TestMethod]
    public async Task CrossChain_TargetManagerCannotSeeWalmartData()
    {
        var walmartChains = await GetChainsAsync(ChainMgrTarget, search: "walmart");
        walmartChains.Data.Should().BeEmpty();

        var walmartLocations = await GetLocationsAsync(ChainMgrTarget, "chain_walmart");
        walmartLocations.Data.Should().BeEmpty();

        var walmartInventory001 = await GetInventoryAsync(ChainMgrTarget, "loc_001");
        walmartInventory001.Data.Should().BeEmpty();

        var walmartInventory002 = await GetInventoryAsync(ChainMgrTarget, "loc_002");
        walmartInventory002.Data.Should().BeEmpty();
    }

    // ====================================================================
    // CROSS-STORE ISOLATION (same chain, different stores)
    // ====================================================================

    [TestMethod]
    public async Task CrossStore_StoreManager001_CannotSeeStore002()
    {
        var locations = await GetLocationsAsync(StoreMgr001, "chain_walmart");
        locations.Data.Select(l => l.StoreNumber).Should().NotContain("002");

        var inventory = await GetInventoryAsync(StoreMgr001, "loc_002");
        inventory.Data.Should().BeEmpty();
    }

    [TestMethod]
    public async Task CrossStore_StoreManager002_CannotSeeStore001()
    {
        var locations = await GetLocationsAsync(StoreMgr002, "chain_walmart");
        locations.Data.Select(l => l.StoreNumber).Should().NotContain("001");

        var inventory = await GetInventoryAsync(StoreMgr002, "loc_001");
        inventory.Data.Should().BeEmpty();
    }

    [TestMethod]
    public async Task CrossStore_StoreClerk001_CannotSeeStore002OrTargetStore()
    {
        var store002 = await GetInventoryAsync(StoreClerk001, "loc_002");
        store002.Data.Should().BeEmpty();

        var targetStore = await GetLocationsAsync(StoreClerk001, "chain_target");
        targetStore.Data.Should().BeEmpty();
    }

    // ====================================================================
    // SORT ORDER — asc, desc, sort+filter, sort desc+multiple filters
    // ====================================================================

    [TestMethod]
    public async Task Sort_NameAsc_ReturnsItemsInAscendingNameOrder()
    {
        // Store 001 has ProBook Laptop ($799.99) and SmartPhone X ($999.99)
        var result = await GetInventoryAsync(CompanyAdmin, "loc_001", sortBy: "name", sortDir: "asc");

        result.Data.Should().HaveCountGreaterThanOrEqualTo(2);
        result.Data.Select(i => i.Name).Should().BeInAscendingOrder();
    }

    [TestMethod]
    public async Task Sort_NameDesc_ReturnsItemsInDescendingNameOrder()
    {
        var result = await GetInventoryAsync(CompanyAdmin, "loc_001", sortBy: "name", sortDir: "desc");

        result.Data.Should().HaveCountGreaterThanOrEqualTo(2);
        result.Data.Select(i => i.Name).Should().BeInDescendingOrder();
    }

    [TestMethod]
    public async Task Sort_PriceAsc_ReturnsItemsInAscendingPriceOrder()
    {
        var result = await GetInventoryAsync(CompanyAdmin, "loc_001", sortBy: "price", sortDir: "asc");

        result.Data.Should().HaveCountGreaterThanOrEqualTo(2);
        result.Data.Select(i => i.Price).Should().BeInAscendingOrder();
        // ProBook Laptop (799.99) should appear before SmartPhone X (999.99)
        var names = result.Data.Select(i => i.Name).ToList();
        names.IndexOf("ProBook Laptop").Should().BeLessThan(names.IndexOf("SmartPhone X"));
    }

    [TestMethod]
    public async Task Sort_PriceDesc_ReturnsItemsInDescendingPriceOrder()
    {
        var result = await GetInventoryAsync(CompanyAdmin, "loc_001", sortBy: "price", sortDir: "desc");

        result.Data.Should().HaveCountGreaterThanOrEqualTo(2);
        result.Data.Select(i => i.Price).Should().BeInDescendingOrder();
        // SmartPhone X (999.99) should appear before ProBook Laptop (799.99)
        var names = result.Data.Select(i => i.Name).ToList();
        names.IndexOf("SmartPhone X").Should().BeLessThan(names.IndexOf("ProBook Laptop"));
    }

    [TestMethod]
    public async Task Sort_SkuAsc_ReturnsItemsInAscendingSkuOrder()
    {
        var result = await GetInventoryAsync(CompanyAdmin, "loc_001", sortBy: "sku", sortDir: "asc");

        result.Data.Should().HaveCountGreaterThanOrEqualTo(2);
        result.Data.Select(i => i.Sku).Should().BeInAscendingOrder();
    }

    [TestMethod]
    public async Task Sort_SkuDesc_ReturnsItemsInDescendingSkuOrder()
    {
        var result = await GetInventoryAsync(CompanyAdmin, "loc_001", sortBy: "sku", sortDir: "desc");

        result.Data.Should().HaveCountGreaterThanOrEqualTo(2);
        result.Data.Select(i => i.Sku).Should().BeInDescendingOrder();
    }

    [TestMethod]
    public async Task Sort_NameAscPlusSearch_FilterAndSortCombined()
    {
        // Search "ELEC" matches both SKUs at store 001, sorted by name asc
        var result = await GetInventoryAsync(CompanyAdmin, "loc_001", search: "ELEC", sortBy: "name", sortDir: "asc");

        result.Data.Should().HaveCountGreaterThanOrEqualTo(2);
        result.Data.Select(i => i.Name).Should().BeInAscendingOrder();
        result.Data.Select(i => i.Name).Should().Contain("ProBook Laptop").And.Contain("SmartPhone X");
    }

    [TestMethod]
    public async Task Sort_PriceDescPlusSearch_FilterAndSortDescCombined()
    {
        // Search "ELEC" + sort by price desc — SmartPhone X (999.99) before ProBook Laptop (799.99)
        var result = await GetInventoryAsync(CompanyAdmin, "loc_001", search: "ELEC", sortBy: "price", sortDir: "desc");

        result.Data.Should().HaveCountGreaterThanOrEqualTo(2);
        result.Data.Select(i => i.Price).Should().BeInDescendingOrder();
        result.Data.First().Name.Should().Be("SmartPhone X");
    }

    [TestMethod]
    public async Task Sort_NameDescPlusCursorPagination_AllPagesInDescOrder()
    {
        // Walk all store 001 items with name desc, pageSize=1
        var allNames = new List<string>();
        string? cursor = null;
        for (var i = 0; i < 20; i++)
        {
            var page = await GetInventoryAsync(CompanyAdmin, "loc_001", pageSize: 1, sortBy: "name", sortDir: "desc", cursor: cursor);
            allNames.AddRange(page.Data.Select(x => x.Name));
            if (!page.HasNextPage) break;
            cursor = page.NextCursor;
        }

        allNames.Should().HaveCountGreaterThanOrEqualTo(2);
        allNames.Should().OnlyHaveUniqueItems();
        allNames.Should().BeInDescendingOrder();
    }

    [TestMethod]
    public async Task Sort_PriceAscPlusCursorPagination_AllPagesInAscPriceOrder()
    {
        // Walk all store 001 items with price asc, pageSize=1
        var allPrices = new List<decimal>();
        var allNames = new List<string>();
        string? cursor = null;
        for (var i = 0; i < 20; i++)
        {
            var page = await GetInventoryAsync(CompanyAdmin, "loc_001", pageSize: 1, sortBy: "price", sortDir: "asc", cursor: cursor);
            allPrices.AddRange(page.Data.Select(x => x.Price));
            allNames.AddRange(page.Data.Select(x => x.Name));
            if (!page.HasNextPage) break;
            cursor = page.NextCursor;
        }

        allPrices.Should().HaveCountGreaterThanOrEqualTo(2);
        allNames.Should().OnlyHaveUniqueItems();
        allPrices.Should().BeInAscendingOrder();
    }

    [TestMethod]
    public async Task Sort_PriceDescPlusCursorPlusSearch_AllFiltersAndSortDesc()
    {
        // Search "ELEC" (matches both items at loc_001) + price desc + paginate
        var allPrices = new List<decimal>();
        var allNames = new List<string>();
        string? cursor = null;
        for (var i = 0; i < 20; i++)
        {
            var page = await GetInventoryAsync(CompanyAdmin, "loc_001", search: "ELEC", pageSize: 1,
                sortBy: "price", sortDir: "desc", cursor: cursor);
            allPrices.AddRange(page.Data.Select(x => x.Price));
            allNames.AddRange(page.Data.Select(x => x.Name));
            if (!page.HasNextPage) break;
            cursor = page.NextCursor;
        }

        allPrices.Should().HaveCountGreaterThanOrEqualTo(2);
        allNames.Should().OnlyHaveUniqueItems();
        allPrices.Should().BeInDescendingOrder();
    }

    // ====================================================================
    // SORT + FILTER DOES NOT BYPASS TVF AUTHORIZATION
    // Ensures that applying sort/filter params doesn't override or
    // circumvent the authorization TVF — unauthorized data stays hidden.
    // ====================================================================

    [TestMethod]
    public async Task SortPlusAuth_ChainManagerWalmart_SortDoesNotExposeTargetItems()
    {
        // ChainManager Walmart has no access to Target's store 100
        // Even with sort params, should return empty
        var result = await GetInventoryAsync(ChainMgrWalmart, "loc_100", sortBy: "name", sortDir: "asc");
        result.Data.Should().BeEmpty();

        var resultDesc = await GetInventoryAsync(ChainMgrWalmart, "loc_100", sortBy: "price", sortDir: "desc");
        resultDesc.Data.Should().BeEmpty();
    }

    [TestMethod]
    public async Task SortPlusAuth_StoreManager001_SortDoesNotExposeStore002Items()
    {
        // StoreManager 001 has no access to store 002
        var result = await GetInventoryAsync(StoreMgr001, "loc_002", sortBy: "name", sortDir: "asc");
        result.Data.Should().BeEmpty();

        var resultDesc = await GetInventoryAsync(StoreMgr001, "loc_002", sortBy: "price", sortDir: "desc");
        resultDesc.Data.Should().BeEmpty();
    }

    [TestMethod]
    public async Task SortPlusAuth_NoGrants_SortDoesNotExposeAnyItems()
    {
        // No grants user should see nothing regardless of sort params
        var result = await GetInventoryAsync(NoGrants, "loc_001", sortBy: "price", sortDir: "desc");
        result.Data.Should().BeEmpty();
    }

    [TestMethod]
    public async Task SortPlusSearchPlusAuth_ChainManagerTarget_FilterAndSortDoNotExposeWalmartItems()
    {
        // ChainManager Target searches for "ELEC" (matches Walmart items) at Walmart's store 001
        // Even with search + sort, auth should prevent access
        var result = await GetInventoryAsync(ChainMgrTarget, "loc_001", search: "ELEC", sortBy: "price", sortDir: "desc");
        result.Data.Should().BeEmpty();
    }

    [TestMethod]
    public async Task SortPlusSearchPlusAuth_StoreClerk001_FilterAndSortDoNotExposeStore002Items()
    {
        // StoreClerk 001 searches for "tablet" at store 002 with sort — should be empty
        var result = await GetInventoryAsync(StoreClerk001, "loc_002", search: "tablet", sortBy: "name", sortDir: "asc");
        result.Data.Should().BeEmpty();
    }

    // ====================================================================
    // HELPERS
    // ====================================================================

    private async Task<PaginatedResult<ChainItem>> GetChainsAsync(
        string subjectId, string? search = null, int? pageSize = null, string? cursor = null,
        string? sortBy = null, string? sortDir = null)
    {
        var url = "/api/chains?";
        if (pageSize.HasValue) url += $"pageSize={pageSize}&";
        if (search != null) url += $"search={search}&";
        if (cursor != null) url += $"cursor={Uri.EscapeDataString(cursor)}&";
        if (sortBy != null) url += $"sortBy={sortBy}&";
        if (sortDir != null) url += $"sortDir={sortDir}&";
        url = url.TrimEnd('&', '?');

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Subject-Id", subjectId);

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} as {subjectId}");

        var result = await response.Content.ReadFromJsonAsync<PaginatedResult<ChainItem>>();
        result.Should().NotBeNull();
        return result!;
    }

    private async Task<PaginatedResult<LocationItem>> GetLocationsAsync(
        string subjectId, string chainId, string? search = null, int? pageSize = null, string? cursor = null)
    {
        var url = $"/api/chains/{chainId}/locations?";
        if (pageSize.HasValue) url += $"pageSize={pageSize}&"; else url += "pageSize=10&";
        if (search != null) url += $"search={search}&";
        if (cursor != null) url += $"cursor={Uri.EscapeDataString(cursor)}&";
        url = url.TrimEnd('&', '?');

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Subject-Id", subjectId);

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} as {subjectId}");

        var result = await response.Content.ReadFromJsonAsync<PaginatedResult<LocationItem>>();
        result.Should().NotBeNull();
        return result!;
    }

    private async Task<PaginatedResult<InventoryItemResult>> GetInventoryAsync(
        string subjectId, string locationId, string? search = null, int? pageSize = null,
        string? cursor = null, string? sortBy = null, string? sortDir = null)
    {
        var url = $"/api/locations/{locationId}/inventory?";
        if (pageSize.HasValue) url += $"pageSize={pageSize}&"; else url += "pageSize=10&";
        if (search != null) url += $"search={search}&";
        if (cursor != null) url += $"cursor={Uri.EscapeDataString(cursor)}&";
        if (sortBy != null) url += $"sortBy={sortBy}&";
        if (sortDir != null) url += $"sortDir={sortDir}&";
        url = url.TrimEnd('&', '?');

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Subject-Id", subjectId);

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} as {subjectId}");

        var result = await response.Content.ReadFromJsonAsync<PaginatedResult<InventoryItemResult>>();
        result.Should().NotBeNull();
        return result!;
    }

    // Response DTOs
    private record PaginatedResult<T>(List<T> Data, int PageSize, string? NextCursor, bool HasNextPage);
    private record ChainItem(string Id, string Name, string? Description);
    private record LocationItem(string Id, string Name, string? StoreNumber, string? City, string? State);
    private record InventoryItemResult(string Id, string Name, string Sku, decimal Price);
}
