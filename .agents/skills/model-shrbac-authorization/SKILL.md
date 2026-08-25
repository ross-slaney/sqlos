---
name: model-shrbac-authorization
description: >-
  Model SqlOS SHRBAC (hierarchical FGA) before writing application code. Produces
  resource trees, permission keys, flat roles, and a grants strategy mapped to
  subject types. Use when designing authorization, RBAC, FGA, roles, permissions,
  grants, tenant scoping, or multi-tenant access control for SqlOS apps — especially
  before implementing endpoints, seeds, or integration tests.
---

# Model SHRBAC Authorization

Use this skill to **design authorization first**, then implement. SqlOS uses **SHRBAC** (Scoped Hierarchical Role-Based Access Control): a **resource tree** provides inheritance; **roles** are flat bundles of permissions; **grants** bind subject + role + resource node.

Do not scatter `CheckAccessAsync` calls until the model below is written and reviewed.

## Non-Negotiables

- Produce a written auth model **before** seed code, endpoints, or migrations.
- Model **one resource tree** that mirrors product ownership — not per-row role columns.
- Keep **roles flat** (no role hierarchy). Scope via **where** you grant on the tree.
- Name permissions `RESOURCETYPE_ACTION` in uppercase, scoped to one resource type.
- Plan **demo personas and integration tests** alongside the grants matrix.
- Prefer entity-backed resources (`ISqlOSResourceEntity` / `IHasResourceId`) for domain rows; manual resource APIs only for roots and special nodes.
- Derive the model from the **user's domain**, not from example apps. Example apps illustrate patterns only.

## SqlOS SHRBAC Primitives

| Primitive | Meaning |
| --- | --- |
| **Resource type** | Category of node (e.g. `workspace`, `project`). Permissions attach to types. |
| **Resource** | Node in a tree: id, parent, type, display name. |
| **Permission** | Capability key scoped to one resource type (`TYPE_VIEW`, `TYPE_EDIT`). |
| **Role** | Flat named bundle of permissions. No role hierarchy. |
| **Grant** | Subject + role + resource node. Permissions inherit **down** the tree. |
| **Subject** | Actor receiving grants: `user`, `group`, `agent`, or `service_account`. |

Docs: `web/content/docs/fga/*`, `web/content/docs/guides/model-fga.mdx`, `web/content/blog/developers-guide-to-hierarchical-rbac.mdx`.

## Worked Examples (read only when you need a concrete reference)

| Example | When to read it |
| --- | --- |
| [retail-example.md](retail-example.md) | Multi-entity hierarchy, groups, agents, service accounts, manual FGA seed |
| `examples/SqlOS.Todo.Api/README.md` | Minimal single-tenant app, `Fga.Seed` + entity sync |
| `paper/shrbac-compsac-2026.md` | Formal SHRBAC model |

Do not copy example domain nouns, roles, or grants into a new app. Extract the **pattern**.

## Planning Workflow

Copy this checklist and complete every step **for the target app** before implementation:

```
Auth model progress:
- [ ] 1. Actors (subject types)
- [ ] 2. Resource tree + types
- [ ] 3. Permissions per type
- [ ] 4. Roles (flat bundles)
- [ ] 5. Grants strategy
- [ ] 6. API operation → permission map
- [ ] 7. Demo personas + test scenarios
- [ ] 8. Implementation map
```

### Step 1 — Inventory actors

List every **subject type** that will receive grants in this app:

| Subject type | Use when |
| --- | --- |
| `user` | Human users authenticated via OAuth/SAML/etc. |
| `group` | Shared access for teams, departments, regions |
| `agent` | Background jobs, bots, AI agents |
| `service_account` | Machine-to-machine / API key integrations |

For each type, note: authentication mechanism and how the app resolves `subjectId` from `HttpContext`.

### Step 2 — Draw the resource tree

Start from **product nouns**, not database tables. Each node needs: **id**, **parent**, **resource type**, **display name**.

Template (replace placeholders with your domain):

```
root
└── {tenant_or_app_root}              ({root_type})
    ├── {container_a}                 ({container_type})
    │   ├── {leaf_a1}                 ({leaf_type})
    │   └── {leaf_a2}
    └── {container_b}
        └── {leaf_b1}
```

Rules:

- Target depth 3–5 for most SaaS; deeper only when the product genuinely nests further.
- Every protected domain row links via `ResourceId` (`IHasResourceId` or `ISqlOSResourceEntity`).
- Grant **high** on the tree for broad admin access; grant **low** for least privilege.
- Ask: *"Which node is the natural tenant/org boundary?"* — that is usually where admin grants land.

### Step 3 — Define permissions

One permission = one capability on **one** resource type. Convention: `RESOURCETYPE_ACTION` in uppercase.

Template:

| Key | Resource type | Action |
| --- | --- | --- |
| `{TYPE}_VIEW` | `{type}` | Read/list/detail |
| `{TYPE}_EDIT` | `{type}` | Create/update/delete |

Avoid generic keys (`READ`, `ADMIN`) — they hide which resource type applies.

For **creates**, decide which **parent** resource the permission is checked against (e.g. create child under parent → check `{PARENT_TYPE}_EDIT` or a dedicated `{CHILD_TYPE}_CREATE` on the parent node).

### Step 4 — Define flat roles

Roles bundle permissions. **Do not nest roles.** Inheritance comes from the resource tree, not role hierarchy.

Template:

