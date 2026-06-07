# Audit Logs

SqlOS Audit Logs are append-only governance records for security, compliance, and support review. They answer who or what acted, what happened, which organization and application were involved, what target resources changed, when the event occurred, where it came from, and what non-sensitive metadata explains the outcome.

Audit logs are not application logs, metrics, traces, or analytics. Use normal logs for debugging detail, metrics for counters and latency, traces for request causality, and analytics for product behavior. Audit events should be durable, structured, bounded, and safe for operators to inspect or export.

## Event Model

Host applications can inject `ISqlOSAuditLogService` after `AddSqlOS<TContext>()` and record application events:

```csharp
await auditLogs.RecordAsync(new SqlOSAuditLogRecordRequest(
    Action: "document.shared",
    OrganizationId: organizationId,
    ApplicationKey: "workspace-web",
    Source: "application",
    Actor: new SqlOSAuditActor("user", userId, "Jane Doe"),
    Targets: [new SqlOSAuditTarget("document", documentId, "Contract.pdf")],
    Context: SqlOSAuditContext.FromHttpContext(httpContext),
    Metadata: new Dictionary<string, object?>
    {
        ["result"] = "success",
        ["role"] = "viewer"
    },
    IdempotencyKey: $"document:{documentId}:shared:{userId}"));
```

Use dot-delimited, past-tense or outcome-specific action names such as `user.login.succeeded`, `client.disabled`, `retail.inventory_item.updated`, or `document.shared`. Keep names stable because operators will filter and export by action.

Actors should model the principal that performed the action. Common actor types are `user`, `client`, `service_account`, `agent`, `dashboard`, and `system`. Targets should model affected resources such as `organization`, `client`, `document`, `location`, or `inventory_item`.

## Application Scoping

Use `ApplicationKey` for host-provided application identifiers such as `northwind-retail` or `workspace-web`. When the key matches a registered SqlOS OAuth client `ClientId`, SqlOS also stores the client row id as `ApplicationId`, enabling client-scoped filtering. You can set `ApplicationId` directly when you already have the registered client id.

Auth-server compatibility writes through `SqlOSAdminService.RecordAuditAsync` now flow through the central service with `Source = "authserver"`. Older code that still writes `SqlOSAuditEvent` rows directly remains queryable through compatibility fields.

## Metadata Safety

Metadata must be non-secret. Do not include passwords, access tokens, refresh tokens, API keys, client secrets, raw authorization headers, cookies, private keys, raw exception payloads, stack traces, full request bodies, or response bodies. The service redacts common secret-like metadata keys, but callers are still responsible for only sending safe metadata.

Prefer short outcome fields such as:

- `result`: `success`, `failed`, `denied`
- `reason`: stable machine-readable reason code
- resource counts, role names, previous and new non-sensitive values

## Idempotency

`IdempotencyKey` is hashed before storage. If a caller retries the same event with the same key, `RecordAsync` returns the original event and does not insert a duplicate. Include the application key, action, target id, actor id, and request id when building keys for retry-safe writes.

## Dashboard And Export

The embedded dashboard has a top-level **Governance > Audit Logs** section. Operators can filter by organization, application/client, source, action, actor type/id, target type/id, result/status metadata, free text, and date range. Rows are sorted by `OccurredAt` descending and are paginated.

Selecting a row opens structured details for actor, targets, context, and metadata. CSV export uses the same filters and is bounded to at most 5,000 rows and a maximum 366-day date range. If no export dates are supplied, export defaults to the last 30 days.

The old Auth Server audit route reuses the central view filtered to `source=authserver`.

## Retention And Privacy

Treat audit logs as sensitive operational data. Restrict dashboard and API access with the same controls used for other SqlOS admin surfaces. Keep rows append-only from public/service APIs. If your environment needs deletion, retention, or legal hold workflows, implement them as explicit administrative or maintenance behavior.

Choose retention based on customer contracts, regulatory needs, and storage cost. Common deployments keep audit rows for 90 days to one year, then archive or delete them through a scheduled maintenance process.

## Retail Example

The Retail example application records application audit events with `ApplicationKey = "northwind-retail"` for successful chain, location, and inventory mutations. Try creating, editing, restocking, or deleting inventory in the local Retail app, then open the SqlOS dashboard and filter Audit Logs by application key `northwind-retail` or source `application`.

Inventory events include targets for the location and inventory item plus safe metadata such as SKU, quantity, price, and stock delta. This demonstrates host-app ingestion without requiring the frontend to call audit APIs directly.
