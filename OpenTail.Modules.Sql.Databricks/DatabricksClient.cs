using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenTail.Modules.Sql.Databricks;

/// <summary>
/// A small ADO.NET provider over the Databricks SQL Statement Execution API (<c>/api/2.0/sql/statements</c>), so the Sql module's
/// guard, row caps and schema tool apply unchanged. Read-only use: no transactions. One factory per module instance, around the
/// Host's guarded <see cref="HttpClient"/>.
/// </summary>
/// <remarks>
/// Connection string: <c>Host</c> (the workspace URL), <c>WarehouseId</c> (or <c>HttpPath</c>, <c>/sql/1.0/warehouses/&lt;id&gt;</c>),
/// optional <c>Catalog</c> and <c>Schema</c>, and <c>Password</c>: a personal access token, or with <c>ClientId</c> a service
/// principal's OAuth secret (machine-to-machine, <c>/oidc/v1/token</c>).
/// </remarks>
public sealed class DatabricksClientFactory(HttpClient http) : DbProviderFactory
{
    internal HttpClient Http { get; } = http;

    /// <summary>OAuth tokens by host and client id, reused until a minute before they expire.</summary>
    internal Dictionary<string, (string Token, DateTimeOffset Expires)> Tokens { get; } = [];

    internal TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>How often a running statement is polled. Shortened by tests.</summary>
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    public override DbConnection CreateConnection() => new DatabricksConnection(this);
    public override DbCommand CreateCommand() => new DatabricksCommand();
    public override DbParameter CreateParameter() => new DatabricksParameter();
    public override DbConnectionStringBuilder CreateConnectionStringBuilder() => new();
}

public sealed class DatabricksException(string message, string? errorCode = null) : DbException(message)
{
    /// <summary>The API's error_code (for example PARSE_SYNTAX_ERROR), or TIMEOUT.</summary>
    public string? DatabricksErrorCode { get; } = errorCode;
}

public sealed class DatabricksConnection : DbConnection
{
    private readonly DatabricksClientFactory factory;
    private ConnectionState state;

    internal DatabricksConnection(DatabricksClientFactory factory) => this.factory = factory;

    [AllowNull] public override string ConnectionString { get; set; } = "";
    public override string Database => Setting("Catalog") ?? "";
    public override string DataSource => Setting("Host") ?? "";
    public override string ServerVersion => "Databricks SQL";
    public override ConnectionState State => state;

    internal DatabricksClientFactory Factory => factory;
    internal Uri BaseUri { get; private set; } = null!;
    internal string WarehouseId { get; private set; } = "";

    private string? Setting(string key)
    {
        var b = new DbConnectionStringBuilder { ConnectionString = ConnectionString };
        return b.TryGetValue(key, out var v) && v is string s && s.Length > 0 ? s : null;
    }

