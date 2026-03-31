namespace AdoMcpServer.Models;

/// <summary>
/// Metadata about any database object (table, view, procedure, function, trigger,
/// sequence, synonym, etc.), including its comment/description where available.
/// </summary>
public class TableInfo
{
    public string Schema { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>Object type, e.g. TABLE, VIEW, PROCEDURE, FUNCTION, TRIGGER, SEQUENCE, SYNONYM.</summary>
    public string Type { get; set; } = string.Empty;
    /// <summary>Comment / description attached to the object in the database (may be null for non-table types).</summary>
    public string? Comment { get; set; }
}

/// <summary>Metadata about a single column, including its comment/description.</summary>
public class ColumnInfo
{
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public bool IsNullable { get; set; }
    public bool IsPrimaryKey { get; set; }
    public string? DefaultValue { get; set; }
    public int? MaxLength { get; set; }
    /// <summary>Comment / description attached to the column in the database.</summary>
    public string? Comment { get; set; }
}

/// <summary>Full schema of a table: its columns and any relevant table comment.</summary>
public class TableSchema
{
    public string Schema { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public string? TableComment { get; set; }
    public List<ColumnInfo> Columns { get; set; } = [];
}

/// <summary>Metadata about a stored procedure or function.</summary>
public class RoutineInfo
{
    public string Schema { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;   // PROCEDURE | FUNCTION
    public string? Definition { get; set; }
    public string? Comment { get; set; }
}

/// <summary>Metadata about an index.</summary>
public class IndexInfo
{
    public string IndexName { get; set; } = string.Empty;
    public bool IsUnique { get; set; }
    public bool IsPrimaryKey { get; set; }
    public List<string> Columns { get; set; } = [];
}

/// <summary>A single row from a query result, expressed as a dictionary of column → value.</summary>
public class QueryRow : Dictionary<string, object?> { }

/// <summary>Result set returned by <c>execute_sql</c>.</summary>
public class QueryResult
{
    public List<string> Columns { get; set; } = [];
    public List<QueryRow> Rows { get; set; } = [];
    public int RowsAffected { get; set; }
    public string? ErrorMessage { get; set; }
}
