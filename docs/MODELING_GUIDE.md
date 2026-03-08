# SHRBAC Modeling Guide

## LLM Prompt for Modeling Your App

Use this prompt with an LLM to generate an SHRBAC authorization model for your application:

```
You are helping me model authorization for an application using Sqlzibar (SHRBAC).

SHRBAC rules (must follow):
- Resources form a rooted TREE (single parent; no DAG).
- Grants: (subject, role, resource_scope, optional time window).
- Roles are flat; roles map to atomic permissions.
- Sharing/collab is done by EXTRA GRANTS, not multiple parents.
- List filtering must be expressible as: WHERE EXISTS(IsResourceAccessible(row.ResourceId, subjectIds, perm, now)).

IMPORTANT STYLE REQUIREMENTS (follow these strictly):
1) Keep it simple and readable: aim for ~1 page of output.
2) Use AT MOST 6 resource types total. Prefer fewer.
3) Do NOT introduce "logical leaves" unless absolutely necessary.
   - Default rule: every database row type that needs authorization has a ResourceId.
   - Only map a table to a parent ResourceId if there are millions of rows and per-row ACL is not needed.
4) Use AT MOST 8 permissions total (atomic verbs), and AT MOST 5 roles.
5) Prefer 1–2 canonical sharing patterns (e.g., "joint account is its own subtree" + "share by grant on the resource").
6) Output should be "starter architecture" someone can build immediately.

TASK:
Given the app description below, output a SHRBAC model design.

OUTPUT FORMAT (exact):
1) One-paragraph summary of the authorization approach for this app.
2) Resource Tree (ASCII) with 3–6 levels max.
3) Tables → ResourceId mapping (bullets).
4) Roles and permissions (small table or bullets).
5) Sharing/joint ownership modeling (2–4 bullets).
6) Three example queries:
   - list endpoint (authorized + paged)
   - get one (point check)
   - mutation (create/update with check)
7) 5 quick "design rules" for the dev team (short bullets).

NOW model this app:

[PASTE APP SUMMARY HERE]

```

## Sample: Personal Finance App

This section shows how to model a personal finance app (banking, brokerage, joint accounts) with SHRBAC. The key insight: **joint ownership is expressed by grants, not by multiple parents** — keeping the resource tree a true tree.

### 1. Resource Tree

Use a single containment tree rooted at the tenant/org (or "platform") boundary, then hang users and accounts under it.

**Example resource types (RT):**

