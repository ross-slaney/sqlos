using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SqlOS.Extensions;
using SqlOS.Fga.Models;
using SqlOS.Fga.Services;
using SqlOS.Fga.Configuration;
using SqlOS.Tests.Infrastructure;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
builder.WebHost.UseUrls("http://127.0.0.1:5198");
builder.Services.AddDbContext<TestSqlOSInMemoryDbContext>(db => db.UseInMemoryDatabase("role-browser-validation"));
builder.Services.AddSqlOS<TestSqlOSInMemoryDbContext>(o =>
{
    o.Fga.CustomRoles.ManagePermission = "ROLES_MANAGE";
    o.Fga.CustomRoles.OwnerResourceTypes.Add("organization");
    o.Fga.CustomRoles.IsEligibleRecipientAsync = (_, owner, subject, _) => Task.FromResult(owner == "acme" && new[] { "supervisors", "procedures", "jamie" }.Contains(subject));
    o.Fga.Seed(s =>
    {
        s.ResourceType("organization", "Organization").ResourceType("checklist", "Checklist");
        s.Permission("ROLES_MANAGE", "Manage custom roles", "organization");
        s.Role("org_admin", "Organization administrator").Can("ROLES_MANAGE");
        foreach (var (key, name) in new[] { ("CHECKLIST_ASSIGN", "Assign work"), ("CHECKLIST_DUE_EDIT", "Change due dates"), ("CHECKLIST_FINISH", "Finish checklists"), ("TEMPLATE_CREATE", "Create templates"), ("TEMPLATE_EDIT", "Edit templates"), ("SCHEDULE_EDIT", "Manage repeats and weekday assignments") })
            s.Permission(key, name, "checklist").AllowInCustomRoles(key);
    });
});
builder.Services.RemoveAll<IHostedService>();
var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TestSqlOSInMemoryDbContext>();
    var seed = scope.ServiceProvider.GetRequiredService<SqlOSFgaSeedService>();
    await seed.SeedCoreAsync();
    var options = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SqlOSFgaOptions>>().Value;
    await seed.SeedAuthorizationDataAsync(options.StartupSeedData!);
    db.Set<SqlOSFgaResource>().AddRange(new() { Id = "acme", Name = "Acme Facilities", ResourceTypeId = "organization", ParentId = "root" }, new() { Id = "other", Name = "Other organization", ResourceTypeId = "organization", ParentId = "root" }, new() { Id = "evening-clean", Name = "Evening clean", ResourceTypeId = "checklist", ParentId = "acme" });
    foreach (var user in new[] { "admin", "sam", "pat", "jamie" }) await db.ProvisionUserSubjectAsync(user, user);
    foreach (var (group, member) in new[] { ("supervisors", "sam"), ("procedures", "pat") })
    {
        db.Set<SqlOSFgaSubject>().Add(new() { Id = group, SubjectTypeId = "group", DisplayName = group });
        db.Set<SqlOSFgaUserGroup>().Add(new() { Id = group, SubjectId = group, Name = group });
        db.Set<SqlOSFgaUserGroupMembership>().Add(new() { UserGroupId = group, SubjectId = member });
    }
    await db.SaveChangesAsync();
    await db.GrantRoleAsync("admin", "acme", "org_admin"); await db.SaveChangesAsync();
    var roles = scope.ServiceProvider.GetRequiredService<SqlOSCustomRoleService>();
    foreach (var (name, permissions, subject) in new[] {
        ("Cleaning supervisor",new[]{"CHECKLIST_ASSIGN","CHECKLIST_DUE_EDIT","CHECKLIST_FINISH"},"supervisors"),
        ("Procedure manager",new[]{"TEMPLATE_CREATE","TEMPLATE_EDIT","SCHEDULE_EDIT"},"procedures"),
        ("Relief supervisor",new[]{"CHECKLIST_ASSIGN","CHECKLIST_DUE_EDIT"},"jamie") })
    {
        var role = await roles.CreateAsync("admin", "acme", new(name, null, permissions));
        await roles.GrantAsync("admin", "acme", role.Id, role.Revision, subject, "acme");
    }
}
await app.RunAsync();
