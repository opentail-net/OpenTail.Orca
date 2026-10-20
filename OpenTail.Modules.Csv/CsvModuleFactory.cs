[assembly: HostModuleFactory(typeof(OpenTail.Modules.Csv.CsvModuleFactory))]

namespace OpenTail.Modules.Csv;

/// <summary><c>"Type": "csv"</c> in modules.json. Config: <c>{ "Root": "data/exports", "Delimiter": ";" }</c>
/// (a relative Root is relative to the modules file).</summary>
public sealed class CsvModuleFactory : IHostModuleFactory
{
    public string Type => "csv";

    public ModuleManifest Describe(string id, System.Text.Json.JsonElement config) => new()
    {
        Id = id,
        DisplayName = "CSV files",
        Publisher = "OpenTail",
        Description = "Describe CSV files and read their rows, inside one folder.",
        Notes =
        [
            "Sees only the configured Root; links are not followed.",
            "Cell text counts as outside content (it taints the caller) unless ContentIsUntrusted is false.",
            "Read only: nothing is ever written.",
        ],
        SuggestedPolicyJson = $$"""
            { "Tools": {
                "{{id}}.describe":  { "Mode": "Execute" },
                "{{id}}.read_rows": { "Mode": "Execute" }
            } }
            """,
    };

    public ValueTask<IHostModule> CreateAsync(ModuleContext context, CancellationToken ct = default)
    {
        var c = context.Bind<Config>();
        if (string.IsNullOrWhiteSpace(c.Root)) throw new ArgumentException("Config.Root is required.");
        if (c.Delimiter.Length != 1) throw new ArgumentException("Config.Delimiter must be one character.");
        var root = Path.IsPathRooted(c.Root) ? c.Root : Path.GetFullPath(Path.Combine(context.BaseDirectory, c.Root));
        if (!Directory.Exists(root)) throw new ArgumentException($"Config.Root {root} does not exist.");
        IHostModule module = new CsvModule(new CsvModuleOptions
        {
            Id = context.Id,
            Manifest = Describe(context.Id, context.Config),
            Root = root,
            Delimiter = c.Delimiter[0],
            MaxRows = c.MaxRows,
            MaxFileBytes = c.MaxFileBytes,
            MaxCellChars = c.MaxCellChars,
            ContentIsUntrusted = c.ContentIsUntrusted,
        });
        return ValueTask.FromResult(module);
    }

    private sealed class Config
    {
        public string Root { get; init; } = "";
        public string Delimiter { get; init; } = ",";
        public int MaxRows { get; init; } = 500;
        public int MaxFileBytes { get; init; } = 20 * 1024 * 1024;
        public int MaxCellChars { get; init; } = 1000;
        public bool ContentIsUntrusted { get; init; } = true;
    }
}
