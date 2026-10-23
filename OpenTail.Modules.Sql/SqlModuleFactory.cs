using System.Data.Common;
using System.Reflection;
using Microsoft.Data.Sqlite;

[assembly: HostModuleFactory(typeof(OpenTail.Modules.Sql.SqlModuleFactory))]

namespace OpenTail.Modules.Sql;

/// <summary>
/// <c>"Type": "sql"</c> in modules.json.
/// <code>
/// "Config": { "Provider": "sqlserver", "ConnectionString": "Server=db1;Database=crm;User Id=agent_ro;Encrypt=true" },
/// "Credentials": { "password": { "Env": "CRM_DB_PASSWORD", "Kind": "Secret" } }
/// </code>
/// Providers: <c>sqlite</c> (built in); <c>sqlserver</c> (also Microsoft Fabric and Azure Synapse), <c>postgres</c>, <c>redshift</c>,
/// <c>mysql</c>, <c>snowflake</c> (the deployment references the driver package); <c>oracle</c> (the deployment references
/// <c>OpenTail.Modules.Sql.Oracle</c>); or <c>ProviderFactory</c> naming any <see cref="DbProviderFactory"/> type. The connection string holds no secret: the password comes from
/// the <c>password</c> credential, or the whole string from the <c>connection</c> credential, both read when a tool runs.
/// With <c>AllowWrites</c> on a server database, write and delete use a second login: <c>Config.WriteConnectionString</c> with the
/// <c>write-password</c> credential, or the <c>write-connection</c> credential. <c>SharedLoginForWrites: true</c> opts out.
/// Snowflake is read-only (no AllowWrites); a service user signs in with a key pair, the <c>private-key</c> credential (PEM text)
/// and optionally <c>private-key-password</c>. Queries get a LIMIT and a query tag, since every query costs warehouse time.
/// </summary>
public sealed class SqlModuleFactory : IHostModuleFactory
{
    public string Type => "sql";