    public override void Open()
    {
        var host = Setting("Host") ?? throw new ArgumentException("The Databricks connection string needs Host (the workspace URL).");
        if (!host.Contains("://", StringComparison.Ordinal)) host = "https://" + host;
        var uri = new Uri(host.TrimEnd('/') + "/");
        if (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
            throw new ArgumentException("The Databricks Host must use https.");
        WarehouseId = Setting("WarehouseId")
            ?? (Setting("HttpPath") is { } path ? path.TrimEnd('/').Split('/')[^1] : null)
            ?? throw new ArgumentException("The Databricks connection string needs WarehouseId (or HttpPath).");
        if (Setting("Password") is null) throw new ArgumentException("Databricks needs a token: map the 'password' credential.");
        BaseUri = uri;
        state = ConnectionState.Open;
    }

    public override Task OpenAsync(CancellationToken cancellationToken)
    {
        Open();
        return Task.CompletedTask;
    }

    public override void Close() => state = ConnectionState.Closed;
    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        throw new NotSupportedException("Databricks statements run without transactions here; the module uses it read-only.");
    protected override DbCommand CreateDbCommand() => new DatabricksCommand { Connection = this };

    internal string? Catalog => Setting("Catalog");
    internal string? Schema => Setting("Schema");

    /// <summary>The bearer token: the personal access token, or an OAuth token for the service principal.</summary>
    internal async Task<string> TokenAsync(CancellationToken ct)
    {
        var secret = Setting("Password")!;
        if (Setting("ClientId") is not { } clientId) return secret;

        var key = BaseUri + "|" + clientId;
        lock (factory.Tokens)
            if (factory.Tokens.TryGetValue(key, out var cached) && cached.Expires > factory.Clock.GetUtcNow().AddMinutes(1)) return cached.Token;

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, "oidc/v1/token"))
        {
            Content = new FormUrlEncodedContent([new("grant_type", "client_credentials"), new("scope", "all-apis")]),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(clientId + ":" + secret)));
        using var response = await factory.Http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new DatabricksException($"Databricks sign-in failed ({(int)response.StatusCode}); check ClientId and the client secret.");
        var json = JsonNode.Parse(body)!;
        var token = json["access_token"]?.GetValue<string>() ?? throw new DatabricksException("Databricks sign-in returned no access token.");
        var expires = factory.Clock.GetUtcNow().AddSeconds(json["expires_in"]?.GetValue<int>() ?? 3600);
        lock (factory.Tokens) factory.Tokens[key] = (token, expires);
        return token;
    }
}

public sealed class DatabricksParameter : DbParameter
{
    public override DbType DbType { get; set; } = DbType.String;
    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
    public override bool IsNullable { get; set; } = true;
    [AllowNull] public override string ParameterName { get; set; } = "";
    [AllowNull] public override string SourceColumn { get; set; } = "";
    public override object? Value { get; set; }
    public override bool SourceColumnNullMapping { get; set; }
    public override int Size { get; set; }
    public override void ResetDbType() => DbType = DbType.String;

    /// <summary>The API's <c>{ name, value, type }</c>: values travel as text with a SQL type to cast to.</summary>
    internal JsonObject ToJson()
    {
        var (type, text) = Value switch
        {
            null or DBNull => ("STRING", null),
            bool b => ("BOOLEAN", b ? "true" : "false"),
            long or int or short or byte => ("BIGINT", Convert.ToString(Value, CultureInfo.InvariantCulture)),
            double d => ("DOUBLE", d.ToString("R", CultureInfo.InvariantCulture)),
            float f => ("DOUBLE", f.ToString("R", CultureInfo.InvariantCulture)),
            decimal m => ("DECIMAL(38,18)", m.ToString(CultureInfo.InvariantCulture)),
            _ => ("STRING", Convert.ToString(Value, CultureInfo.InvariantCulture)),
        };
        return new JsonObject { ["name"] = ParameterName.TrimStart(':', '@'), ["value"] = text, ["type"] = type };
    }
}

public sealed class DatabricksParameterCollection : DbParameterCollection
{
    private readonly List<DatabricksParameter> items = [];
    public override int Count => items.Count;
    public override object SyncRoot => items;
    public override int Add(object value) { items.Add((DatabricksParameter)value); return items.Count - 1; }
    public override void AddRange(Array values) { foreach (var v in values) Add(v); }
    public override void Clear() => items.Clear();
    public override bool Contains(object value) => items.Contains((DatabricksParameter)value);
    public override bool Contains(string value) => IndexOf(value) >= 0;
    public override void CopyTo(Array array, int index) => ((ICollection)items).CopyTo(array, index);
    public override IEnumerator GetEnumerator() => items.GetEnumerator();
    public override int IndexOf(object value) => items.IndexOf((DatabricksParameter)value);
    public override int IndexOf(string parameterName) => items.FindIndex(p => p.ParameterName == parameterName);
    public override void Insert(int index, object value) => items.Insert(index, (DatabricksParameter)value);
    public override void Remove(object value) => items.Remove((DatabricksParameter)value);
    public override void RemoveAt(int index) => items.RemoveAt(index);
    public override void RemoveAt(string parameterName) => items.RemoveAt(IndexOf(parameterName));
    protected override DbParameter GetParameter(int index) => items[index];
    protected override DbParameter GetParameter(string parameterName) => items[IndexOf(parameterName)];
    protected override void SetParameter(int index, DbParameter value) => items[index] = (DatabricksParameter)value;
    protected override void SetParameter(string parameterName, DbParameter value) => items[IndexOf(parameterName)] = (DatabricksParameter)value;
    internal IEnumerable<DatabricksParameter> All => items;
}

