# Retail Example — SHRBAC Reference

Full implementation: `examples/SqlOS.Example.Api/FgaRetail/`

Run via AppHost: `dotnet run --project examples/SqlOS.Example.AppHost/SqlOS.Example.AppHost.csproj`  
Web UI: `http://localhost:3000/retail` · FGA admin: `http://localhost:5062/sqlos/admin/fga/`

## Domain → FGA mapping

| Domain entity | Table | Resource type | Parent in tree |
| --- | --- | --- | --- |
| (app root) | — | `root` | `root` (system) |
| Retail org scope | — | `root` | `root` |
| Chain | `Chains` | `chain` | `retail_root` |
| Location (store) | `Locations` | `location` | chain resource |
| InventoryItem | `InventoryItems` | `inventory_item` | location resource |

Each entity implements `IHasResourceId` with a stable `ResourceId` set at creation time.

## Resource tree (seeded)

```
root
└── retail_root                         "Retail Root"
    ├── res_chain_walmart               "Walmart"
    │   ├── res_location_001            "Store 001"
    │   │   ├── res_inv_laptop
    │   │   └── res_inv_phone
    │   └── res_location_002            "Store 002"
    │       └── res_inv_tablet
    ├── res_chain_target                "Target"
    │   └── res_location_100            "Store 100"
    │       └── res_inv_headphones
    ├── res_chain_costco
    ├── res_chain_kroger
    └── res_chain_aldi
```

Costco, Kroger, Aldi exist for list-filter demos (company admin sees all five chains).

## Permissions and roles

Constants: `RetailPermissionKeys.cs`, `RetailRoleKeys.cs`, `RetailResourceTypeIds.cs`

### Permissions

| Key | Resource type |
| --- | --- |
| `CHAIN_VIEW` | chain |
| `CHAIN_EDIT` | chain |
| `LOCATION_VIEW` | location |
| `LOCATION_EDIT` | location |
| `INVENTORY_VIEW` | inventory_item |
| `INVENTORY_EDIT` | inventory_item |

### Role → permission matrix

| Role key | CHAIN_VIEW | CHAIN_EDIT | LOCATION_VIEW | LOCATION_EDIT | INVENTORY_VIEW | INVENTORY_EDIT |
| --- | --- | --- | --- | --- | --- | --- |
| CompanyAdmin | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| ChainManager | ✓ | | ✓ | ✓ | ✓ | ✓ |
| StoreManager | | | ✓ | ✓ | ✓ | ✓ |
| StoreClerk | | | ✓ | | ✓ | |

## Grants matrix (seeded personas)

| Display name | Email / credential | Subject type | Role | Resource | Expected scope |
| --- | --- | --- | --- | --- | --- |
| Company Admin | admin@retail.demo | user | CompanyAdmin | retail_root | All chains, stores, inventory |
| Walmart Chain Manager | walmart.mgr@retail.demo | user | ChainManager | res_chain_walmart | Walmart only |
| Target Chain Manager | target.mgr@retail.demo | user | ChainManager | res_chain_target | Target only |
| Store 001 Manager | store001.mgr@retail.demo | user | StoreManager | res_location_001 | Store 001 subtree |
| Store 002 Manager | store002.mgr@retail.demo | user | StoreManager | res_location_002 | Store 002 subtree |
| Store 001 Clerk | clerk001@retail.demo | user | StoreClerk | res_location_001 | Read store 001 + inventory |
| Alice (Regional) | alice@retail.demo | user (in group) | ChainManager via group | res_chain_walmart | Same as Walmart chain mgr |
| Bob (Regional) | bob@retail.demo | user (in group) | ChainManager via group | res_chain_walmart | Same as Walmart chain mgr |
| No Grants User | nogrants@retail.demo | user | — | — | Denied everywhere |
| Inventory Sync Agent | agent credential | agent | StoreManager | res_chain_walmart | Walmart stores + inventory |
| API Integration | client_id `retail_api_client` | service_account | StoreClerk | retail_root | Read-only org-wide |

Group: `grp_walmart_regional` / subject `subj_walmart_regional_group` — Alice and Bob are members.

## API routes and enforcement

| Route | List/detail pattern | Permission |
| --- | --- | --- |
| `GET /api/chains` | `BuildFilterAsync<Chain>` + `.Where(filter)` in the EF query | CHAIN_VIEW |
| `GET /api/chains/{id}` | `AuthorizedDetailAsync` | CHAIN_VIEW |
| `POST /api/chains` | `CheckAccessAsync(..., CHAIN_EDIT, "retail_root")` | CHAIN_EDIT on parent |
| `PUT/DELETE /api/chains/{id}` | `CheckAccessAsync(..., CHAIN_EDIT, chain.ResourceId)` | CHAIN_EDIT |
| `GET /api/chains/{chainId}/locations` | `GetLocationsSpecification` | LOCATION_VIEW |
| `GET /api/locations`, inventory routes | Similar spec/detail/check patterns | LOCATION_* / INVENTORY_* |

Create-chain uses manual `context.CreateResource("retail_root", ...)` — comment in code notes Todo-style `ISqlOSResourceEntity` as the recommended path for new apps.

## Integration test scenarios

File: `SqlOSExampleRetailFgaIntegrationTests.cs`

| Test | Proves |
| --- | --- |
| `CompanyAdmin_CanListAndCreateChains` | Root grant + CHAIN_EDIT on parent |
| `ChainManager_IsScopedToAssignedChain_AndCanManageChildLocations` | Subtree scoping + child create |
| `StoreClerk_CanViewAssignedInventory_ButCannotCreateInventory` | Least privilege at leaf |
| `GroupMembership_GrantsInheritedRetailAccess` | Group → subject resolution |
| `Agent_HasStoreManagerAccessOnWalmart` | Non-user subjects |
| `ServiceAccount_HasReadOnlyAccessAtRetailRoot` | SA + inherited read, write denied |

## Demo password

All retail demo users: `RetailDemo1!` (constant `RetailSeedService.DemoPassword`)

Switch users in the example web app user switcher or via `POST /api/v1/auth/demo/switch`.

## Contrast with Todo sample

| Aspect | Retail | Todo |
| --- | --- | --- |
| Complexity | Multi-chain org, 4 subject types, 4 roles | Single-user tenant |
| Resource sync | Manual `SqlOSFgaResource` + `IHasResourceId` | `ISqlOSResourceEntity` auto-sync |
| Seed style | `RetailSeedService` + hosted seeder | `options.Fga.Seed` in `Program.cs` |
| Grant pattern | Many grants on different nodes | One `tenant_owner` grant per user on `tenant::{subjectId}` |

Use **Todo** for minimal single-tenant apps. Use **retail** as the template when modeling hierarchical multi-entity authorization with groups, agents, and service accounts.
