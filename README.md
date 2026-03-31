# AdoMcpServer

**AdoMcpServer** is a [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) server that helps large language models (LLMs) understand database structure, read table comments, and execute SQL queries.

AdoMcpServer 是一个基于 [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) 的数据库工具服务，帮助大型语言模型（LLM）理解数据库结构、读取表注释、执行 SQL 查询。

## MCP Tools

| Tool | Description |
|---|---|
| `list_connections` | List all configured database connections (pre-configured + dynamically added at runtime) |
| `add_connection` | **Dynamically add** a database connection at runtime without modifying config files |
| `remove_connection` | Remove a dynamically-added connection |
| `list_objects` | List all database objects (tables, views, procedures, functions, triggers, sequences, synonyms, …) with name-filter support |
| `get_table_schema` | Get the full schema of a table (column names, types, primary keys, **column comments**) |
| `get_table_indexes` | Get indexes defined on a table |
| `list_routines` | List stored procedures and functions (with type and comment), with name-filter support |
| `query_sql` | Execute a **read-only** SQL query and return results as CSV |
| `execute_sql` | Execute a **write** SQL statement (INSERT / UPDATE / DELETE / DDL) — requires `--allow-any-sql` |

## Supported Databases

| Database | Driver | Comment support |
|---|---|---|
| **SQL Server** | `Microsoft.Data.SqlClient` | `MS_Description` extended properties |
| **MySQL / MariaDB** | `MySqlConnector` | `TABLE_COMMENT` / `COLUMN_COMMENT` |
| **PostgreSQL** | `Npgsql` | `obj_description` / `col_description` |
| **SQLite** | `Microsoft.Data.Sqlite` | — (SQLite has no native comments) |
| **Oracle** | `Oracle.ManagedDataAccess.Core` | `ALL_TAB_COMMENTS` / `ALL_COL_COMMENTS` (includes PUBLIC synonyms) |

ORM support: [Dapper](https://github.com/DapperLib/Dapper) · [SqlSugarCore](https://www.donet5.com/)

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

---

## Quick Start

### 1. Configure database connections (optional)

Edit `src/AdoMcpServer/appsettings.json` and add pre-configured connections under the `Databases` array.  
You can also skip this step entirely and let the LLM add connections dynamically via the `add_connection` tool.

```json
"Databases": [
  {
    "Name": "mydb",
    "DbType": "SqlServer",
    "ConnectionString": "Server=localhost;Database=MyDb;User Id=sa;Password=***;TrustServerCertificate=true;",
    "Description": "Main business database"
  }
]
```

Supported `DbType` values: `SqlServer` | `MySql` | `PostgreSql` | `Sqlite` | `Oracle`

> **Security tip**: Use [.NET User Secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets) or environment variables to manage connection strings in production.

### 2. Run the server

#### Automatic mode detection (recommended)

When stdin is redirected (i.e. launched by an MCP client), **stdio** mode is used automatically.  
When run interactively in a terminal, **HTTP/SSE** mode is used automatically.

```bash
dotnet run --project src/AdoMcpServer
```

#### Specify mode manually

```bash
# stdio mode (all logs go to stderr; stdout carries only MCP JSON-RPC)
dotnet run --project src/AdoMcpServer -- --stdio

# HTTP/SSE mode (default: http://localhost:5100, MCP endpoint /mcp)
dotnet run --project src/AdoMcpServer -- --http

# Via environment variable
ADOMCP_MODE=http dotnet run --project src/AdoMcpServer
```

#### Enable execute_sql (write operations)

By default the `execute_sql` tool is **disabled** to prevent unauthorised writes.  
Add `--allow-any-sql` to enable it:

```bash
dotnet run --project src/AdoMcpServer -- --allow-any-sql
# Combine with transport mode
dotnet run --project src/AdoMcpServer -- --http --allow-any-sql
```

### 3. Run via NuGet / ndx (.NET 10)

After the package is published to NuGet.org, you can run it without cloning the repo:

```bash
# Install as a global .NET tool once, then run directly
dotnet tool install -g AdoMcpServer
adomcp

# Or use ndx (https://www.npmjs.com/package/ndx) — installs and runs on demand
npx -y ndx AdoMcpServer
npx -y ndx AdoMcpServer -- --allow-any-sql
```

---

## Dynamic connections at runtime (no config file needed)

LLMs can add new database connections during a session using `add_connection`:

```
User: Connect me to Oracle database oradb01
LLM → calls add_connection(
    connectionString = "Data Source=oradb01:1521/PROD;User Id=appuser;Password=***;",
    dbType = "Oracle",
    name = "prod-oracle",
    description = "Production Oracle DB"
)
→ returns: Connection 'prod-oracle' (Oracle) added successfully.
LLM → calls list_objects(connectionName = "prod-oracle")
```

Dynamically-added connections exist only for the lifetime of the process; restart the server or add the connection to `appsettings.json` for persistence.

---

## Using with Claude Desktop / Cursor

### stdio mode (recommended)

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

#### Via ndx (after NuGet publish)

```json
{
  "mcpServers": {
    "adomcp": {
      "command": "npx",
      "args": ["-y", "ndx", "AdoMcpServer"]
    }
  }
}
```

### HTTP mode

Start the server first:
```bash
dotnet run --project src/AdoMcpServer -- --http
```

Then configure the client:
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

## Environment variables

All environment variables are prefixed with `ADOMCP_` (override `appsettings.json`):

| Variable | Description |
|---|---|
| `ADOMCP_MODE` | Transport mode: `stdio` or `http` (auto-detected when not set) |
| `ADOMCP_URLS` | HTTP listen address, e.g. `http://0.0.0.0:5100` |

---

## MCP Registry (Smithery)

This server is listed on the [Smithery MCP registry](https://smithery.ai/).
The `smithery.yaml` file at the repository root describes how to launch the server.

To publish to Smithery:
1. Publish the NuGet package to [NuGet.org](https://www.nuget.org/): `dotnet pack -c Release && dotnet nuget push ...`
2. Submit the repository URL at [smithery.ai/new](https://smithery.ai/new) — Smithery will read `smithery.yaml` automatically.

## Build & Pack

```bash
# Build
dotnet build

# Pack as a NuGet tool (supports npx / ndx)
dotnet pack src/AdoMcpServer -c Release -o ./nupkg

# Publish to NuGet.org (set NUGET_API_KEY first)
dotnet nuget push ./nupkg/AdoMcpServer.*.nupkg --source https://api.nuget.org/v3/index.json --api-key $NUGET_API_KEY
```
