namespace SqlOS.Database;

/// <summary>
/// The store names the page-order statements need: the entry table of one page order, the
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

/// <summary>
/// The resources one save changed, as the names of their parameters (one resource-id list each). The
/// rows under a moved, deactivated or reactivated resource are found by walking its descendants. An
/// added or deleted resource stands alone: <c>ParentId</c> is a foreign key, so the only resources under
/// it are ones the same save adds or deletes, which are in this list themselves.
/// </summary>
/// <param name="Moved">Resources whose parent or activity changed, or <see langword="null"/> for none.</param>
/// <param name="AddedOrDeleted">Resources added or deleted, or <see langword="null"/> for none.</param>
internal sealed record SqlOSFgaChangedResources(string? Moved, string? AddedOrDeleted);