| Role key | Display name | Permissions | Intended scope when granted |
| --- | --- | --- | --- |
| `{scope}_admin` | ... | all types at this level | App/tenant root |
| `{scope}_manager` | ... | manage children, view parent | Container nodes |
| `{scope}_viewer` | ... | view only | Leaf or container nodes |

For each role, answer: *"If granted on node X, what can this subject do on X and all descendants?"*

Avoid role explosion (`TenantA-Editor`, `TenantB-Editor`). One role, many grants at different nodes.

### Step 5 — Plan grants

A grant is **(subject, role, resource node)**. Permissions flow **down** the tree.

Template:

| Persona | Subject type | Role | Resource node | Rationale |
| --- | --- | --- | --- | --- |
| Org admin | user | `{admin_role}` | `{app_root}` | Full subtree access |
| Scoped manager | user | `{manager_role}` | `{container_id}` | One branch only |
| Read-only member | user | `{viewer_role}` | `{leaf_id}` | Single resource |
| Team | group | `{manager_role}` | `{container_id}` | Shared branch access |
| Automation | agent / service_account | `{role}` | `{node}` | Machine actor scope |

Include at least one **no-grants** persona for negative tests.

Optional: time-bounded grants via `EffectiveFrom` / `EffectiveTo` on `SqlOSFgaGrant`.

### Step 6 — Map API operations to permissions

Before coding endpoints, fill this table for **your routes**:

| Operation | HTTP route | Permission | Resource checked |
| --- | --- | --- | --- |
| List `{entities}` | GET /api/... | `{TYPE}_VIEW` | per-row (spec filter) |
| Get one | GET /api/.../{id} | `{TYPE}_VIEW` | row.ResourceId |
| Create | POST /api/... | `{TYPE}_EDIT` or parent edit | parent ResourceId |
| Update / delete | PUT/DELETE /api/.../{id} | `{TYPE}_EDIT` | row.ResourceId |

Enforcement patterns in SqlOS:

- **Lists**: `fga.BuildFilterAsync<T>(subjectId, permission)` composed into the EF query with `.Where(filter)`; pagination, sorting, search, and projection are plain EF in the endpoint
- **Detail**: `authService.AuthorizedDetailAsync(query, predicate, subjectId, permission, mapper)`
- **Mutations**: `authService.CheckAccessAsync(subjectId, permission, resourceId)` before `SaveChanges`

### Step 7 — Demo personas and tests

Define personas that **prove the grants matrix** for this app. Each persona should have explicit allow and deny cases.

Write test names as **Given persona → action → expected allow/deny** before writing test code.

Minimum coverage:

- One broad admin persona (root grant)
- One scoped persona (single branch or leaf)
- One sibling-denial case (can access A, cannot access B)
- One child-inheritance case (grant on parent, action on descendant)
- One no-grants denial
- Non-user subjects if the app uses groups, agents, or service accounts

See [retail-example.md](retail-example.md) for how a complex example app structures its integration tests — adapt the **scenarios**, not the domain.

### Step 8 — Implementation map

Translate the design doc to SqlOS artifacts:

| Design artifact | SqlOS implementation |
| --- | --- |
| Resource types | `options.Fga.Seed(seed => seed.ResourceType(...))` or `SqlOSFgaSeedData.ResourceTypes` |
| Permissions | `seed.Permission(...)` with resource type id |
| Roles + role-permissions | `seed.Role(...)` + `seed.RolePermission(roleKey, permKey)` |
| Resource tree | `ProvisionResourceWithIdAsync` / `CreateResource` / EF sync via `ISqlOSResourceEntity` |
| Grants | `GrantRoleAsync` or `SqlOSFgaGrant` rows in seed |
| Key constants | `{App}PermissionKeys.cs`, `{App}RoleKeys.cs`, `{App}ResourceTypeIds.cs` |
| Subject provisioning | `ProvisionUserSubjectAsync`, `CreateAgentAsync`, `CreateServiceAccountAsync`, groups |

Prefer startup `Fga.Seed` + entity sync for new apps. Manual resource seeding is fine when you need to expose every FGA layer explicitly (see retail example).

## Design Document Template

Deliver this markdown (in issue, PR description, or `.agents/` draft) before coding:

```markdown
# [App Name] SHRBAC Model

## Resource tree
[ASCII diagram using this app's nouns]

## Resource types
| Type id | Display name | Domain entity |

## Permissions
| Key | Type | Action | Used by endpoints |

## Roles
| Key | Display name | Permissions |

## Grants plan
| Persona | Subject type | Role | Resource node | Rationale |

## API authorization map
| Route | Permission | Resource resolution |

## Test personas
| Persona | Expected allow | Expected deny |
```

## Common Mistakes

| Mistake | Fix |
| --- | --- |
| Role per tenant (`Acme-Editor`, `Beta-Editor`) | One role; scope with grants on different tree nodes |
| Copying example app roles/permissions verbatim | Model from product requirements; use examples for structure only |
| Permission checks without resource id | Always pass the row's `ResourceId` or parent for creates |
| Skipping list filters | Use `RequirePermission` on specs — don't filter in memory |
| Org membership = FGA access | AuthServer org role ≠ FGA grant; provision subject + grant explicitly |
| Deep trees without typed permissions | Each level gets its own resource type and permission pair |

## Validation

Before marking auth work complete:

1. Open `/sqlos/admin/fga/` — tree, roles, grants match the design doc.
2. Use the FGA access tester for one allow and one deny case per role.
3. Run integration tests for every persona in the grants plan.
4. Confirm no endpoint returns data without a permission check or spec filter.
