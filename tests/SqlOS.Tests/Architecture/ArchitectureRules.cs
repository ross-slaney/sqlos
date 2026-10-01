using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace SqlOS.Tests.Architecture;

/// <summary>
/// The architecture rules of <c>docs/architecture/domain-model.md</c> §11, as scans that return
/// every violation as a sorted list of lines. <see cref="ArchitectureTests"/> runs them on SqlOS
/// against the allowlists; <see cref="ArchitectureRuleSelfTests"/> runs them on fixtures with
/// known violations, so no rule can pass by finding nothing.
/// </summary>
internal static class ArchitectureRules
{
    /// <summary>The types that are, or give access to, a SqlOS database context.</summary>
    public static readonly TypeFamily DatabaseTypes = new(
        "Microsoft.EntityFrameworkCore.DbContext",
        "SqlOS.AuthServer.Interfaces.ISqlOSAuthServerDbContext",
        "SqlOS.Fga.Interfaces.ISqlOSFgaDbContext");

    private static readonly TypeFamily DbContexts = new("Microsoft.EntityFrameworkCore.DbContext");

    private static readonly Dictionary<string, string> ClockReads = new(StringComparer.Ordinal)
    {
        ["System.DateTime::get_Now"] = "DateTime.Now",
        ["System.DateTime::get_UtcNow"] = "DateTime.UtcNow",
        ["System.DateTime::get_Today"] = "DateTime.Today",
        ["System.DateTimeOffset::get_Now"] = "DateTimeOffset.Now",
        ["System.DateTimeOffset::get_UtcNow"] = "DateTimeOffset.UtcNow"
    };

    // Processes read TimeProvider once per execution; domain code takes `now` and reads no clock.
    private static readonly Dictionary<string, string> TimeProviderReads = new(StringComparer.Ordinal)
    {
        ["System.TimeProvider::get_System"] = "TimeProvider.System",
        ["System.TimeProvider::GetUtcNow"] = "TimeProvider.GetUtcNow",
        ["System.TimeProvider::GetLocalNow"] = "TimeProvider.GetLocalNow"
    };

    /// <summary>The proof types §3.5 names; each must be an <c>ISqlOSProof</c> once it exists.</summary>
    public static readonly string[] DesignProofNames = ["LoginEvidence", "OwnershipProof", "LoginDecision", "GrantAuthority", "DnsProof"];

