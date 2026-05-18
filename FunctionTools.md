# Function Calling — How It Works

This document explains how the AI in ChatAI uses C# functions to query a SQLite database,
and traces a real request ("Which products are low on stock?") from the HTTP call all the
way to the final streamed reply.

---

## 1. What is Function Calling?

Function calling (also called tool use) lets an AI model do more than generate text. Instead
of answering purely from training data, the model can pause mid-response, request that a
named function be executed by the host application, receive the result, and then continue
generating a grounded answer.

The model does not run code itself. It emits a structured JSON message that says:
"call this function with these arguments." The application receives that, runs the real
C# code, sends back the result, and the model finishes its reply.

```
User message
    │
    ▼
AI model  ──── tool_call: query_database(sql) ────►  C# code executes SQL
    │                                                        │
    │◄──────────────── tool_result: [{...rows...}] ──────────┘
    │
    ▼
Final text response to user
```

---

## 2. Architecture — The Layered Stack

```
HTTP Request
    │
    ▼
ChatController          (Controllers/ChatController.cs)
    │ calls
    ▼
AIChatService           (Services/AIChatService.cs)
    │ builds ChatOptions with Tools=[...], calls
    ▼
IChatClient             ← UseFunctionInvocation() middleware wraps this
    │  if model returns a tool_call, middleware intercepts and calls:
    ▼
DatabaseTools           (Services/DatabaseTools.cs)
    │ delegates to
    ▼
IDatabaseService / DatabaseService   (Services/DatabaseService.cs)
    │ opens connection to
    ▼
SQLite  (chatai.db)
```

Each layer has a single responsibility:

| Layer | File | Responsibility |
|---|---|---|
| `DatabaseService` | `Services/DatabaseService.cs` | Opens SQLite connections, runs queries, enforces SELECT-only |
| `DatabaseTools` | `Services/DatabaseTools.cs` | Wraps methods as `AIFunction` objects the model can call |
| `AIChatService` | `Services/AIChatService.cs` | Builds `ChatOptions` with the tool list and calls the client |
| `ChatClientBuilder` | `DependencyInjection/ChatClientFactory.cs` | Adds `UseFunctionInvocation()` middleware to the pipeline |

---

## 3. The Three Tools

Tools are defined in `DatabaseTools` as private async methods decorated with `[Description]`
attributes. Those attributes become the tool schema the model reads before deciding what to call.

### 3.1 `query_database`

```csharp
[Description("Execute a SQL SELECT query against the database and return the results as a JSON array of row objects.")]
private async Task<string> QueryDatabaseAsync(
    [Description("A valid SQL SELECT statement")] string sql,
    CancellationToken cancellationToken = default)
```

- **Input:** any SQL string the model constructs
- **Output:** a JSON array of row objects, e.g. `[{"id":6,"name":"Webcam HD","stock":45}]`
- **Guard:** `DatabaseService.ExecuteSelectAsync` rejects anything that does not start with `SELECT`
- **Error handling:** exceptions are caught and returned as `"Error: <message>"` so the model
  can react (e.g. retry with a corrected query) instead of the whole request failing

### 3.2 `list_tables`

```csharp
[Description("List all table names available in the database.")]
private async Task<string> ListTablesAsync(CancellationToken cancellationToken = default)
```

- **Input:** none — no parameters
- **Output:** `["orders","products"]`
- **Purpose:** lets the model discover the schema before committing to a query; useful when the
  model is uncertain what tables exist
- **Implementation:** queries `sqlite_master` filtering out internal `sqlite_%` tables

### 3.3 `get_table_schema`

```csharp
[Description("Get the column schema (name, type, nullable) for a database table.")]
private async Task<string> GetTableSchemaAsync(
    [Description("The name of the table to inspect")] string tableName,
    CancellationToken cancellationToken = default)
```

- **Input:** table name string
- **Output:** JSON array from `PRAGMA table_info(tableName)` — column names, types, nullability,
  defaults, primary key flags
- **Security:** the table name is validated against `^[a-zA-Z_][a-zA-Z0-9_]*$` before being
  interpolated into the PRAGMA statement, preventing any injection via the table name argument

---

## 4. How `AIFunctionFactory.Create` Works

`AIFunctionFactory.Create` (from `Microsoft.Extensions.AI`) converts a C# delegate into an
`AIFunction` — an object that carries both the JSON schema the model needs and the actual
callable implementation.

