namespace AdoMcpServer.Models;

/// <summary>Supported database engine types.</summary>
public enum DbType
{
    SqlServer,
    MySql,
    PostgreSql,
    Sqlite,
}

/// <summary>A named database connection configuration entry.</summary>
public class DatabaseConfig
{
    /// <summary>Logical name used to identify this connection (e.g. "default", "reporting").</summary>
    public string Name { get; set; } = "default";

    /// <summary>Database engine type.</summary>
    public DbType DbType { get; set; } = DbType.SqlServer;

    /// <summary>ADO.NET connection string.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Optional human-readable description of what this database is used for.</summary>
    public string? Description { get; set; }
}
