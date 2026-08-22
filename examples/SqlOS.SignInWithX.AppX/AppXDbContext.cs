using Microsoft.EntityFrameworkCore;
using SqlOS;

namespace SqlOS.SignInWithX.AppX;

/// <summary>
/// App X owns no application tables — it exists purely as an OpenID Provider,
/// so its context carries only the SqlOS identity/authorization model.
/// </summary>
public sealed class AppXDbContext(DbContextOptions<AppXDbContext> options)
    : SqlOSDbContext<AppXDbContext>(options);
