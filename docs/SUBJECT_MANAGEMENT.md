# Subject Management & Data Access

## Subjects

```csharp
var subjectService = services.GetRequiredService<ISqlzibarSubjectService>();

// Create a subject
var subject = await subjectService.CreateSubjectAsync(
    displayName: "Alice Smith",
    subjectTypeId: "user");

// Create a group
var group = await subjectService.CreateGroupAsync(
    name: "Engineering Team",
    description: "All engineers");

// Add to group (only users/service accounts — groups cannot contain groups)
await subjectService.AddToGroupAsync(subject.Id, group.Id);

// Resolve all IDs for authorization (user + their groups)
List<string> allIds = await subjectService.ResolveSubjectIdsAsync(subject.Id);
// Returns: [subject.Id, group.SubjectId]
```

> **No nested groups.** Only subjects of type `user` or `service_account` can be members of groups. Attempting to add a group to another group throws `InvalidOperationException`.

## Data Access & Integration

Your DbContext has full EF Core access to all Sqlzibar tables. You can query them directly, use `Include` for navigation, and link your own entities to Sqlzibar entities via foreign keys.

### Querying Sqlzibar Entities

```csharp
// Query subjects directly
var subject = await _context.Set<SqlzibarSubject>()
    .Include(p => p.Grants)
    .FirstOrDefaultAsync(p => p.Id == subjectId);

// Include Sqlzibar data when querying your entities
var user = await _context.AuthUsers
    .Include(u => u.Subject)  // Subject is SqlzibarSubject
    .FirstOrDefaultAsync(u => u.Email == email);
```

### Creating a User (Subject + Your Entity)

When creating a new user, create the Sqlzibar subject first, then your user entity with the subject reference:

```csharp
// 1. Create the Sqlzibar subject (or use ISqlzibarSubjectService.CreateSubjectAsync)
var subject = new SqlzibarSubject
{
    Id = $"subj_{Guid.NewGuid():N}",
    SubjectTypeId = "user",
    DisplayName = $"{firstName} {lastName}",
    CreatedAt = DateTime.UtcNow,
    UpdatedAt = DateTime.UtcNow
};
_context.Set<SqlzibarSubject>().Add(subject);

// 2. Create your user entity with FK to the subject
var user = new AuthUser
{
    Id = Guid.NewGuid().ToString(),
    Email = email,
    SubjectId = subject.Id,
    FirstName = firstName,
    LastName = lastName,
    // ...
};
_context.AuthUsers.Add(user);

await _context.SaveChangesAsync();
```

### Linking Your Entities to Sqlzibar

Your entities can reference Sqlzibar entities via foreign keys. Configure the relationship in `OnModelCreating`:

```csharp
entity.HasOne(e => e.Subject)
    .WithMany()
    .HasForeignKey(e => e.SubjectId)
    .OnDelete(DeleteBehavior.Restrict);
```

Sqlzibar tables are created by the library (via schema initialization). Once they exist, you use them like any other EF Core entity set.