    public ModuleManifest Describe(string id, System.Text.Json.JsonElement config)
    {
        var writes = config.ValueKind == System.Text.Json.JsonValueKind.Object
            && config.EnumerateObject().Any(p => p.Name.Equals("AllowWrites", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == System.Text.Json.JsonValueKind.True);
        var name = config.ValueKind == System.Text.Json.JsonValueKind.Object
            ? config.EnumerateObject().FirstOrDefault(p => p.Name.Equals("Provider", StringComparison.OrdinalIgnoreCase)) is { Value.ValueKind: System.Text.Json.JsonValueKind.String } p
                ? p.Value.GetString() : null
            : null;
        IReadOnlyList<string> providerNotes = name is null ? [] : TryFind(name)?.Notes ?? [];
        return new()
        {
            Id = id,
            DisplayName = "SQL database",
            Publisher = "OpenTail",
            Description = "Read-only SELECT queries" + (writes ? ", and previewed INSERT/UPDATE/DELETE." : "."),
            Credentials =
            [
                new CredentialRequirement("password", CredentialKind.Secret, "Database password, added to the connection string") { Optional = true },
                new CredentialRequirement("connection", CredentialKind.Secret, "The whole connection string, instead of Config.ConnectionString") { Optional = true },
                new CredentialRequirement("write-password", CredentialKind.Secret, "Password of the write login, added to Config.WriteConnectionString") { Optional = true },
                new CredentialRequirement("write-connection", CredentialKind.Secret, "The write login's whole connection string, instead of Config.WriteConnectionString") { Optional = true },
                new CredentialRequirement("private-key", CredentialKind.Secret, "Snowflake: the service user's private key (PEM text), for key-pair sign-in") { Optional = true },
                new CredentialRequirement("private-key-password", CredentialKind.Secret, "Snowflake: the passphrase of an encrypted private key") { Optional = true },
            ],
            Notes =
            [
                "Connects with the login you configure; give it the least privilege. schema and query use a read-only login; with AllowWrites, write and delete use a second login.",
                "Database traffic does not go through the Host's HTTP guard; the database server is whatever the connection string names.",
                "Row values count as outside content (they taint the caller) unless ContentIsUntrusted is false.",
                writes ? "write (INSERT/UPDATE) is Write risk, which taint does not hold: queue it. delete is Destructive." : "Writes are off (AllowWrites false).",
                .. providerNotes,
            ],
            SuggestedPolicyJson = writes
                ? $$"""{ "Tools": { "{{id}}.schema": { "Mode": "Execute" }, "{{id}}.query": { "Mode": "Execute" }, "{{id}}.write": { "Mode": "Queue" }, "{{id}}.delete": { "Mode": "Queue" } } }"""
                : $$"""{ "Tools": { "{{id}}.schema": { "Mode": "Execute" }, "{{id}}.query": { "Mode": "Execute" } } }""",
        };
    }

    public ValueTask<IHostModule> CreateAsync(ModuleContext context, CancellationToken ct = default)
    {
        var c = context.Bind<Config>();
        var hasConnection = context.Credentials.Names.Contains("connection");
        if (!hasConnection && string.IsNullOrWhiteSpace(c.ConnectionString))
            throw new ArgumentException("Config.ConnectionString is required (or map the 'connection' credential).");
        var sqlite = string.Equals(c.Provider, "sqlite", StringComparison.OrdinalIgnoreCase) && c.ProviderFactory is not { Length: > 0 };
        var provider = sqlite ? null : ResolveProvider(c);
        if (provider is { ReadOnly: true } && c.AllowWrites)
            throw new ArgumentException($"{provider.Name} is read-only in this module: remove AllowWrites. Loading data into a warehouse is a job for ETL tools.");
        var factory = provider is null ? SqliteFactory.Instance : FactoryOf(provider);
        var snowflake = provider == SqlProviders.Snowflake;

        if (!hasConnection && ContainsSecret(factory, c.ConnectionString!))
            throw new ArgumentException("Config.ConnectionString must not contain a password; map the 'password' credential instead.");

        var credentials = context.Credentials;
        var hasWriteConnection = credentials.Names.Contains("write-connection");
        var hasWriteLogin = hasWriteConnection || !string.IsNullOrWhiteSpace(c.WriteConnectionString);
        if (!hasWriteConnection && hasWriteLogin && ContainsSecret(factory, c.WriteConnectionString!))
            throw new ArgumentException("Config.WriteConnectionString must not contain a password; map the 'write-password' credential instead.");
        if (c.AllowWrites && !sqlite && !hasWriteLogin && !c.SharedLoginForWrites)
            throw new ArgumentException(
                "AllowWrites on a server database needs a second login for writes: set Config.WriteConnectionString (with the 'write-password' credential) " +
                "or map the 'write-connection' credential, and keep the main login read-only. SharedLoginForWrites: true opts out, " +
                "so a statement the guard misreads could then write through the query tool.");

        var baseDirectory = context.BaseDirectory;
        async ValueTask<DbConnection> OpenAsync(bool write, CancellationToken token)
        {
            var useWriteLogin = write && hasWriteLogin;
            var (connectionName, passwordName, configured) = useWriteLogin
                ? ("write-connection", "write-password", c.WriteConnectionString)
                : ("connection", "password", c.ConnectionString);
            var whole = useWriteLogin ? hasWriteConnection : hasConnection;
            var b = factory.CreateConnectionStringBuilder() ?? new DbConnectionStringBuilder();
            b.ConnectionString = whole ? (await credentials.GetAsync(connectionName, token)).Secret : configured;
            if (!whole && credentials.Names.Contains(passwordName)) b["Password"] = (await credentials.GetAsync(passwordName, token)).Secret;
            if (!whole && snowflake && credentials.Names.Contains("private-key"))
            {
                // Set through the builder, which quotes the PEM text; the driver parses it back with the same builder.
                if (!b.ContainsKey("authenticator")) b["authenticator"] = "snowflake_jwt";
                b["private_key"] = (await credentials.GetAsync("private-key", token)).Secret;
                if (credentials.Names.Contains("private-key-password")) b["private_key_pwd"] = (await credentials.GetAsync("private-key-password", token)).Secret;
            }
            provider?.PrepareConnection?.Invoke(b);
            if (b is SqliteConnectionStringBuilder s)
            {
                if (!Path.IsPathRooted(s.DataSource) && s.DataSource != ":memory:") s.DataSource = Path.GetFullPath(Path.Combine(baseDirectory, s.DataSource));
                // SQLite needs no second login: schema and query always open the file read-only.
                s.Mode = write && c.AllowWrites ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly;
            }
            var conn = factory.CreateConnection() ?? throw new InvalidOperationException("The provider did not create a connection.");
            conn.ConnectionString = b.ConnectionString;
            return conn;
        }

        var defaults = provider ?? new SqlProvider { Name = "sqlite", Dialect = SqlDialect.Sqlite };
        IHostModule module = new SqlModule(new SqlModuleOptions(defaults)
        {
            Name = context.Id,
            Manifest = Describe(context.Id, context.Config),
            ConnectionFactoryAsync = token => OpenAsync(false, token),
            WriteConnectionFactoryAsync = token => OpenAsync(true, token),
            ReadOnlyQueryTransaction = c.ReadOnlyQueryTransaction ?? defaults.ReadOnlyStatement is not null,
            AddRowLimit = c.AddRowLimit ?? defaults.AddRowLimit,
            AllowWrites = c.AllowWrites,
            MaxRows = c.MaxRows,
            MaxCellChars = c.MaxCellChars,
            CommandTimeoutSeconds = c.CommandTimeoutSeconds,
            PreviewByRollback = c.PreviewByRollback,
            ContentIsUntrusted = c.ContentIsUntrusted,
            SchemaQuery = c.SchemaQuery ?? defaults.SchemaQuery,
            MaxSchemaRows = c.MaxSchemaRows,
            Schemas = c.Schemas,
        });
        return ValueTask.FromResult(module);
    }

    /// <summary>The named provider, a custom ProviderFactory (every dialect's refusals), or an error naming what to reference.</summary>
    private static SqlProvider ResolveProvider(Config c)
    {
        if (c.ProviderFactory is { Length: > 0 } custom) return new SqlProvider { Name = "custom", Dialect = SqlDialect.Unknown, FactoryType = custom };
        if (c.Provider is { } name && SqlProviders.BuiltIn.TryGetValue(name, out var builtIn)) return builtIn;
        if (c.Provider is { } addOnName && SqlProviders.AddOns.TryGetValue(addOnName, out var addOn))
            return TryFind(addOnName) ?? throw new ArgumentException($"Provider '{addOnName}' was not found: reference package {addOn.Package} in the deployment.");
        throw new ArgumentException(
            $"Config.Provider must be sqlite, {string.Join(", ", SqlProviders.BuiltIn.Keys.Concat(SqlProviders.AddOns.Keys))}, or set ProviderFactory.");
    }

    /// <summary>A built-in provider, or an add-on's when its package is loaded.</summary>
    private static SqlProvider? TryFind(string name)
    {
        if (SqlProviders.BuiltIn.TryGetValue(name, out var builtIn)) return builtIn;
        if (!SqlProviders.AddOns.TryGetValue(name, out var addOn)) return null;
        return System.Type.GetType(addOn.TypeName, throwOnError: false)?.GetField("Provider", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as SqlProvider;
    }

    private static DbProviderFactory FactoryOf(SqlProvider provider)
    {
        if (provider.Factory is { } factory) return factory;
        var typeName = provider.FactoryType ?? throw new ArgumentException($"Provider '{provider.Name}' names no driver.");
        var hint = provider.Package is { } package ? "package " + package : "the package that contains it";
        var type = System.Type.GetType(typeName, throwOnError: false)
            ?? throw new ArgumentException($"Provider type '{typeName}' was not found: reference {hint} in the deployment.");
        var instance = type.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as DbProviderFactory;
        return instance ?? throw new ArgumentException($"'{typeName}' is not a DbProviderFactory with a static Instance field.");
    }

    private static bool ContainsSecret(DbProviderFactory factory, string connectionString)
    {
        var b = factory.CreateConnectionStringBuilder() ?? new DbConnectionStringBuilder();
        b.ConnectionString = connectionString;
        return new[] { "Password", "Pwd", "Private_Key", "Private_Key_Pwd", "Token", "ProxyPassword" }.Any(k => b.TryGetValue(k, out var v) && v is string s && s.Length > 0);
    }

    private sealed class Config
    {
        public string? Provider { get; init; }
        public string? ProviderFactory { get; init; }
        public string? ConnectionString { get; init; }
        public bool AllowWrites { get; init; }
        public string? WriteConnectionString { get; init; }
        public bool SharedLoginForWrites { get; init; }
        public bool? ReadOnlyQueryTransaction { get; init; }
        public bool? AddRowLimit { get; init; }
        public int MaxRows { get; init; } = 200;
        public int MaxCellChars { get; init; } = 2000;
        public int CommandTimeoutSeconds { get; init; } = 15;
        public bool PreviewByRollback { get; init; } = true;
        public bool ContentIsUntrusted { get; init; } = true;
        public string? SchemaQuery { get; init; }
        public int MaxSchemaRows { get; init; } = 2000;
        public List<string> Schemas { get; init; } = [];
    }
}
