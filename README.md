# AdoMcpServer

AdoMcpServer 是一个基于 [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) 的数据库工具服务，帮助大型语言模型（LLM）理解数据库结构、读取表注释、执行 SQL 查询。

## 功能

| MCP 工具 | 说明 |
|---|---|
| `list_connections` | 列出所有已注册的数据库连接（预配置 + 运行时动态添加） |
| `add_connection` | **运行时动态添加**数据库连接，无需修改配置文件 |
| `remove_connection` | 移除一个已注册的连接 |
| `list_tables` | 列出数据库所有表和视图（含表注释） |
| `get_table_schema` | 获取表的完整结构（列名、类型、主键、**列注释**） |
| `get_table_indexes` | 获取表上的索引信息 |
| `list_routines` | 列出存储过程和函数（含定义与注释） |
| `execute_sql` | 执行任意 SQL 并返回结果集或影响行数 |

## 支持的数据库

| 数据库 | 驱动 | 注释支持 |
|---|---|---|
| **SQL Server** | `Microsoft.Data.SqlClient` | `MS_Description` 扩展属性 |
| **MySQL / MariaDB** | `MySqlConnector` | `TABLE_COMMENT` / `COLUMN_COMMENT` |
| **PostgreSQL** | `Npgsql` | `obj_description` / `col_description` |
| **SQLite** | `Microsoft.Data.Sqlite` | — (SQLite 无原生注释) |
| **Oracle** | `Oracle.ManagedDataAccess.Core` | `ALL_TAB_COMMENTS` / `ALL_COL_COMMENTS` |

ORM 支持：[Dapper](https://github.com/DapperLib/Dapper) · [SqlSugarCore](https://www.donet5.com/)

## 环境要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

---

## 快速开始

### 1. 配置数据库连接（可选）

编辑 `src/AdoMcpServer/appsettings.json`，在 `Databases` 数组中添加预配置连接。  
也可以完全跳过此步骤，让 LLM 在运行时通过 `add_connection` 工具动态添加连接。

```json
"Databases": [
  {
    "Name": "mydb",
    "DbType": "SqlServer",
    "ConnectionString": "Server=localhost;Database=MyDb;User Id=sa;Password=***;TrustServerCertificate=true;",
    "Description": "主业务库"
  }
]
```

支持的 `DbType` 值：`SqlServer` | `MySql` | `PostgreSql` | `Sqlite` | `Oracle`

> **安全提示**：生产环境请使用 [.NET User Secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets) 或环境变量管理连接字符串。

### 2. 运行服务

#### 自动模式检测（推荐）

当进程的标准输入被重定向（即由 MCP 客户端拉起）时，自动使用 **stdio** 模式；  
在终端交互式运行时，自动使用 **HTTP/SSE** 模式。

```bash
dotnet run --project src/AdoMcpServer
```

#### 手动指定模式

```bash
# stdio 模式（所有日志输出到 stderr，stdout 仅传输 MCP JSON-RPC）
dotnet run --project src/AdoMcpServer -- --stdio

# HTTP/SSE 模式（默认监听 http://localhost:5100，MCP 端点 /mcp）
dotnet run --project src/AdoMcpServer -- --http

# 通过环境变量指定
ADOMCP_MODE=http dotnet run --project src/AdoMcpServer
```

### 3. 通过 npx 运行（.NET 10）

发布 NuGet 包后，可直接通过 npx 调用：

```bash
npx adomcpserver
```

---

## 运行时动态连接（无需配置文件）

LLM 可以通过 `add_connection` 工具在会话中随时添加新的数据库连接：

```
用户: 帮我连接到 Oracle 数据库 oradb01
LLM → 调用 add_connection(
    connectionString = "Data Source=oradb01:1521/PROD;User Id=appuser;Password=***;",
    dbType = "Oracle",
    name = "prod-oracle",
    description = "生产 Oracle 库"
)
→ 返回: 连接 'prod-oracle' (Oracle) 已成功添加。
LLM → 调用 list_tables(connectionName = "prod-oracle")
```

动态添加的连接仅在当前进程生命周期内有效，重启后需重新添加（或在 appsettings.json 中持久化）。

---

## 在 Claude Desktop / Cursor 中使用

### stdio 模式（推荐）

```json
{
  "mcpServers": {
    "adomcp": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/src/AdoMcpServer"]
    }
  }
}
```

### HTTP 模式

先启动服务：
```bash
dotnet run --project src/AdoMcpServer -- --http
```

再配置客户端：
```json
{
  "mcpServers": {
    "adomcp": {
      "url": "http://localhost:5100/mcp"
    }
  }
}
```

---

## 环境变量

所有环境变量以 `ADOMCP_` 为前缀（覆盖 appsettings.json）：

| 变量 | 说明 |
|---|---|
| `ADOMCP_MODE` | 传输模式：`stdio` 或 `http`（未设置时自动检测） |
| `ADOMCP_URLS` | HTTP 监听地址，如 `http://0.0.0.0:5100` |

## 构建 & 打包

```bash
# 构建
dotnet build

# 打包为 NuGet 工具（支持 npx 调用）
dotnet pack src/AdoMcpServer -c Release -o ./nupkg
```
