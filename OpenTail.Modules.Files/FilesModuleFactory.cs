[assembly: HostModuleFactory(typeof(OpenTail.Modules.Files.FilesModuleFactory))]

namespace OpenTail.Modules.Files;

/// <summary><c>"Type": "files"</c> in modules.json. Config: <c>{ "Root": "D:/shared", "ContentIsUntrusted": true }</c>
/// (a relative Root is relative to the modules file).</summary>
public sealed class FilesModuleFactory : IHostModuleFactory
{
    public string Type => "files";

    public ModuleManifest Describe(string id, System.Text.Json.JsonElement config) => new()
    {
        Id = id,
        DisplayName = "Files",
        Publisher = "OpenTail",
        Description = "List, read and write text files inside one folder; read PDF and Office documents as text.",
        Notes =
        [
            "Sees only the configured Root; links are not followed.",
            "read extracts the text of PDF, Office and OpenDocument files (up to MaxDocumentBytes) unless ExtractDocuments is false.",
            "File content counts as outside content (it taints the caller) unless ContentIsUntrusted is false.",
            "write replaces files and is Write risk: taint does not hold it, so queue it if untrusted text can reach the model.",
        ],
        SuggestedPolicyJson = $$"""
            { "Tools": {
                "{{id}}.list":  { "Mode": "Execute" },
                "{{id}}.read":  { "Mode": "Execute" },
                "{{id}}.write": { "Mode": "Queue", "ExpireAfter": "24:00:00" }
            } }
            """,
    };

    public ValueTask<IHostModule> CreateAsync(ModuleContext context, CancellationToken ct = default)
    {
        var c = context.Bind<Config>();
        if (string.IsNullOrWhiteSpace(c.Root)) throw new ArgumentException("Config.Root is required.");
        var root = Path.IsPathRooted(c.Root) ? c.Root : Path.GetFullPath(Path.Combine(context.BaseDirectory, c.Root));
        if (!Directory.Exists(root)) throw new ArgumentException($"Config.Root {root} does not exist.");
        IHostModule module = new FilesModule(new FilesModuleOptions
        {
            Id = context.Id,
            Root = root,
            MaxReadBytes = c.MaxReadBytes,
            MaxWriteBytes = c.MaxWriteBytes,
            MaxListEntries = c.MaxListEntries,
            ContentIsUntrusted = c.ContentIsUntrusted,
            ExtractDocuments = c.ExtractDocuments,
            MaxDocumentBytes = c.MaxDocumentBytes,
        }, Describe(context.Id, context.Config));
        return ValueTask.FromResult(module);
    }

    private sealed class Config
    {
        public string Root { get; init; } = "";
        public int MaxReadBytes { get; init; } = 256 * 1024;
        public int MaxWriteBytes { get; init; } = 1024 * 1024;
        public int MaxListEntries { get; init; } = 500;
        public bool ContentIsUntrusted { get; init; } = true;
        public bool ExtractDocuments { get; init; } = true;
        public int MaxDocumentBytes { get; init; } = 10 * 1024 * 1024;
    }
}