public sealed class DatabricksCommand : DbCommand
{
    private readonly DatabricksParameterCollection parameters = new();
    private CancellationTokenSource? running;

    [AllowNull] public override string CommandText { get; set; } = "";
    public override int CommandTimeout { get; set; } = 30;
    public override CommandType CommandType { get; set; } = CommandType.Text;
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }
    protected override DbConnection? DbConnection { get; set; }
    protected override DbParameterCollection DbParameterCollection => parameters;
    protected override DbTransaction? DbTransaction { get; set; }

    /// <summary><c>key:value</c> (the Sql module sets <c>opentail:&lt;action id&gt;</c>), sent as one entry of <c>query_tags</c>.</summary>
    public string? QueryTag { get; set; }

    public override void Cancel() => running?.Cancel();
    public override void Prepare() { }
    protected override DbParameter CreateDbParameter() => new DatabricksParameter();

    public override int ExecuteNonQuery() => ExecuteNonQueryAsync(CancellationToken.None).GetAwaiter().GetResult();
    public override object? ExecuteScalar() => ExecuteScalarAsync(CancellationToken.None).GetAwaiter().GetResult();
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => ExecuteDbDataReaderAsync(behavior, CancellationToken.None).GetAwaiter().GetResult();

    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        await using var reader = await ExecuteDbDataReaderAsync(CommandBehavior.Default, cancellationToken).ConfigureAwait(false);
        return -1;
    }

    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        await using var reader = await ExecuteDbDataReaderAsync(CommandBehavior.Default, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && reader.FieldCount > 0 ? reader.GetValue(0) : null;
    }

    private static readonly HashSet<string> Running = ["PENDING", "RUNNING"];

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        var conn = DbConnection as DatabricksConnection ?? throw new InvalidOperationException("The command has no open Databricks connection.");
        if (conn.State != ConnectionState.Open) throw new InvalidOperationException("The connection is not open.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        running = cts;
        var deadline = CommandTimeout > 0 ? conn.Factory.Clock.GetUtcNow().AddSeconds(CommandTimeout) : DateTimeOffset.MaxValue;
        var ct = cts.Token;

        var body = new JsonObject
        {
            ["statement"] = CommandText,
            ["warehouse_id"] = conn.WarehouseId,
            ["disposition"] = "INLINE",
            ["format"] = "JSON_ARRAY",
            // Wait up to 10 s in the first request, then poll: the Host's client has its own per-request timeout.
            ["wait_timeout"] = "10s",
            ["on_wait_timeout"] = "CONTINUE",
            ["parameters"] = new JsonArray(parameters.All.Select(p => (JsonNode)p.ToJson()).ToArray()),
        };
        if (conn.Catalog is { } catalog) body["catalog"] = catalog;
        if (conn.Schema is { } schema) body["schema"] = schema;
        if (QueryTag is { Length: > 0 } tag)
        {
            var at = tag.IndexOf(':', StringComparison.Ordinal);
            body["query_tags"] = new JsonArray(new JsonObject { ["key"] = at < 0 ? tag : tag[..at], ["value"] = at < 0 ? null : tag[(at + 1)..] });
        }

        string? statementId = null;
        try
        {
            var response = await SendAsync(conn, HttpMethod.Post, "api/2.0/sql/statements", body, ct).ConfigureAwait(false);
            statementId = response["statement_id"]?.GetValue<string>();
            while (State(response) is { } state && Running.Contains(state))
            {
                if (conn.Factory.Clock.GetUtcNow() >= deadline)
                {
                    await CancelAsync(conn, statementId).ConfigureAwait(false);
                    throw new DatabricksException($"The statement ran past the {CommandTimeout} s timeout and was cancelled.", "TIMEOUT");
                }
                await Task.Delay(conn.Factory.PollInterval, ct).ConfigureAwait(false);
                response = await SendAsync(conn, HttpMethod.Get, $"api/2.0/sql/statements/{Uri.EscapeDataString(statementId!)}", null, ct).ConfigureAwait(false);
            }

            return State(response) switch
            {
                "SUCCEEDED" => new DatabricksDataReader(conn, response),
                "FAILED" => throw new DatabricksException(
                    "Databricks: " + (response["status"]?["error"]?["message"]?.GetValue<string>() ?? "the statement failed."),
                    response["status"]?["error"]?["error_code"]?.GetValue<string>()),
                var other => throw new DatabricksException($"Databricks: the statement ended as {other ?? "unknown"}."),
            };
        }
        catch (OperationCanceledException) when (statementId is not null)
        {
            await CancelAsync(conn, statementId).ConfigureAwait(false);
            throw;
        }
        finally
        {
            running = null;
        }
    }

    private static string? State(JsonNode response) => response["status"]?["state"]?.GetValue<string>();

    /// <summary>Asks the warehouse to stop the statement; best effort, never throws.</summary>
    private static async Task CancelAsync(DatabricksConnection conn, string? statementId)
    {
        if (statementId is null) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await SendAsync(conn, HttpMethod.Post, $"api/2.0/sql/statements/{Uri.EscapeDataString(statementId)}/cancel", new JsonObject(), cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or DatabricksException) { }
    }

    internal static async Task<JsonNode> SendAsync(DatabricksConnection conn, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(conn.BaseUri, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await conn.TokenAsync(ct).ConfigureAwait(false));
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await conn.Factory.Http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string? message = null;
            try { message = JsonNode.Parse(text)?["message"]?.GetValue<string>(); } catch (JsonException) { }
            throw new DatabricksException($"Databricks returned {(int)response.StatusCode}: {message ?? response.ReasonPhrase}");
        }
        return JsonNode.Parse(text.Length == 0 ? "{}" : text) ?? new JsonObject();
    }
}

