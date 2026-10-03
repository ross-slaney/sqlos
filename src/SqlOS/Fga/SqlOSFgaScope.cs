using System.Buffers.Binary;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace SqlOS.Fga;

/// <summary>
/// The scope value of a row (see <see cref="SqlOSFgaLineage.ScopeColumn"/>): how it is encoded, and how a
/// query reads a piece of it. The value is the same byte string on both engines; the methods below stand in
/// a query for the <c>SUBSTRING</c> expressions SqlOS indexes per level (an expression index on PostgreSQL,
/// a computed column on SQL Server), and for the comparison on the depth byte that selects a level's
/// filtered index. They are translated, never run.
/// </summary>
internal static class SqlOSFgaScope
{
    internal static readonly MethodInfo AncestorAtMethod = typeof(SqlOSFgaScope).GetMethod(nameof(AncestorAt), BindingFlags.Static | BindingFlags.Public)!;
    internal static readonly MethodInfo TypeOfMethod = typeof(SqlOSFgaScope).GetMethod(nameof(TypeOf), BindingFlags.Static | BindingFlags.Public)!;
    internal static readonly MethodInfo ReachesMethod = typeof(SqlOSFgaScope).GetMethod(nameof(Reaches), BindingFlags.Static | BindingFlags.Public)!;

    /// <summary>The eight bytes holding the row's ancestor at a level; <c>SUBSTRING(scope, offset, 8)</c>.</summary>
    public static byte[]? AncestorAt(byte[]? scope, int level) => throw Translated();

    /// <summary>The four bytes holding the row's resource type; <c>SUBSTRING(scope, offset, 4)</c> (the offset is <see cref="SqlOSFgaLineage.ScopeTypeOffset"/>; an argument so the translation has an integer to type its constants by).</summary>
    public static byte[]? TypeOf(byte[]? scope, int offset) => throw Translated();

    /// <summary>Whether the row sits at or below a level; <c>scope &gt;= 0x0l</c>, the predicate of the level's filtered index.</summary>
    public static bool Reaches(byte[]? scope, int level) => throw Translated();

    private static InvalidOperationException Translated()
        => new("This method is translated to SQL by SqlOS and cannot be called directly.");

    /// <summary>Registers the translations on a relational model.</summary>
    public static void Register(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDbFunction(AncestorAtMethod).HasTranslation(args =>
        {
            var level = (int)((SqlConstantExpression)args[1]).Value!;
            return Substring(args[0], args[1], SqlOSFgaLineage.ScopeAncestorOffset(level), 8);
        });
        modelBuilder.HasDbFunction(TypeOfMethod).HasTranslation(args =>
            Substring(args[0], args[1], (int)((SqlConstantExpression)args[1]).Value!, 4));
        modelBuilder.HasDbFunction(ReachesMethod).HasTranslation(args =>
        {
            var level = (int)((SqlConstantExpression)args[1]).Value!;
            return new SqlBinaryExpression(
                ExpressionType.GreaterThanOrEqual,
                args[0],
                new SqlConstantExpression(new[] { checked((byte)level) }, args[0].TypeMapping),
                typeof(bool),
                null);
        });
    }

    /// <summary>
    /// <c>SUBSTRING(scope, offset, length)</c>. The constants take the integer argument's type mapping: EF Core
    /// applies default mappings to a translation's arguments, and requires one on every constant.
    /// </summary>
    private static SqlFunctionExpression Substring(SqlExpression scope, SqlExpression intTemplate, int offset, int length)
    {
        var intMapping = intTemplate.TypeMapping;
        return new SqlFunctionExpression(
            "SUBSTRING",
            [scope, new SqlConstantExpression(offset, intMapping), new SqlConstantExpression(length, intMapping)],
            nullable: true,
            argumentsPropagateNullability: [true, false, false],
            typeof(byte[]),
            scope.TypeMapping);
    }

    /// <summary>The eight bytes of a resource's compact key as the value stores them: big-endian, as SQL Server's <c>CAST(bigint AS BINARY(8))</c> and PostgreSQL's <c>int8send</c> write them.</summary>
    public static byte[] Bytes(long seq)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, seq);
        return bytes;
    }

    /// <summary>The four bytes of a resource type's compact key as the value stores them.</summary>
    public static byte[] Bytes(int typeSeq)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, typeSeq);
        return bytes;
    }

    /// <summary>
    /// The value: depth, type, then the ancestor at each level, zero where access does not flow from that
    /// level. Exactly what the database triggers compute, for loaders that write rows themselves.
    /// </summary>
    public static byte[] Encode(int depth, int typeSeq, IReadOnlyList<long?> ancestorsByLevel)
    {
        var bytes = new byte[SqlOSFgaLineage.ScopeBinaryLength(ancestorsByLevel.Count)];
        bytes[0] = checked((byte)depth);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(1, 4), typeSeq);
        for (var level = 0; level < ancestorsByLevel.Count; level++)
        {
            BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(5 + 8 * level, 8), ancestorsByLevel[level] ?? 0);
        }

        return bytes;
    }

    /// <summary>Reads a value back: depth, type, and the ancestor at each level (NULL where none).</summary>
    public static (int Depth, int TypeSeq, long?[] Ancestors) Decode(byte[] value, int levels)
    {
        var ancestors = new long?[levels];
        for (var level = 0; level < levels; level++)
        {
            var offset = 5 + 8 * level;
            if (offset + 8 > value.Length)
            {
                break;
            }

            var seq = BinaryPrimitives.ReadInt64BigEndian(value.AsSpan(offset, 8));
            ancestors[level] = seq == 0 ? null : seq;
        }

        return (value[0], BinaryPrimitives.ReadInt32BigEndian(value.AsSpan(1, 4)), ancestors);
    }
}