    /// <summary>Rule 1: properties of entity types with a public (or init) setter.</summary>
    public static IReadOnlyList<string> PublicSetters(IEnumerable<Type> entityTypes)
        => entityTypes
            .SelectMany(type => type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(property => property.SetMethod?.IsPublic == true)
                .Select(property => $"{type.FullName}.{property.Name}"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>The columns of the lifecycle parts (§3.4): expiry, consumption, revocation, attempts, verification and enablement.</summary>
    public static bool IsLifecycleColumn(string property)
        => property is "ConsumedAt" or "RevokedAt" or "RevocationReason" or "AttemptCount"
                or "IsVerified" or "VerifiedAt" or "IsActive" or "IsEnabled" or "DisabledAt" or "DisabledReason"
            || property.EndsWith("ExpiresAt", StringComparison.Ordinal);

    /// <summary>
    /// Rule 2: writes to an entity's lifecycle columns from outside that entity, through a setter or
    /// an EF bulk update (<c>ExecuteUpdate</c>). A bulk update is attributed to every lifecycle
    /// column its method names, which can over-report but never misses one.
    /// </summary>
    public static IReadOnlyList<string> LifecycleWrites(IlScanner scanner, IReadOnlySet<string> entityTypeNames)
    {
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var method in scanner.Methods(static _ => true))
        {
            var writer = IlScanner.TypeName(IlScanner.Outermost(method.DeclaringType));
            var source = IlScanner.SourceMember(method);
            var bulkUpdate = false;
            var namedColumns = new List<string>();
            foreach (var (instruction, target) in IlScanner.Calls(method))
            {
                if (target.Name == "SetProperty" && target.DeclaringType.Name.StartsWith("SetPropertyCalls", StringComparison.Ordinal))
                {
                    bulkUpdate = true;
                    continue;
                }

                var owner = IlScanner.TypeName(target.DeclaringType);
                if (!entityTypeNames.Contains(owner) || owner == writer || target.Name.Length <= 4)
                {
                    continue;
                }

                var column = target.Name[4..];
                if (!IsLifecycleColumn(column))
                {
                    continue;
                }

                if (instruction.OpCode.Code is Code.Call or Code.Callvirt && target.Name.StartsWith("set_", StringComparison.Ordinal))
                {
                    findings.Add($"{source} -> {ShortName(owner)}.{column}");
                }
                else if (instruction.OpCode.Code == Code.Ldtoken && target.Name.StartsWith("get_", StringComparison.Ordinal))
                {
                    namedColumns.Add($"{ShortName(owner)}.{column}");
                }
            }

            if (bulkUpdate)
            {
                foreach (var column in namedColumns)
                {
                    findings.Add($"{source} -> {column} (bulk update)");
                }
            }
        }

        return findings.ToList();
    }

    /// <summary>Rule 3: adapter members (endpoints, dashboard middleware, renderers) that touch a database context.</summary>
    public static IReadOnlyList<string> DatabaseAccess(IlScanner scanner, Func<TypeDefinition, bool> isAdapter)
        => scanner.Methods(isAdapter)
            .Where(method => IlScanner.ReferencedTypes(method).Any(DatabaseTypes.Contains))
            .Select(IlScanner.SourceMember)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Rule 4: members outside processes that call <c>SaveChanges</c> or <c>SaveChangesAsync</c>. A
    /// context's own <c>SaveChanges</c> overrides calling the base are the save, not a call site.
    /// </summary>
    public static IReadOnlyList<string> SaveCalls(IlScanner scanner, Func<TypeDefinition, bool> isProcess)
        => scanner.Methods(type => !isProcess(type))
            .Where(method => !IsSaveOverride(method))
            .Where(method => IlScanner.Calls(method).Any(call =>
                call.Instruction.OpCode.Code is Code.Call or Code.Callvirt
                && call.Target.Name is "SaveChanges" or "SaveChangesAsync"
                && DatabaseTypes.Contains(call.Target.DeclaringType)))
            .Select(IlScanner.SourceMember)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Rule 5: every proof is sealed, has no public or protected constructor, lists its producers,
    /// and is constructed (or copied with <c>with</c>) only by them; a proof never constructs itself
    /// outside its copy method. Returns the violations; there is no allowlist.
    /// </summary>
    public static IReadOnlyList<string> ProofViolations(
        IlScanner scanner,
        IReadOnlyDictionary<string, IReadOnlySet<string>> producers,
        Func<TypeDefinition, bool> scope,
        string proofInterface = "SqlOS.Domain.ISqlOSProof")
    {
        var proofFamily = new TypeFamily(proofInterface);
        var proofs = scanner.Module.GetTypes()
            .Where(type => scope(IlScanner.Outermost(type)) && type is { IsInterface: false } && proofFamily.Contains(type))
            .ToDictionary(type => IlScanner.TypeName(type), StringComparer.Ordinal);
        var findings = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var (name, proof) in proofs)
        {
            if (!proof.IsSealed)
            {
                findings.Add($"{name} is not sealed");
            }

            if (proof.Methods.Any(method => method.IsConstructor && !method.IsStatic && (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly)))
            {
                findings.Add($"{name} has a public or protected constructor");
            }

            if (!producers.ContainsKey(name))
            {
                findings.Add($"{name} lists no producers");
            }
        }

        foreach (var listed in producers.Keys.Where(listed => !proofs.ContainsKey(listed)))
        {
            findings.Add($"{listed} is listed as a proof but is not an {proofInterface}");
        }

        foreach (var type in scanner.Module.GetTypes().Where(type => scope(IlScanner.Outermost(type)) && DesignProofNames.Contains(type.Name)))
        {
            if (!proofs.ContainsKey(IlScanner.TypeName(type)))
            {
                findings.Add($"{IlScanner.TypeName(type)} is a proof named by the design record but is not an {proofInterface}");
            }
        }

        foreach (var method in scanner.Methods(static _ => true))
        {
            var site = IlScanner.TypeName(IlScanner.Outermost(method.DeclaringType));
            foreach (var (instruction, target) in IlScanner.Calls(method))
            {
                var constructed = IlScanner.TypeName(target.DeclaringType);
                if (!proofs.ContainsKey(constructed))
                {
                    continue;
                }

                var creates = instruction.OpCode.Code == Code.Newobj && target.Name == ".ctor";
                var copies = target.Name == "<Clone>$";
                if (!creates && !copies)
                {
                    continue;
                }

                if (site == constructed)
                {
                    if (creates && method.Name != "<Clone>$")
                    {
                        findings.Add($"{IlScanner.SourceMember(method)} constructs {constructed} (proofs are constructed by their producers)");
                    }

                    continue;
                }

                if (!producers.TryGetValue(constructed, out var allowed) || !allowed.Contains(site))
                {
                    findings.Add($"{IlScanner.SourceMember(method)} {(creates ? "constructs" : "copies")} {constructed}");
                }
            }
        }

        return findings.ToList();
    }

