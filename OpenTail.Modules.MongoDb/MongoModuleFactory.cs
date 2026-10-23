using MongoDB.Driver;

[assembly: HostModuleFactory(typeof(OpenTail.Modules.MongoDb.MongoModuleFactory))]

namespace OpenTail.Modules.MongoDb;

/// <summary>
/// <c>"Type": "mongodb"</c> in modules.json.
/// <code>
/// "Config": { "ConnectionString": "mongodb+srv://agent_ro@cluster0.example.mongodb.net/?readPreference=secondaryPreferred",
///             "Database": "sales", "Collections": [ "orders", "customers" ] },
/// "Credentials": { "password": { "Env": "SALES_MONGO_PASSWORD", "Kind": "Secret" } }
/// </code>
/// The connection string holds no password: it comes from the <c>password</c> credential, or the whole string from the
/// <c>connection</c> credential, both read on first use. Give the user the built-in <c>read</c> role on the database only.
/// </summary>
public sealed class MongoModuleFactory : IHostModuleFactory
{
    public string Type => "mongodb";

    public ModuleManifest Describe(string id, System.Text.Json.JsonElement config) => new()
    {
        Id = id,
        DisplayName = "MongoDB",
        Publisher = "OpenTail",
        Description = "Read-only queries over allowed MongoDB collections: collections, find, aggregate, count.",
        Credentials =
        [
            new CredentialRequirement("password", CredentialKind.Secret, "The database user's password, added to the connection string") { Optional = true },
            new CredentialRequirement("connection", CredentialKind.Secret, "The whole connection string, instead of Config.ConnectionString") { Optional = true },
        ],
        Notes =
        [
            "Give the user the built-in read role on one database: the server is the read-only boundary, the module's operator allow-list the second line.",
            "One collection per query: no $lookup, $graphLookup, $unionWith, $facet, $regex, $push or $addToSet in this version; server-side JavaScript and writes never.",
            "Every operation has a server-side time limit (maxTimeMS) and the comment opentail:<action id>.",
            "Database traffic does not go through the Host's HTTP guard; the server is whatever the connection string names.",
            "Values count as outside content (they taint the caller) unless ContentIsUntrusted is false.",
        ],
        SuggestedPolicyJson = $$"""{ "Tools": { "{{id}}.collections": { "Mode": "Execute" }, "{{id}}.find": { "Mode": "Execute" }, "{{id}}.aggregate": { "Mode": "Execute" }, "{{id}}.count": { "Mode": "Execute" } } }""",
    };

    public ValueTask<IHostModule> CreateAsync(ModuleContext context, CancellationToken ct = default)
    {
        var c = context.Bind<Config>();
        var credentials = context.Credentials;
        var hasConnection = credentials.Names.Contains("connection");
        if (!hasConnection && string.IsNullOrWhiteSpace(c.ConnectionString))
            throw new ArgumentException("Config.ConnectionString is required (or map the 'connection' credential).");
        if (!hasConnection && !string.IsNullOrEmpty(new MongoUrl(c.ConnectionString).Password))
            throw new ArgumentException("Config.ConnectionString must not contain a password; map the 'password' credential instead.");
        if (c.Collections is not { Count: > 0 })
            throw new ArgumentException("Config.Collections must list the collections the module may read.");

        IMongoDatabase? database = null;
        var gate = new SemaphoreSlim(1, 1);
        async ValueTask<IMongoDatabase> DatabaseAsync(CancellationToken token)
        {
            if (database is not null) return database;
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (database is not null) return database;
                var url = new MongoUrlBuilder(hasConnection ? (await credentials.GetAsync("connection", token).ConfigureAwait(false)).Secret : c.ConnectionString);
                if (!hasConnection && credentials.Names.Contains("password"))
                    url.Password = (await credentials.GetAsync("password", token).ConfigureAwait(false)).Secret;
                var name = c.Database ?? url.DatabaseName
                    ?? throw new ArgumentException("Config.Database is required (or name the database in the connection string).");
                return database = new MongoClient(url.ToMongoUrl()).GetDatabase(name);
            }
            finally
            {
                gate.Release();
            }
        }

        IHostModule module = new MongoModule(new MongoModuleOptions
        {
            Name = context.Id,
            Manifest = Describe(context.Id, context.Config),
            Database = DatabaseAsync,
            Collections = c.Collections,
            MaxRows = c.MaxRows,
            MaxCellChars = c.MaxCellChars,
            MaxResponseBytes = c.MaxResponseBytes,
            CommandTimeoutSeconds = c.CommandTimeoutSeconds,
            SampleSize = c.SampleSize,
            ContentIsUntrusted = c.ContentIsUntrusted,
        });
        return ValueTask.FromResult(module);
    }

    private sealed class Config
    {
        public string? ConnectionString { get; init; }
        public string? Database { get; init; }
        public List<string> Collections { get; init; } = [];
        public int MaxRows { get; init; } = 200;
        public int MaxCellChars { get; init; } = 2000;
        public int MaxResponseBytes { get; init; } = 256 * 1024;
        public int CommandTimeoutSeconds { get; init; } = 15;
        public int SampleSize { get; init; } = 20;
        public bool ContentIsUntrusted { get; init; } = true;
    }
}