/// <summary>Rows of a <c>JSON_ARRAY</c> result, chunk by chunk; values arrive as text and are converted by column type.</summary>
public sealed class DatabricksDataReader : DbDataReader
{
    private readonly DatabricksConnection conn;
    private readonly (string Name, string Type)[] columns;
    private JsonArray rows;
    private string? nextChunk;
    private int index = -1;
    private bool closed;

    internal DatabricksDataReader(DatabricksConnection conn, JsonNode response)
    {
        this.conn = conn;
        columns = (response["manifest"]?["schema"]?["columns"]?.AsArray() ?? [])
            .Select(c => (c!["name"]!.GetValue<string>(), (c["type_name"]?.GetValue<string>() ?? "STRING").ToUpperInvariant()))
            .ToArray();
        rows = response["result"]?["data_array"]?.AsArray() ?? [];
        nextChunk = response["result"]?["next_chunk_internal_link"]?.GetValue<string>();
    }

    public override int FieldCount => columns.Length;
    public override bool HasRows => rows.Count > 0 || nextChunk is not null;
    public override bool IsClosed => closed;
    public override int RecordsAffected => -1;
    public override int Depth => 0;
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read() => ReadAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        if (++index < rows.Count) return true;
        while (nextChunk is not null)
        {
            var chunk = await DatabricksCommand.SendAsync(conn, HttpMethod.Get, nextChunk.TrimStart('/'), null, cancellationToken).ConfigureAwait(false);
            rows = chunk["data_array"]?.AsArray() ?? [];
            nextChunk = chunk["next_chunk_internal_link"]?.GetValue<string>();
            index = 0;
            if (rows.Count > 0) return true;
        }
        return false;
    }

    public override bool NextResult() => false;
    public override void Close() => closed = true;

    private string? Raw(int ordinal) => rows[index]?.AsArray()[ordinal]?.GetValue<string>();

    public override bool IsDBNull(int ordinal) => Raw(ordinal) is null;

    public override object GetValue(int ordinal)
    {
        if (Raw(ordinal) is not { } s) return DBNull.Value;
        var type = columns[ordinal].Type;
        return type switch
        {
            "BOOLEAN" => bool.Parse(s),
            "TINYINT" or "SMALLINT" or "INT" or "BIGINT" or "LONG" or "SHORT" or "BYTE" => long.Parse(s, CultureInfo.InvariantCulture),
            "FLOAT" or "DOUBLE" => double.Parse(s, CultureInfo.InvariantCulture),
            "DECIMAL" => decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture),
            "DATE" => DateOnly.Parse(s, CultureInfo.InvariantCulture),
            "TIMESTAMP" => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            "TIMESTAMP_NTZ" => DateTime.Parse(s, CultureInfo.InvariantCulture),
            "BINARY" => Convert.FromBase64String(s),
            _ => s, // STRING, and ARRAY, MAP, STRUCT, VARIANT and INTERVAL as their JSON or text form
        };
    }

    public override int GetValues(object[] values)
    {
        var n = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < n; i++) values[i] = GetValue(i);
        return n;
    }

    public override string GetName(int ordinal) => columns[ordinal].Name;
    public override int GetOrdinal(string name) => Array.FindIndex(columns, c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    public override string GetDataTypeName(int ordinal) => columns[ordinal].Type;

    public override Type GetFieldType(int ordinal) => columns[ordinal].Type switch
    {
        "BOOLEAN" => typeof(bool),
        "TINYINT" or "SMALLINT" or "INT" or "BIGINT" or "LONG" or "SHORT" or "BYTE" => typeof(long),
        "FLOAT" or "DOUBLE" => typeof(double),
        "DECIMAL" => typeof(decimal),
        "DATE" => typeof(DateOnly),
        "TIMESTAMP" => typeof(DateTimeOffset),
        "TIMESTAMP_NTZ" => typeof(DateTime),
        "BINARY" => typeof(byte[]),
        _ => typeof(string),
    };

    public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);
    public override byte GetByte(int ordinal) => Convert.ToByte(GetValue(ordinal), CultureInfo.InvariantCulture);
    public override char GetChar(int ordinal) => GetString(ordinal)[0];
    public override DateTime GetDateTime(int ordinal) => GetValue(ordinal) switch { DateTimeOffset d => d.UtcDateTime, DateOnly d => d.ToDateTime(TimeOnly.MinValue), var v => (DateTime)v };
    public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(GetValue(ordinal), CultureInfo.InvariantCulture);
    public override double GetDouble(int ordinal) => Convert.ToDouble(GetValue(ordinal), CultureInfo.InvariantCulture);
    public override float GetFloat(int ordinal) => Convert.ToSingle(GetValue(ordinal), CultureInfo.InvariantCulture);
    public override Guid GetGuid(int ordinal) => Guid.Parse(GetString(ordinal));
    public override short GetInt16(int ordinal) => Convert.ToInt16(GetValue(ordinal), CultureInfo.InvariantCulture);
    public override int GetInt32(int ordinal) => Convert.ToInt32(GetValue(ordinal), CultureInfo.InvariantCulture);
    public override long GetInt64(int ordinal) => Convert.ToInt64(GetValue(ordinal), CultureInfo.InvariantCulture);
    public override string GetString(int ordinal) => Raw(ordinal) ?? throw new InvalidCastException("The value is null.");
    public override IEnumerator GetEnumerator() => new DbEnumerator(this);

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var bytes = (byte[])GetValue(ordinal);
        if (buffer is null) return bytes.Length;
        var n = (int)Math.Min(length, bytes.Length - dataOffset);
        Array.Copy(bytes, dataOffset, buffer, bufferOffset, n);
        return n;
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var s = GetString(ordinal);
        if (buffer is null) return s.Length;
        var n = (int)Math.Min(length, s.Length - dataOffset);
        s.CopyTo((int)dataOffset, buffer, bufferOffset, n);
        return n;
    }
}