    /// <summary>
    /// Rule 8: an aggregate's members are created and changed only through its root (§3.1). A call
    /// to a member's constructor, its static or instance methods, or its setters from any type
    /// other than the member and its root is a violation; reading the member (a property getter)
    /// is not. <paramref name="rootOfMember"/> maps each member type to its root. Returns the
    /// violations; there is no allowlist.
    /// </summary>
    public static IReadOnlyList<string> MemberChangesOutsideTheirRoot(
        IlScanner scanner,
        IReadOnlyDictionary<string, string> rootOfMember)
    {
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var method in scanner.Methods(static _ => true))
        {
            var site = IlScanner.TypeName(IlScanner.Outermost(method.DeclaringType));
            foreach (var (_, target) in IlScanner.Calls(method))
            {
                var member = IlScanner.TypeName(target.DeclaringType);
                if (!rootOfMember.TryGetValue(member, out var root)
                    || site == member
                    || site == root
                    || target.Name.StartsWith("get_", StringComparison.Ordinal))
                {
                    continue;
                }

                findings.Add($"{IlScanner.SourceMember(method)} -> {ShortName(member)}::{target.Name}");
            }
        }

        return findings.ToList();
    }

    /// <summary>
    /// Rule 6: clock reads in domain and process code. Domain code (building blocks, policies and
    /// entities) reads no clock at all; processes read <see cref="TimeProvider"/> only.
    /// </summary>
    public static IReadOnlyList<string> DomainClockReads(
        IlScanner scanner,
        Func<TypeDefinition, bool> isDomain,
        Func<TypeDefinition, bool> isProcess)
    {
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var method in scanner.Methods(type => isDomain(type) || isProcess(type)))
        {
            var domain = isDomain(IlScanner.Outermost(method.DeclaringType));
            foreach (var (_, target) in IlScanner.Calls(method))
            {
                var key = $"{IlScanner.TypeName(target.DeclaringType)}::{target.Name}";
                if (ClockReads.TryGetValue(key, out var read) || (domain && TimeProviderReads.TryGetValue(key, out read)))
                {
                    findings.Add($"{IlScanner.SourceMember(method)} -> {read}");
                }
            }
        }

        return findings.ToList();
    }

    /// <summary>Rule 7: references from domain and process code to <c>Microsoft.AspNetCore.Http</c>.</summary>
    public static IReadOnlyList<string> DomainHttpDependencies(IlScanner scanner, Func<TypeDefinition, bool> isDomainOrProcess)
    {
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var method in scanner.Methods(isDomainOrProcess))
        {
            foreach (var type in IlScanner.ReferencedTypes(method).Where(IsHttpType))
            {
                findings.Add($"{IlScanner.SourceMember(method)} -> {IlScanner.TypeName(Element(type))}");
            }
        }

        foreach (var type in scanner.Module.GetTypes().Where(type => isDomainOrProcess(IlScanner.Outermost(type))))
        {
            var declared = type.Interfaces.Select(implementation => implementation.InterfaceType)
                .Concat(type.Fields.Select(field => field.FieldType))
                .Append(type.BaseType)
                .OfType<TypeReference>();
            foreach (var reference in declared.Where(IsHttpType))
            {
                findings.Add($"{IlScanner.TypeName(IlScanner.Outermost(type))} -> {IlScanner.TypeName(Element(reference))}");
            }
        }

        return findings.ToList();
    }

    private static bool IsSaveOverride(MethodDefinition method)
    {
        var member = IlScanner.SourceMember(method);
        return (member.EndsWith("::SaveChanges", StringComparison.Ordinal) || member.EndsWith("::SaveChangesAsync", StringComparison.Ordinal))
            && DbContexts.Contains(IlScanner.Outermost(method.DeclaringType));
    }

    private static bool IsHttpType(TypeReference type)
    {
        var element = Element(type);
        while (element.IsNested)
        {
            element = element.DeclaringType;
        }

        return element.Namespace == "Microsoft.AspNetCore.Http"
            || element.Namespace.StartsWith("Microsoft.AspNetCore.Http.", StringComparison.Ordinal);
    }

    private static TypeReference Element(TypeReference type)
        => type is TypeSpecification specification ? Element(specification.ElementType) : type;

    private static string ShortName(string fullName) => fullName[(fullName.LastIndexOf('.') + 1)..];
}