The key trick is the typed `Func<>` intermediate:

```csharp
// In DatabaseTools constructor:
Func<string, CancellationToken, Task<string>> queryFn = QueryDatabaseAsync;

AIFunctionFactory.Create(queryFn, new AIFunctionFactoryOptions { Name = "query_database" })
```

**Why the `Func<>` intermediate?**

C# cannot implicitly convert a method group to the abstract `System.Delegate` base type —
the target type must be a concrete delegate type (like `Func<>` or `Action<>`). Once
`QueryDatabaseAsync` is assigned to a typed `Func<string, CancellationToken, Task<string>>`,
the delegate's `.Method` property still points to the original `MethodInfo`. That means
`AIFunctionFactory.Create` can reflect on it and read the `[Description]` attributes from
both the method and its parameters to build the tool schema.

The factory produces:

```json
{
  "name": "query_database",
  "description": "Execute a SQL SELECT query against the database and return the results as a JSON array of row objects.",
  "parameters": {
    "type": "object",
    "properties": {
      "sql": {
        "type": "string",
        "description": "A valid SQL SELECT statement"
      }
    },
    "required": ["sql"]
  }
}
```

`CancellationToken` parameters are detected and excluded from the schema automatically —
MEAI injects the request's cancellation token at invocation time instead.

All three `AIFunction` objects are stored once in `DatabaseTools.All` (initialized in the
constructor) and reused for every request.

---

## 5. Wiring: `UseFunctionInvocation()` Middleware

In `ChatClientFactory.cs`, the raw provider client is wrapped before it is registered in DI:

```csharp
return new ChatClientBuilder(inner)
    .UseFunctionInvocation()
    .Build();
```

`UseFunctionInvocation()` is MEAI middleware that intercepts the response stream from the
underlying model. When a `tool_call` message appears instead of text, the middleware:

1. Finds the matching `AIFunction` in `ChatOptions.Tools` by name
2. Deserializes the model's JSON arguments
3. Calls the C# method (passing the real `CancellationToken`)
4. Appends the result as a `tool_result` message
5. Sends the full conversation (original messages + tool call + tool result) back to the
   model for continuation
6. Repeats if the model issues another tool call; returns when the model emits only text

This loop is completely invisible to `AIChatService` and `ChatController`. From their point
of view, they make one call and get back one final response.

The tools are attached per-request in `BuildChatOptions`:

```csharp
private ChatOptions BuildChatOptions() => new()
{
    ModelId   = _settings.Anthropic.Model,
    MaxOutputTokens = _settings.Anthropic.MaxTokens,
    Temperature     = _settings.Anthropic.Temperature,
    Tools = [.._dbTools.All]   // spreads the three AIFunction objects in
};
```

---

## 6. The Database — Schema and Seed Data

`DatabaseSeeder.SeedAsync()` runs once at startup (before `app.Run()`) and uses
`CREATE TABLE IF NOT EXISTS` + `INSERT OR IGNORE`, making it idempotent across restarts.

### `products`

| Column | Type | Notes |
|---|---|---|
| `id` | INTEGER PK | |
| `name` | TEXT | product display name |
| `category` | TEXT | Electronics / Accessories / Furniture |
| `price` | REAL | unit price in USD |
| `stock` | INTEGER | units currently available |

Seed data: 8 products across three categories, with `stock` values ranging from 45 to 300.

### `orders`

| Column | Type | Notes |
|---|---|---|
| `id` | INTEGER PK | |
| `product_id` | INTEGER FK → products.id | |
| `customer` | TEXT | customer name |
| `quantity` | INTEGER | units ordered |
| `total` | REAL | quantity × price at time of order |
| `order_date` | TEXT | ISO 8601 date string |
| `status` | TEXT | `pending` / `shipped` / `delivered` / `cancelled` |

Seed data: 10 orders across 6 customers.

---

## 7. End-to-End Trace — "Which products are low on stock?"

This section walks through every step from the HTTP request to the final reply.

### Step 1 — HTTP POST arrives at the controller

```http
POST /api/chat
{ "message": "Which products are low on stock?" }
```

`ChatController.Chat` validates the request is non-empty and delegates to
`AIChatService.GetResponseAsync`.

### Step 2 — `AIChatService` builds the message list

`BuildMessages` produces:

```
[System]    "You are Ramon, an AI assistant with access to a product database…"
[User]      "Which products are low on stock?"
```

