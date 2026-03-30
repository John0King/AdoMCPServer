using System.ComponentModel;
using System.Text;
using System.Text.Json;
using AdoMcpServer.Models;
using AdoMcpServer.Services;
using ModelContextProtocol.Server;

namespace AdoMcpServer.Tools;

/// <summary>MCP tools that expose database metadata and query execution to AI models.</summary>
[McpServerToolType]
public class DatabaseTools(IDatabaseService db)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ─────────────────────────────────────────────────────────────────────────
    // list_connections
    // ─────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "list_connections")]
    [Description("列出所有已配置的数据库连接名称及类型（包含运行时动态添加的连接）。用于确认可用的连接名称（connectionName）。")]
    public string ListConnections()
    {
        var configs = db.GetConfigurations();
        if (configs.Count == 0)
            return "没有配置任何数据库连接。请使用 add_connection 工具添加连接，或在 appsettings.json 中添加 Databases 配置节。";

        var sb = new StringBuilder();
        foreach (var c in configs)
        {
            sb.AppendLine($"- **{c.Name}** ({c.DbType})");
            if (!string.IsNullOrWhiteSpace(c.Description))
                sb.AppendLine($"  描述: {c.Description}");
        }
        return sb.ToString().TrimEnd();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // add_connection
    // ─────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "add_connection")]
    [Description("""
        在运行时动态添加（或替换）一个数据库连接，无需修改配置文件。
        添加后可立即通过其他工具（list_tables、execute_sql 等）使用该连接名称。
        支持的 dbType 值：SqlServer | MySql | PostgreSql | Sqlite | Oracle
        """)]
    public async Task<string> AddConnectionAsync(
        [Description("连接字符串，例如 SQL Server: \"Server=host;Database=db;User Id=sa;Password=***;TrustServerCertificate=true;\"")]
        string connectionString,
        [Description("数据库引擎类型：SqlServer | MySql | PostgreSql | Sqlite | Oracle")]
        string dbType,
        [Description("连接的逻辑名称，用于其他工具的 connectionName 参数。默认自动生成。")]
        string? name = null,
        [Description("连接的描述（可选）。")]
        string? description = null,
        [Description("是否在保存前测试连接可用性，默认 true。")]
        bool testConnection = true,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<Models.DbType>(dbType, ignoreCase: true, out var parsedDbType))
        {
            var validValues = string.Join(", ", Enum.GetNames<Models.DbType>());
            return $"错误: 无法识别的数据库类型 '{dbType}'。支持的值: {validValues}";
        }

        var connectionName = string.IsNullOrWhiteSpace(name)
            ? $"{parsedDbType.ToString().ToLower()}-{Guid.NewGuid().ToString("N")[..6]}"
            : name;

        var config = new DatabaseConfig
        {
            Name             = connectionName,
            DbType           = parsedDbType,
            ConnectionString = connectionString,
            Description      = description,
        };

        var error = await db.AddConnectionAsync(config, testFirst: testConnection, ct: cancellationToken);
        if (error is not null)
            return $"错误: {error}";

        return $"连接 '{connectionName}' ({parsedDbType}) 已成功添加。现在可以使用此名称调用其他数据库工具。";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // remove_connection
    // ─────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "remove_connection")]
    [Description("移除一个已注册的数据库连接（动态添加的或预配置的均可）。")]
    public string RemoveConnection(
        [Description("要移除的连接名称。")]
        string connectionName)
    {
        return db.RemoveConnection(connectionName)
            ? $"连接 '{connectionName}' 已移除。"
            : $"未找到名为 '{connectionName}' 的连接。";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // list_tables
    // ─────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "list_tables")]
    [Description("""
        列出数据库中的所有表（以及可选的视图），包含每个表的注释/描述。
        这是理解数据库结构的第一步。
        """)]
    public async Task<string> ListTablesAsync(
        [Description("数据库连接名称，来自 list_connections 工具的结果。")]
        string connectionName,
        [Description("是否同时列出视图，默认 true。")]
        bool includeViews = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var tables = await db.ListTablesAsync(connectionName, includeViews, cancellationToken);
            if (tables.Count == 0)
                return "数据库中没有找到任何表或视图。";

            return JsonSerializer.Serialize(tables, JsonOpts);
        }
        catch (Exception ex)
        {
            return $"错误: {ex.Message}";
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // get_table_schema
    // ─────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "get_table_schema")]
    [Description("""
        获取指定表的详细结构，包含：
        - 每列的名称、数据类型、是否可空、是否主键、默认值、最大长度
        - 表注释和每列的注释/描述（对理解业务含义非常重要）
        """)]
    public async Task<string> GetTableSchemaAsync(
        [Description("数据库连接名称。")]
        string connectionName,
        [Description("表名（不含 schema 前缀）。")]
        string tableName,
        [Description("Schema 名称，SQL Server 默认 dbo，PostgreSQL 默认 public，Oracle 默认当前用户，MySQL/SQLite 可留空。")]
        string? schema = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await db.GetTableSchemaAsync(connectionName, tableName, schema, cancellationToken);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (Exception ex)
        {
            return $"错误: {ex.Message}";
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // get_table_indexes
    // ─────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "get_table_indexes")]
    [Description("获取指定表上定义的所有索引，包含索引名称、是否唯一、是否主键及涉及的列。")]
    public async Task<string> GetTableIndexesAsync(
        [Description("数据库连接名称。")]
        string connectionName,
        [Description("表名。")]
        string tableName,
        [Description("Schema 名称，可留空使用默认值。")]
        string? schema = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await db.GetTableIndexesAsync(connectionName, tableName, schema, cancellationToken);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (Exception ex)
        {
            return $"错误: {ex.Message}";
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // list_routines
    // ─────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "list_routines")]
    [Description("列出数据库中的存储过程和函数，包含其定义和注释。SQLite 不支持存储过程，会返回空列表。")]
    public async Task<string> ListRoutinesAsync(
        [Description("数据库连接名称。")]
        string connectionName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await db.ListRoutinesAsync(connectionName, cancellationToken);
            if (result.Count == 0)
                return "没有找到存储过程或函数。";

            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (Exception ex)
        {
            return $"错误: {ex.Message}";
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // execute_sql
    // ─────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "execute_sql")]
    [Description("""
        在指定数据库上执行任意 SQL 语句并返回结果。
        - SELECT：返回列名和数据行（最多 maxRows 行）。
        - INSERT / UPDATE / DELETE：返回受影响行数。
        注意：请谨慎执行 DDL 或 DELETE/UPDATE 语句，此操作不可回滚。
        """)]
    public async Task<string> ExecuteSqlAsync(
        [Description("数据库连接名称。")]
        string connectionName,
        [Description("要执行的 SQL 语句。")]
        string sql,
        [Description("SELECT 结果最大返回行数，默认 200，最大 1000。")]
        int maxRows = 200,
        CancellationToken cancellationToken = default)
    {
        maxRows = Math.Clamp(maxRows, 1, 1000);
        try
        {
            var result = await db.ExecuteSqlAsync(connectionName, sql, maxRows, cancellationToken);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (Exception ex)
        {
            return $"错误: {ex.Message}";
        }
    }
}

