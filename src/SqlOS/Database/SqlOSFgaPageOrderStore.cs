namespace SqlOS.Database;

/// <summary>
/// The store names the page-order statements need: the entry table of one declared order, the
/// application table it copies, and the FGA objects that decide which nodes a row sits under.
/// </summary>
/// <param name="FgaSchema">Schema of the FGA tables and functions.</param>
/// <param name="ResourcesTable">The FGA resources table.</param>
/// <param name="MaxDepth">The configured maximum resource hierarchy depth.</param>
/// <param name="EntrySchema">Schema of the entry table.</param>
/// <param name="EntryTable">The entry table.</param>
/// <param name="EntryNodeIdColumn">The entry table's node column.</param>
/// <param name="EntrySortColumns">The entry table's copies of the order columns, in order, without the key.</param>
/// <param name="EntryRowKeyColumn">The entry table's copy of the application row's primary key.</param>
/// <param name="AppSchema">Schema of the application table, or <see langword="null"/> for the default schema.</param>
/// <param name="AppTable">The application table.</param>
/// <param name="AppSortColumns">The application's order columns, in order, without the key.</param>
/// <param name="AppKeyColumn">The application table's primary key column.</param>
/// <param name="AppResourceIdColumn">The application table's <c>ResourceId</c> column.</param>
/// <param name="KeyStoreType">The store type of the primary key column.</param>
internal sealed record SqlOSFgaPageOrderStore(
    string FgaSchema,
    string ResourcesTable,
    int MaxDepth,
    string? EntrySchema,
    string EntryTable,
    string EntryNodeIdColumn,
    IReadOnlyList<string> EntrySortColumns,
    string EntryRowKeyColumn,
    string? AppSchema,
    string AppTable,
    IReadOnlyList<string> AppSortColumns,
    string AppKeyColumn,
    string AppResourceIdColumn,
    string KeyStoreType);