`BuildChatOptions` produces a `ChatOptions` with the three `AIFunction` tools attached.

### Step 3 — The first model call

The message list and tool schemas are sent to Claude. The model reads the system prompt,
sees it has database tools, and decides it needs data before it can answer.

It does not stream text yet. Instead it replies with a tool call:

```json
{
  "type": "tool_use",
  "name": "query_database",
  "input": {
    "sql": "SELECT id, name, stock FROM products ORDER BY stock ASC"
  }
}
```

The model chose to order by stock ascending so it can reason about which are lowest.
It might alternatively call `list_tables` first if it were uncertain about the schema —
the system prompt tells it what tools are available, so in practice it goes straight to
`query_database`.

### Step 4 — `UseFunctionInvocation()` intercepts

The middleware catches the `tool_use` message before it reaches `AIChatService`. It:

1. Looks up `"query_database"` in `ChatOptions.Tools`
2. Deserializes `{ "sql": "SELECT id, name, stock FROM products ORDER BY stock ASC" }`
3. Calls `DatabaseTools.QueryDatabaseAsync("SELECT id, name, stock FROM products ORDER BY stock ASC", ct)`

### Step 5 — `DatabaseTools.QueryDatabaseAsync` runs

The method logs the SQL, then calls `DatabaseService.ExecuteSelectAsync`.

### Step 6 — `DatabaseService.ExecuteSelectAsync` enforces the guard

```csharp
if (!sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Only SELECT queries are permitted.");
```

The query starts with `SELECT`, so it passes. A `SqliteConnection` is opened, the command
runs, and the `SqliteDataReader` is walked row by row into a list of dictionaries:

```csharp
{ "id": 6, "name": "Webcam HD",           "stock": 45 }
{ "id": 2, "name": "Mechanical Keyboard", "stock": 85 }
{ "id": 4, "name": "Standing Desk Mat",   "stock": 60 }
...
```

The connection is disposed by `await using` immediately after reading.

### Step 7 — Result is serialized and returned to the middleware

`DatabaseTools` serializes `result.Rows` to a JSON string and returns it.
The middleware appends two messages to the conversation:

```
[Assistant]  <tool_use: query_database, input: {...}>
[Tool]       <tool_result: [{"id":6,"name":"Webcam HD","stock":45}, ...]>
```

### Step 8 — The second model call

The updated conversation (original messages + tool call + tool result) is sent back to
Claude. Now the model has concrete numbers. It generates its final text response — no
more tool calls needed:

```
The products currently lowest in stock are:

1. Webcam HD — 45 units
2. Standing Desk Mat — 60 units
3. Mechanical Keyboard — 85 units

You may want to consider restocking the Webcam HD first as it has the fewest units remaining.
```

### Step 9 — Response returns through the stack

The middleware returns the final `ChatCompletion` to `AIChatService`, which wraps it in a
`ChatResponse` (text + model ID + token count) and returns it to `ChatController`,
which writes a `200 OK` JSON body.

---

## 8. Security Model

| Threat | Mitigation |
|---|---|
| AI issues a `DELETE`, `DROP`, or `UPDATE` | `ExecuteSelectAsync` checks `StartsWith("SELECT")` and throws before any connection is opened |
| AI passes a malicious table name to `get_table_schema` | Table name is matched against `^[a-zA-Z_][a-zA-Z0-9_]*$`; anything else throws before being interpolated into the PRAGMA |
| SQL syntax error causes an unhandled exception | `DatabaseTools` wraps every tool method in `try/catch` and returns `"Error: <message>"` — the model sees the error and can retry with a corrected query rather than crashing the request |
| Tool call hangs | The `CancellationToken` from the HTTP request is passed through the entire chain; if the client disconnects or the Polly timeout fires, the SQLite reader is cancelled |

---

## 9. Adding a New Tool

1. Add a method to `DatabaseTools` with `[Description]` on the method and parameters.
2. Create a typed `Func<>` local in the constructor pointing to the new method.
3. Call `AIFunctionFactory.Create(fn, new AIFunctionFactoryOptions { Name = "tool_name" })` and add it to the `All` collection initializer.
4. If the tool needs a new database method, add it to `IDatabaseService` and implement it in `DatabaseService`.

No changes are needed to `AIChatService`, `ChatController`, or `Program.cs` — the tool is
automatically included in every request via `Tools = [.._dbTools.All]`.
