# Example: Retail API

A complete example application lives in `examples/Sqlzibar.Example.Api/`. It models a retail company with chains, locations, and inventory items.

## Running the Example

Requires a SQL Server instance (e.g., via Docker):

```bash
cd examples/Sqlzibar.Example.Api
dotnet run
```

- Swagger UI: `http://localhost:5000/swagger`
- Dashboard: `http://localhost:5000/sqlzibar/`

> **If you change the seed data**, drop the database first — the seed service skips if data already exists:
>
> ```bash
> sqlcmd -S 127.0.0.1,1434 -U sa -P 'YourPassword' -Q "DROP DATABASE example"
> ```

## Seeded Resource Hierarchy

```
retail_root (CompanyAdmin)
  ├── Walmart (ChainManager Walmart, Walmart Regional Managers group)
  │     ├── Store 001 (StoreManager 001, StoreClerk 001)
  │     │     ├── ProBook Laptop
  │     │     └── SmartPhone X
  │     └── Store 002 (StoreManager 002)
  │           └── TabPro 11
  ├── Target (ChainManager Target)
  │     └── Store 100
  │           └── BassMax Headphones
  ├── Costco
  ├── Kroger
  └── Aldi
```

## Seeded Subjects

| Subject                   | Type            | Direct Grant                  | Scope                                 |
| ------------------------- | --------------- | ----------------------------- | ------------------------------------- |
| Company Admin             | user            | CompanyAdmin @ retail_root    | Everything                            |
| Walmart Chain Manager     | user            | ChainManager @ Walmart        | Walmart + all descendants             |
| Target Chain Manager      | user            | ChainManager @ Target         | Target + all descendants              |
| Store 001 Manager         | user            | StoreManager @ Store 001      | Store 001 + its inventory             |
| Store 002 Manager         | user            | StoreManager @ Store 002      | Store 002 + its inventory             |
| Store 001 Clerk           | user            | StoreClerk @ Store 001        | Store 001 + its inventory (view only) |
| No Grants User            | user            | _(none)_                      | Nothing                               |
| Walmart Regional Managers | group           | ChainManager @ Walmart        | Walmart + all descendants             |
| Alice (Regional)          | user            | _(none — inherits via group)_ | Walmart + all descendants             |
| Bob (Regional)            | user            | _(none — inherits via group)_ | Walmart + all descendants             |
| Inventory Sync Agent      | agent           | StoreManager @ Walmart        | Walmart stores                        |
| API Integration           | service_account | StoreClerk @ retail_root      | Read-only everywhere                  |

## Access Tester Demo Scenarios

Open the dashboard at `/sqlzibar/` and navigate to **Access Tester**. These scenarios demonstrate different authorization behaviors with the seeded data:

### 1. Group Inheritance — User inherits access via group membership

| Field      | Value            |
| ---------- | ---------------- |
| Subject    | Alice (Regional) |
| Permission | CHAIN_VIEW       |
| Resource   | Walmart          |

**Expected: ACCESS GRANTED.** Alice has no direct grants. The trace shows principal resolution found her membership in the "Walmart Regional Managers" group, which has a ChainManager grant on Walmart. ChainManager includes CHAIN_VIEW.

### 2. Hierarchy Cascade — Root grant gives access to everything

| Field      | Value          |
| ---------- | -------------- |
| Subject    | Company Admin  |
| Permission | INVENTORY_VIEW |
| Resource   | Laptop         |

**Expected: ACCESS GRANTED.** Company Admin's grant is at `retail_root`. The trace walks up from Laptop → Store 001 → Walmart → retail_root and finds the CompanyAdmin role grant, which includes INVENTORY_VIEW.

### 3. Cross-Chain Isolation — Grant on one chain doesn't leak to another

| Field      | Value                 |
| ---------- | --------------------- |
| Principal  | Walmart Chain Manager |
| Permission | CHAIN_VIEW            |
| Resource   | Target                |

**Expected: ACCESS DENIED.** The trace walks up from Target → retail_root, checking for grants from this subject at each level. The ChainManager grant is on Walmart, not on Target or retail_root.

### 4. Permission Boundary — Having a role doesn't mean having all permissions

| Field      | Value           |
| ---------- | --------------- |
| Principal  | Store 001 Clerk |
| Permission | INVENTORY_EDIT  |
| Resource   | Laptop          |

**Expected: ACCESS DENIED.** The clerk has a StoreClerk grant at Store 001 which covers the Laptop resource, but StoreClerk only includes INVENTORY_VIEW, not INVENTORY_EDIT. The trace shows the grant is found but the permission does not match.

### 5. Store-Level Scoping — Store manager can't see sibling store's data

| Field      | Value             |
| ---------- | ----------------- |
| Principal  | Store 001 Manager |
| Permission | INVENTORY_VIEW    |
| Resource   | Tablet            |

**Expected: ACCESS DENIED.** The Tablet is under Store 002, and Store 001 Manager's grant is at Store 001. Walking up from Tablet → Store 002 → Walmart → retail_root finds no grants for this subject at any ancestor.

### 6. Group Isolation — Non-member doesn't inherit group access

| Field      | Value          |
| ---------- | -------------- |
| Principal  | No Grants User |
| Permission | CHAIN_VIEW     |
| Resource   | Walmart        |

**Expected: ACCESS DENIED.** This user has no direct grants and no group memberships. The trace shows only one subject was checked (the user themselves), with no grants found at any level.

### 7. Agent Access — Agent has direct grant

| Field      | Value                |
| ---------- | -------------------- |
| Principal  | Inventory Sync Agent |
| Permission | INVENTORY_EDIT       |
| Resource   | Store 001            |

**Expected: ACCESS GRANTED.** The agent has StoreManager grant on Walmart chain, which cascades to Store 001. The agent is also a member of Walmart Regional Managers group (inherits ChainManager).

### 8. Service Account Access — Service account with limited permissions

| Field      | Value           |
| ---------- | --------------- |
| Subject    | API Integration |
| Permission | INVENTORY_EDIT  |
| Resource   | Laptop          |

**Expected: ACCESS DENIED.** The service account has StoreClerk role (view-only) at retail_root, which doesn't include INVENTORY_EDIT.
