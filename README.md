# AdoMcpServer

AdoMcpServer 是一个基于 [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) 的数据库工具服务，帮助大型语言模型（LLM）理解数据库结构、读取表注释、执行 SQL 查询。

## 功能

| MCP 工具 | 说明 |
|---|---|
| `list_connections` | 列出所有已配置的数据库连接 |
| `list_tables` | 列出数据库所有表和视图（含表注释） |
| `get_table_schema` | 获取表的完整结构（列名、类型、主键、注释） |
| `get_table_indexes` | 获取表上的索引信息 |
| `list_routines` | 列出存储过程和函数（含定义与注释） |
| `execute_sql` | 执行任意 SQL 并返回结果集或影响行数 |

## 支持的数据库

- **SQL Server** — 通过 `Microsoft.Data.SqlClient`
- **MySQL / MariaDB** — 通过 `MySqlConnector`
- **PostgreSQL** — 通过 `Npgsql`
- **SQLite** — 通过 `Microsoft.Data.Sqlite`

ORM 支持：[Dapper](https://github.com/DapperLib/Dapper) · [SqlSugarCore](https://www.donet5.com/)

## 环境要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## 快速开始

### 1. 配置数据库连接

编辑 `src/AdoMcpServer/appsettings.json`，在 `Databases` 数组中添加连接：

```json
"Databases": [
  {
    "Name": "mydb",
    "DbType": "SqlServer",
    "ConnectionString": "Server=localhost;Database=MyDb;User Id=sa;Password=xxx;TrustServerCertificate=true;",
    "Description": "主业务库"
  }
]
```

支持的 `DbType` 值：`SqlServer` | `MySql` | `PostgreSql` | `Sqlite`

> **安全提示**：生产环境请使用 [.NET User Secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets) 或环境变量管理连接字符串，避免明文提交。

### 2. 运行（stdio 模式，默认）

```bash
dotnet run --project src/AdoMcpServer
# 或
dotnet run --project src/AdoMcpServer -- --stdio
```

stdio 模式下，所有日志输出到 **stderr**，stdout 仅传输 MCP JSON-RPC 消息。

### 3. 运行（HTTP/SSE 模式）

```bash
dotnet run --project src/AdoMcpServer -- --http
# 或
ADOMCP_MODE=http dotnet run --project src/AdoMcpServer
```

HTTP 模式默认监听 `http://localhost:5100`，MCP 端点为 `/mcp`。

可通过 `appsettings.json` 中的 `Urls` 字段或 `--urls` 参数修改监听地址。

### 4. 通过 npx 运行（.NET 10）

发布 NuGet 包后，可直接通过 npx 调用：

```bash
npx adomcpserver --http
```

## 在 Claude Desktop / Cursor 中使用

### stdio 模式配置示例

```json
{
  "mcpServers": {
    "adomcp": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/src/AdoMcpServer", "--", "--stdio"]
    }
  }
}
```

### HTTP 模式配置示例

先启动服务：
```bash
dotnet run --project src/AdoMcpServer -- --http
```

再在 MCP 客户端中配置：
```json
{
  "mcpServers": {
    "adomcp": {
      "url": "http://localhost:5100/mcp"
    }
  }
}
```

## 环境变量

所有环境变量以 `ADOMCP_` 为前缀（覆盖 appsettings.json）：

| 变量 | 说明 |
|---|---|
| `ADOMCP_MODE` | 传输模式：`stdio`（默认）或 `http` |
| `ADOMCP_URLS` | HTTP 监听地址，如 `http://0.0.0.0:5100` |

## 构建

```bash
dotnet build
```

## 打包为 NuGet 工具

```bash
dotnet pack src/AdoMcpServer -c Release -o ./nupkg
```
