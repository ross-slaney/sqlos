# Custom role validation

These screenshots show the **SqlOS operator dashboard**, backed by the real management service and authorization engine in a synthetic local fixture. They are not ChecklistSquad product screens or proof that its checklist/template operations are integrated. The browser fixture uses EF InMemory; separate SQL Server and PostgreSQL tests cover actual persistence, transactions, SQL authorization functions and sparse paging.

Reproduce with `dotnet run --project tests/SqlOS.CustomRoleBrowserFixture`, then open `http://127.0.0.1:5198/sqlos`. This fixture binds only to loopback, uses disposable synthetic data and development dashboard authorization, and resets on restart. Its permission vocabulary illustrates the requested roles; its generic resource is not a running ChecklistSquad checklist.

| Browser action | Observed result | Evidence |
| --- | --- | --- |
| View built-in and three customer roles | Built-in is application-owned; custom definitions belong to Acme | [Role list](role-list.jpg) |
| Create Cleaning supervisor and grant to Supervisors | Three permissions; organization-scoped squad grant; revision visible | [Definition and grant](cleaning-supervisor.jpg) |
| Try granting Acme's role on another organization | Rejected; no grant added | [Foreign target denied](foreign-organization-denied.jpg) |
| Add reserved ROLES_MANAGE permission | Rejected; revision and definition unchanged | [Reserved permission denied](reserved-permission-denied.jpg) |
| Sam, through Supervisors, checks CHECKLIST_FINISH | Allowed; trace identifies squad, role and ancestor | [Sam allowed](sam-finish-allowed.jpg) |
| Jamie checks CHECKLIST_FINISH | Denied; Relief supervisor contains no finish permission | [Jamie denied](jamie-finish-denied.jpg) |
| Jamie checks CHECKLIST_DUE_EDIT | Allowed through a direct grant on the organization | [Jamie allowed](jamie-due-allowed.jpg) |
| Pat, through Procedures, checks SCHEDULE_EDIT | Allowed through Procedure manager | [Pat allowed](pat-repeat-allowed.jpg) |
| Pat checks CHECKLIST_ASSIGN | Denied; no work-assignment permission | [Pat denied](pat-assign-denied.jpg) |
| Retire Relief supervisor | Archived definition, incremented revision, grant removed, mutation controls absent | [Retired](relief-retired.jpg) |

The fixture was restarted while improving the dashboard, so generated role IDs differ between captures. No production users, credentials or data appear in them.

Automated coverage additionally exercises all three roles' selected and omitted capabilities, foreign actors and recipients, startup reconciliation, stale edits, membership removal, independent ownership grants, archived-role resurrection attempts, raw foreign grants, resource moves, service/dashboard parity, unauthenticated and CSRF rejection, bounded role lists, concurrent writers, audit rollback, and composing host offboarding with grant revocation in one transaction. Exact final commands and results belong in the PR description.

ChecklistSquad acceptance remains separate: actual signed-in persona journeys, checklist and template mutations, recurring ownership, offline recovery, assistant/platform tools, help pages, six surfaces and its own coverage/E2E gates.
