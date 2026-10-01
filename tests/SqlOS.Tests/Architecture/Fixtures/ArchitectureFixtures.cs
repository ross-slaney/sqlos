using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Interfaces;
using SqlOS.Domain;
using SqlOS.Tests.Architecture.Fixtures.Model;
using SqlOS.Tests.Architecture.Fixtures.Proofs;

// Code with known violations for ArchitectureRuleSelfTests. Each rule must find exactly these, so a
// rule that silently finds nothing fails its self-test. Nothing calls this code.
#pragma warning disable IDE0051, IDE0060

namespace SqlOS.Tests.Architecture.Fixtures.Model
{
    public sealed class FixtureToken
    {
        public string Name { get; set; } = string.Empty;

        public DateTime ExpiresAt { get; set; }

        public DateTime? ConsumedAt { get; set; }

        public bool IsActive { get; private set; }

        public string Owner { get; init; } = string.Empty;

        // The entity writes its own lifecycle columns: allowed.
        internal void Consume(DateTime now) => ConsumedAt = now;
    }
}

namespace SqlOS.Tests.Architecture.Fixtures.Services
{
    internal static class FixtureTokenService
    {
        public static void Consume(FixtureToken token) => token.ConsumedAt = DateTime.UnixEpoch;

        public static void Rename(FixtureToken token) => token.Name = "renamed";

        public static async Task ExpireAsync(FixtureToken token)
        {
            await Task.Yield();
            token.ExpiresAt = DateTime.UnixEpoch;
        }

        public static IQueryable<FixtureToken> Expired(IQueryable<FixtureToken> tokens)
            => tokens.Where(token => token.ExpiresAt < DateTime.UnixEpoch);

        public static Task<int> BulkConsumeAsync(IQueryable<FixtureToken> tokens)
            => tokens.ExecuteUpdateAsync(setters => setters.SetProperty(token => token.ConsumedAt, DateTime.UnixEpoch));

        public static Task<int> SaveAsync(DbContext context) => context.SaveChangesAsync();

        public static Func<Task<int>> SaveLater(ISqlOSAuthServerDbContext context) => () => context.SaveChangesAsync();

        public static FixtureProof Forge() => new("forged");

        public static FixtureProof Rewrite(FixtureProof proof) => proof with { Subject = "rewritten" };
    }
}

namespace SqlOS.Tests.Architecture.Fixtures.Processes
{
    internal static class FixtureProcess
    {
        public static Task<int> SaveAsync(DbContext context) => context.SaveChangesAsync();

        public static DateTimeOffset ReadClock(TimeProvider time) => time.GetUtcNow();

        public static DateTime ReadSystemClock() => DateTime.Now;
    }
}

namespace SqlOS.Tests.Architecture.Fixtures.Endpoints
{
    internal static class FixtureEndpoints
    {
        public static int CountTokens(ISqlOSAuthServerDbContext context) => context.Set<FixtureToken>().Count();

        public static int Clean(int value) => value + 1;

        public static async Task<int> CountLaterAsync(IServiceProvider services)
        {
            await Task.Yield();
            return services.GetService(typeof(DbContext)) is DbContext context ? context.ChangeTracker.Entries().Count() : 0;
        }
    }
}

namespace SqlOS.Tests.Architecture.Fixtures.Domain
{
    internal sealed class FixtureDomainCode
    {
        private readonly HttpContext? _context;

        public FixtureDomainCode(HttpContext? context) => _context = context;

        public static DateTime Now() => DateTime.UtcNow;

        public static DateTimeOffset FromProvider() => TimeProvider.System.GetUtcNow();

        public static string? Path(HttpContext context) => context.Request.Path.Value;

        public static DateTime Pure(DateTime now) => now.AddMinutes(5);
    }
}

namespace SqlOS.Tests.Architecture.Fixtures.Aggregates
{
    internal sealed class FixtureRoot
    {
        private readonly List<FixtureMember> _members = [];

        // The root creates and changes its members: allowed.
        public FixtureMember Add(string name)
        {
            var member = FixtureMember.Create(name);
            member.Rename(name + "!");
            _members.Add(member);
            return member;
        }
    }

    internal sealed class FixtureMember
    {
        private FixtureMember(string name) => Name = name;

        public string Name { get; private set; }

        // A member builds itself: allowed.
        internal static FixtureMember Create(string name) => new(name);

        internal void Rename(string name) => Name = name;
    }

    internal static class FixtureMemberService
    {
        // Reading a member: allowed.
        public static string Read(FixtureMember member) => member.Name;

        public static FixtureMember Forge() => FixtureMember.Create("forged");

        public static void Rename(FixtureMember member) => member.Rename("bypassed");
    }
}

namespace SqlOS.Tests.Architecture.Fixtures.Proofs
{
    internal sealed record FixtureProof : ISqlOSProof
    {
        internal FixtureProof(string subject) => Subject = subject;

        public string Subject { get; init; }
    }

    internal sealed class FixtureProducer
    {
        public static FixtureProof Prove(string subject) => new(subject);

        public static FixtureProof Narrow(FixtureProof proof) => proof with { Subject = proof.Subject + "!" };

        public static FixtureGuarded Bypass() => new(null);
    }

    internal sealed class FixtureGuarded
    {
        internal FixtureGuarded(FixtureProof? proof) => Proof = proof;

        public FixtureProof? Proof { get; }

        public static FixtureGuarded Create(FixtureProof proof) => new(proof);

        public static FixtureGuarded Leak() => new(null);
    }

    public class FixtureLeakyProof : ISqlOSProof
    {
        public FixtureLeakyProof()
        {
        }

        public static FixtureLeakyProof Create() => new();
    }

    internal sealed class LoginEvidence
    {
    }
}

#pragma warning restore IDE0051, IDE0060