- `platform_root`
- `tenant` (optional if multi-tenant B2B; if consumer, tenant can be "household" or just platform)
- `customer` (the user's profile/identity "space")
- `portfolio` (brokerage portfolio)
- `account` (savings, checking, brokerage sub-account)
- `subaccount` (optional: cash, margin, positions)
- `instrument` (optional)
- `transaction`
- `statement`
- `beneficiary` / `payee`
- `transfer`

**Example tree:**

```
platform_root
  └── tenant/{tenantId}                       (or omit tenant for pure consumer)
      ├── customer/{aliceCustomerId}
      │    ├── portfolio/{alicePortId}
      │    │    ├── account/{aliceBrokerageAcctId}
      │    │    ├── positions/{...}
      │    │    └── statements/{...}
      │    └── profile/{...}
      ├── customer/{bobCustomerId}
      │    └── ...
      └── joint_account/{jointSavingsId}
           ├── transactions/{...}
           ├── statements/{...}
           └── beneficiaries/{...}
```

**Key point:** The joint account is its own subtree. It is not "under Alice" or "under Bob." That keeps containment a tree and makes joint ownership just… grants.

### 2. Principals and Joint Accounts

**Principals:**

- **User subjects:** AliceUserId, BobUserId
- **Group subjects** (optional): e.g., `household/{id}`, `tenant_admins/{id}`
- **Agent subjects:** robo-advisor, reconciliation worker, statement generator

**Joint ownership pattern**

For joint savings, you do two grants to the joint account node:

- `(AliceUserId, JOINT_OWNER, joint_account/{id}, [tfrom,tto])`
- `(BobUserId, JOINT_OWNER, joint_account/{id}, [tfrom,tto])`

No DAG needed. No multi-parenting.

### 3. Roles and Permissions

Keep roles flat and permissions atomic per resource type.

**Savings / banking permissions**

| Permission              | Description                    |
| ----------------------- | ------------------------------ |
| `ACCOUNT_VIEW`          | View account                   |
| `ACCOUNT_DEPOSIT`       | Deposit funds                  |
| `ACCOUNT_WITHDRAW`      | Withdraw funds                 |
| `ACCOUNT_TRANSFER_OUT`  | Transfer out                   |
| `ACCOUNT_MANAGE_PAYEES` | Manage payees/beneficiaries    |
| `ACCOUNT_CLOSE`         | Close account                  |

**Brokerage permissions**

| Permission           | Description              |
| -------------------- | ------------------------ |
| `PORTFOLIO_VIEW`     | View portfolio           |
| `TRADE_PLACE_ORDER`  | Place orders             |
| `TRADE_CANCEL_ORDER` | Cancel orders            |
| `VIEW_STATEMENTS`    | View statements          |
| `DOWNLOAD_TAX_DOCS`  | Download tax documents   |

**Admin / support permissions** (careful)

| Permission               | Description                                      |
| ------------------------ | ------------------------------------------------ |
| `SUPPORT_READ_ONLY`      | Read-only support access                         |
| `SUPPORT_LIMITED_ACTIONS`| Limited actions (e.g., reset 2FA, reissue statement) |
| `COMPLIANCE_AUDIT`       | Compliance audit access                          |

**Roles (R) map to permission sets:**

| Role               | Permissions                                                       |
| ------------------ | ----------------------------------------------------------------- |
| `SAVINGS_OWNER`    | view + withdraw + transfer + manage payees                        |
| `SAVINGS_VIEWER`   | view only                                                         |
| `JOINT_OWNER`      | same as savings owner, maybe with extra constraints               |
| `BROKERAGE_OWNER`  | view + trade + statements                                        |
| `BROKERAGE_VIEWER` | view + statements                                                |
| `SUPPORT_READONLY` | read-only across tenant/customer scopes (but scoped!)             |
| `AGENT_REBALANCER` | trade permissions, tightly scoped + time-bounded                 |

### 4. Common Access Rules as SHRBAC Grants

**Personal (single-owner) savings account**

Put the account under either the customer subtree (simple) or a separate "accounts" subtree under tenant. Either way, grant the owner on the account node:

```
(AliceUserId, SAVINGS_OWNER, account/{aliceSavingsId}, ⊥, ⊥)
```

**Joint savings account**

```
(AliceUserId, JOINT_OWNER, joint_account/{id}, ⊥, ⊥)
(BobUserId, JOINT_OWNER, joint_account/{id}, ⊥, ⊥)
```

If you want "view-only joint holder":

```
(BobUserId, SAVINGS_VIEWER, joint_account/{id}, ⊥, ⊥)
```

**Household-level shared view** (optional)

If you want "household members can view all household accounts" without per-account grants:

1. Create a group principal `household/{hid}`
2. Add users to that group
3. Grant the group at the household root scope (could be tenant child):

```
(household/{hid}, HOUSEHOLD_VIEWER, household_scope/{hid}, ⊥, ⊥)
```

You may not even need household groups if joint is the only sharing mechanism.

### 5. List Filtering Examples

**"List accounts Alice can view"**

Query the Accounts table and filter with the TVF on `Account.ResourceId` + `ACCOUNT_VIEW`. Because joint account is its own subtree and Alice has a grant there, it shows up naturally.

**"List transactions in a joint account"**

Transactions live under the joint account subtree, so the `JOINT_OWNER` grant cascades.

### 6. Edge Cases (Finance-Specific, SHRBAC-Friendly Patterns)

**A) "Authorized users can invite another joint holder"**

That's just a permission like `ACCOUNT_MANAGE_OWNERS`. Grant it only to `JOINT_OWNER` at the joint_account node.

**B) "Power of attorney / advisor access"**

Model advisor as a user or `service_account` subject. Grant them `ADVISOR_VIEWER` on a specific customer subtree or account subtree. Time-bound it.

**C) Break-glass support access**

Create support principals/groups and scope them to tenant root (B2B) or to a "support case scope" node created per case, then grant support there with time bounds.

**D) Compliance/audit**

Auditors should get read-only role, scoped to tenant root or customer root, time-bounded.

### 7. Why This Avoids the "DAG" Trap

You keep containment as the system-of-record tree: **a resource has one parent.**

Sharing is expressed by additional grants. Joint ownership is not "two parents." It's "two subjects granted on one resource."

That's exactly what SHRBAC wants.
