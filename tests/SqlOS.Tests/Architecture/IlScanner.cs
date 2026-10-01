using Mono.Cecil;
using Mono.Cecil.Cil;

namespace SqlOS.Tests.Architecture;

/// <summary>
/// Reads an assembly's IL with Mono.Cecil for the architecture rules. Every finding is reported
/// against the source member that contains it: compiler-generated lambdas, local functions, async
/// state machines and closures are folded into the method they were written in, so an allowlist
/// entry reads <c>Namespace.Type::Method</c>.
/// </summary>
internal sealed class IlScanner
{
    public IlScanner(string assemblyPath)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(AppContext.BaseDirectory);
        resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
        resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(Microsoft.AspNetCore.Http.HttpContext).Assembly.Location)!);
        Module = ModuleDefinition.ReadModule(assemblyPath, new ReaderParameters { AssemblyResolver = resolver });
    }

    public static IlScanner SqlOS { get; } = new(typeof(SqlOS.Configuration.SqlOSOptions).Assembly.Location);

    public static IlScanner Tests { get; } = new(typeof(IlScanner).Assembly.Location);

    public ModuleDefinition Module { get; }

    /// <summary>Every method with a body whose outermost declaring type is in scope.</summary>
    public IEnumerable<MethodDefinition> Methods(Func<TypeDefinition, bool> scope)
        => Module.GetTypes()
            .Where(type => scope(Outermost(type)))
            .SelectMany(type => type.Methods)
            .Where(method => method.HasBody);

    /// <summary>The type a member was written in: nested compiler-generated types fold into it.</summary>
    public static TypeDefinition Outermost(TypeDefinition type)
    {
        while (type.IsNested)
        {
            type = type.DeclaringType;
        }

        return type;
    }

    /// <summary><c>Namespace.Type::Method</c> of the source member that contains <paramref name="method"/>.</summary>
    public static string SourceMember(MethodDefinition method)
    {
        var name = GeneratedOwner(method.Name) switch
        {
            { Length: > 0 } owner => owner,
            // <>n__0 is the compiler's thunk for a base call from an async method or lambda; it
            // belongs to the override that calls the base member of the same name.
            "" when method.Name.StartsWith("<>n__", StringComparison.Ordinal) && method.HasBody
                && method.Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>().FirstOrDefault() is { } baseMember
                => baseMember.Name,
            _ => method.Name
        };
        var type = method.DeclaringType;
        while (type.IsNested && type.Name.StartsWith('<'))
        {
            if (GeneratedOwner(type.Name) is { Length: > 0 } owner)
            {
                name = owner;
            }

            type = type.DeclaringType;
        }

        return $"{TypeName(type)}::{name}";
    }

    /// <summary>The reflection-style full name (<c>Outer+Inner</c>) of a Cecil type.</summary>
    public static string TypeName(TypeReference type) => type.FullName.Replace('/', '+');

    /// <summary>
    /// Every type a method's signature, locals and instructions mention, including the declaring
    /// types, signatures and generic arguments of the members it uses.
    /// </summary>
    public static IEnumerable<TypeReference> ReferencedTypes(MethodDefinition method)
    {
        foreach (var type in SignatureTypes(method))
        {
            yield return type;
        }

        foreach (var variable in method.Body.Variables)
        {
            foreach (var type in Expand(variable.VariableType))
            {
                yield return type;
            }
        }

        foreach (var instruction in method.Body.Instructions)
        {
            var types = instruction.Operand switch
            {
                MethodReference member => SignatureTypes(member).Concat(Expand(member.DeclaringType)),
                FieldReference field => Expand(field.FieldType).Concat(Expand(field.DeclaringType)),
                TypeReference type => Expand(type),
                _ => []
            };
            foreach (var type in types)
            {
                yield return type;
            }
        }
    }

    /// <summary>The methods a method calls or creates, with the instruction that does it.</summary>
    public static IEnumerable<(Instruction Instruction, MethodReference Target)> Calls(MethodDefinition method)
        => method.Body.Instructions
            .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj or Code.Ldtoken or Code.Ldftn or Code.Ldvirtftn)
            .Select(instruction => (instruction, instruction.Operand as MethodReference))
            .Where(pair => pair.Item2 is not null)
            .Select(pair => (pair.instruction, pair.Item2!));

    private static IEnumerable<TypeReference> SignatureTypes(MethodReference method)
    {
        foreach (var type in Expand(method.ReturnType))
        {
            yield return type;
        }

        foreach (var parameter in method.Parameters)
        {
            foreach (var type in Expand(parameter.ParameterType))
            {
                yield return type;
            }
        }

        if (method is GenericInstanceMethod generic)
        {
            foreach (var argument in generic.GenericArguments)
            {
                foreach (var type in Expand(argument))
                {
                    yield return type;
                }
            }
        }
    }

    private static IEnumerable<TypeReference> Expand(TypeReference type)
    {
        yield return type;
        switch (type)
        {
            case GenericInstanceType generic:
                foreach (var nested in Expand(generic.ElementType).Concat(generic.GenericArguments.SelectMany(Expand)))
                {
                    yield return nested;
                }

                break;
            case TypeSpecification specification:
                foreach (var nested in Expand(specification.ElementType))
                {
                    yield return nested;
                }

                break;
        }
    }

    /// <summary>
    /// The member a compiler-generated name was written in: <c>&lt;Handle&gt;d__5</c>,
    /// <c>&lt;Handle&gt;b__5_0</c> and <c>&lt;&lt;Handle&gt;g__Local|5_0&gt;d</c> all name <c>Handle</c>;
    /// a closure such as <c>&lt;&gt;c__DisplayClass5_0</c> names none (empty).
    /// </summary>
    private static string? GeneratedOwner(string name)
    {
        if (!name.StartsWith('<'))
        {
            return null;
        }

        var depth = 0;
        for (var index = 0; index < name.Length; index++)
        {
            depth += name[index] switch { '<' => 1, '>' => -1, _ => 0 };
            if (depth == 0)
            {
                var inner = name[1..index];
                return inner.StartsWith('<') ? GeneratedOwner(inner) ?? inner : inner;
            }
        }

        return null;
    }
}

/// <summary>
/// A family of types: the named types and every type that derives from or implements one of them,
/// for example <c>DbContext</c> and every host context.
/// </summary>
internal sealed class TypeFamily
{
    private readonly HashSet<string> _names;
    private readonly Dictionary<string, bool> _members = new(StringComparer.Ordinal);

    public TypeFamily(params string[] fullNames) => _names = new HashSet<string>(fullNames, StringComparer.Ordinal);

    public bool Contains(TypeReference? type)
    {
        if (type is null)
        {
            return false;
        }

        var element = type is TypeSpecification specification ? specification.ElementType : type;
        var key = element.FullName;
        if (!_members.TryGetValue(key, out var member))
        {
            member = Walk(element);
            _members[key] = member;
        }

        return member;
    }

    private bool Walk(TypeReference type)
    {
        if (_names.Contains(IlScanner.TypeName(type)))
        {
            return true;
        }

        TypeDefinition? definition;
        try
        {
            definition = type.Resolve();
        }
        catch (AssemblyResolutionException)
        {
            return false;
        }

        return definition is not null
            && (definition.Interfaces.Any(implementation => Contains(implementation.InterfaceType))
                || Contains(definition.BaseType));
    }
}
