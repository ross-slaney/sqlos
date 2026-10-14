using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Paging;

namespace SqlOS.Fga.Extensions;

/// <summary>
/// Optimizes the orders an authorized list is paged by with <c>ToPageAsync</c>.
/// </summary>
public static class PageOrderModelExtensions
{
    /// <summary>
    /// Declares an order this entity is paged by, as an optimization: SqlOS keeps a copy of the order
    /// under every node of the resource tree in a table of its own
    /// (<c>SqlOSFgaPage_{Table}_{Columns}</c>), so a page by exactly this order costs about a page for
    /// any caller. Every order pages without a declaration, at a cost bounded by the rows the caller
    /// can see; declare the hot ones.
    /// </summary>
    /// <typeparam name="T">The entity type exposing the FGA resource identifier.</typeparam>
    /// <param name="entity">The entity type builder.</param>
    /// <param name="columns">
    /// One property (<c>p =&gt; p.Price</c>) or an anonymous type of properties
    /// (<c>p =&gt; new { p.Status, p.CreatedAt }</c>). The primary key is appended when it is not
    /// already the last column. One declaration serves ascending and descending pages.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// The declaration adds a table to the EF model, so the application's next migration (or a
    /// model-driven schema tool) creates it. Declaring the same order twice is a no-op, and so is
    /// declaring the key alone: that is the node index SqlOS keeps for every protected entity. A page
    /// uses the declaration when its order has exactly these columns, in either direction.
    /// </para>
    /// <para>
    /// Order columns must be non-nullable mapped scalar properties; strings need a maximum length.
    /// The entity needs a single-column primary key, a mapped <c>ResourceId</c>, and a table of its own
    /// (no owned types, views, or TPT/TPC). These rules are checked when the model is built.
    /// Page orders are kept current by <c>SqlOSDbContext&lt;TContext&gt;</c>.
    /// </para>
    /// </remarks>
    public static EntityTypeBuilder<T> HasPageOrder<T>(
        this EntityTypeBuilder<T> entity,
        Expression<Func<T, object>> columns)
        where T : class, IHasResourceId
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(columns);

        SqlOSFgaPageOrders.Declare(entity.Metadata, ReadColumns(columns));
        return entity;
    }

    private static IReadOnlyList<string> ReadColumns<T>(Expression<Func<T, object>> columns)
    {
        var parameter = columns.Parameters[0];
        var body = StripConvert(columns.Body);
        var members = body is NewExpression { Members: not null } anonymous
            ? anonymous.Arguments.Select(StripConvert).ToArray()
            : [body];

        var names = new List<string>(members.Length);
        foreach (var member in members)
        {
            if (member is not MemberExpression { Expression: { } target } access || target != parameter)
            {
                throw new InvalidOperationException(
                    $"HasPageOrder on {typeof(T).Name}: '{member}' is not a mapped scalar property of {typeof(T).Name}. Pass one property (e => e.Price) or an anonymous type of properties (e => new {{ e.Status, e.CreatedAt }}).");
            }

            names.Add(access.Member.Name);
        }

        return names;
    }

    private static Expression StripConvert(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
        {
            expression = convert.Operand;
        }

        return expression;
    }
}
