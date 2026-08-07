# AdoMcp

[![NuGet](https://img.shields.io/nuget/v/AdoMcp.svg)](https://www.nuget.org/packages/AdoMcp)

**AdoMcp** is a [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) server that helps large language models (LLMs) understand database structure, read table comments, and execute SQL queries.

AdoMcp 是一个基于 [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) 的数据库工具服务，帮助大型语言模型（LLM）理解数据库结构、读取表注释、执行 SQL 查询。

<!-- mcp-name: io.github.John0King/adomcp -->

## MCP Tools

| Tool | Description |
|---|---|
| `list_connections` | List configured database connections |
| `add_connection` | Add (or replace) a database connection at runtime |
| `remove_connection` | Remove a dynamically-added connection |
| `list_objects` | List database objects (table/view/procedure/function/trigger/sequence/synonym, etc.) |
| `get_table_schema` | Get table schema details (columns/types/nullability/PK/default/comments) |
| `get_table_indexes` | Get table indexes |
| `query_sql` | Execute read-only SQL and return CSV |
| `execute_sql` | Execute write SQL (requires `--allow-any-sql`) |

## Recommended Tool Workflow (for LLM agents)

To reduce mistakes (wrong database/schema/object), use tools in this order:

1. `list_connections` to discover available connections.
2. If none are available, call `add_connection`.
3. Before inspecting a table/view, call `list_objects` to locate `schema + objectType + objectName`.
4. Use `get_table_schema` for column details (type, nullability, PK, default, comments).
5. Use `get_table_indexes` when index/key design matters.
6. Use `query_sql` only for read-only verification.
7. Use `execute_sql` only when explicitly authorized and server is started with `--allow-any-sql`.

Oracle note: objects without owner prefix may be synonyms. Always confirm the real schema via `list_objects` first.

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

AdoMcp loads configuration from multiple sources (later sources override earlier ones):

1. `appsettings.json` (in the app directory)
2. `appsettings.{Environment}.json`
3. **`~/.adomcp.json`** — user-level config (`%USERPROFILE%\.adomcp.json` on Windows), persists connections and `AllowAnySql` without touching the app directory
4. Environment variables prefixed `ADOMCP_`
5. [.NET User Secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets)

You can pre-configure connections in any of these. You can also skip this step entirely and let the LLM add connections dynamically via the `add_connection` tool.

```json
{
  "AllowAnySql": false,
  "Databases": [
    {
      "Name": "mydb",
      "DbType": "SqlServer",
      "ConnectionString": "Server=localhost;Database=MyDb;User Id=sa;Password=***;TrustServerCertificate=true;",
      "Description": "Main business database"
    }
  ]
}
```

Supported `DbType` values: `SqlServer` | `MySql` | `PostgreSql` | `Sqlite` | `Oracle`

> **Security tip**: Use [.NET User Secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets) or environment variables to manage connection strings in production.

#### User-level config example (`~/.adomcp.json`)

Create `~/.adomcp.json` in your home directory to persist personal connections and settings across projects:

```json
{
  "AllowAnySql": true,
  "Databases": [
    {
      "Name": "local-pg",
      "DbType": "PostgreSql",
      "ConnectionString": "Host=localhost;Database=dev;Username=postgres;Password=***;",
      "Description": "Local PostgreSQL dev DB"
    }
  ]
}
```

### 2. Run the server

#### Default mode

By default the server runs in **stdio** mode (the standard MCP transport for local clients).
Use `--http` (or `ADOMCP_MODE=http`) to switch to **HTTP/SSE** mode.

```bash
# stdio mode (default) - all logs go to stderr; stdout carries only MCP JSON-RPC
dnx AdoMcp
```

#### Specify mode manually

```bash
# stdio mode (all logs go to stderr; stdout carries only MCP JSON-RPC)
dnx AdoMcp -- --stdio

# HTTP/SSE mode (default: http://localhost:5100, MCP endpoint /mcp)
dnx AdoMcp -- --http

# Via environment variable
ADOMCP_MODE=http 
dnx AdoMcp
```

#### Enable execute_sql (write operations)

By default the `execute_sql` tool is **disabled** to prevent unauthorised writes.  
Enable it via CLI flag, config file, or environment variable (CLI flag wins when explicitly set):

```bash
# CLI flag
# Combine with transport mode
dnx AdoMcp -- --http --allow-any-sql
```

```jsonc
// ~/.adomcp.json or appsettings.json
{
  "AllowAnySql": true
}
```

```bash
# Environment variable
# ADOMCP_ALLOWANYSQL=true dnx AdoMcp
```

Priority: `--allow-any-sql` CLI > `ADOMCP_ALLOWANYSQL` env > `~/.adomcp.json` `AllowAnySql` > `appsettings.json` `AllowAnySql` > `false` (default).

### 3. Alternative: Install as a global .NET tool

After the package is published to NuGet.org, you can also install it as a global tool:

```bash
dotnet tool install -g AdoMcp
adomcp
```

You can also run dnx directly (installs and runs on demand, .NET 10+):

```bash
dnx AdoMcp -- --allow-any-sql
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

#### Via dnx (after NuGet publish)

```json
{
  "mcpServers": {
    "adomcp": {
      "command": "dnx",
      "args": ["-y","AdoMcp"]
    }
  }
}
```

### HTTP mode

Start the server first:
```bash
dnx -y AdoMcp -- --http
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
| `ADOMCP_ALLOWANYSQL` | Enable the `execute_sql` tool: `true` or `false` (default `false`) |
| `ADOMCP_DATABASES` | JSON-encoded `Databases` array (overrides config-file connections) |

---

## MCP Registries

### Official MCP Registry

This repository now includes `server.json` for the official MCP Registry with the server name `io.github.John0King/adomcp`.

To publish to the official MCP Registry:
1. Publish the NuGet package `AdoMcp` to [NuGet.org](https://www.nuget.org/).
2. Push a version tag such as `v1.0.1`, or manually run the `Publish to NuGet and MCP Registry` GitHub Actions workflow.
3. The workflow publishes the NuGet package, authenticates with GitHub OIDC, and publishes `server.json` to the MCP Registry.


## Build & Pack

```bash
# Build
dotnet build

# Pack as a NuGet tool (supports dnx)
dotnet pack src/AdoMcp -c Release -o ./nupkg

# Publish to NuGet.org (set NUGET_API_KEY first)
dotnet nuget push ./nupkg/AdoMcp.*.nupkg --source https://api.nuget.org/v3/index.json --api-key $NUGET_API_KEY
```
