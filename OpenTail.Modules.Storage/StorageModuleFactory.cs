using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using OpenTail.Connectivity.Storage;
using OpenTail.Connectivity.Storage.S3;

[assembly: HostModuleFactory(typeof(OpenTail.Modules.Storage.StorageModuleFactory))]

namespace OpenTail.Modules.Storage;

/// <summary>
/// <c>"Type": "storage"</c> in modules.json.
/// <code>
/// { "Type": "storage", "Id": "exports",
///   "Config": { "Provider": "s3", "Bucket": "acme-exports", "Region": "eu-west-1",
///               "Prefixes": [ "finance/monthly/", "crm/exports/" ], "MaxReadBytes": 262144 },
///   "Credentials": { "access-key": { "Env": "EXPORTS_KEY_ID", "Kind": "Secret" },
///                    "secret-key": { "Env": "EXPORTS_SECRET", "Kind": "Secret" } },
///   "Network": { "Hosts": [ "acme-exports.s3.eu-west-1.amazonaws.com" ] } }
/// </code>
/// Without credentials the SDK's ambient identity is used (an IAM role, preferable). <c>ServiceUrl</c> (with <c>ForcePathStyle</c>)
/// points it at an S3-compatible store instead. Give the identity <c>s3:ListBucket</c> and <c>s3:GetObject</c> on the prefixes only.
/// </summary>
public sealed class StorageModuleFactory : IHostModuleFactory
{
    public const string AccessKey = "access-key";
    public const string SecretKey = "secret-key";

    public string Type => "storage";

    public ModuleManifest Describe(string id, JsonElement config)
    {
        var c = config.ValueKind == JsonValueKind.Object ? config.Deserialize<Config>(Json) ?? new Config() : new Config();
        return ManifestFor(id, c);
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The host S3 requests go to: the bucket's virtual-hosted name, the regional endpoint for path style, or the ServiceUrl's.</summary>
    internal static string HostFor(Config c)
    {
        if (Uri.TryCreate(c.ServiceUrl, UriKind.Absolute, out var u))
        {
            var authority = u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort ? u.IdnHost : u.GetLeftPart(UriPartial.Authority);
            return c.ForcePathStyle || string.IsNullOrEmpty(c.Bucket) ? authority : $"{c.Bucket}.{authority}";
        }
        var region = string.IsNullOrWhiteSpace(c.Region) ? "*" : c.Region;
        return c.ForcePathStyle || string.IsNullOrEmpty(c.Bucket) ? $"s3.{region}.amazonaws.com" : $"{c.Bucket}.s3.{region}.amazonaws.com";
    }

    internal static ModuleManifest ManifestFor(string id, Config c) => new()
    {
        Id = id,
        DisplayName = "Object storage",
        Publisher = "OpenTail",
        Description = $"List and read objects in {(string.IsNullOrEmpty(c.Bucket) ? "one bucket" : $"bucket {c.Bucket}")}, inside the configured prefixes. Read only.",
        NetworkHosts = [HostFor(c)],
        Credentials =
        [
            new CredentialRequirement(AccessKey, CredentialKind.Secret, "Access key id; omit both keys to use the ambient identity (an IAM role)") { Optional = true },
            new CredentialRequirement(SecretKey, CredentialKind.Secret, "Secret access key") { Optional = true },
        ],
        Notes =
        [
            "The bucket is fixed by configuration; the model never names one. Keys outside Prefixes are refused before any request.",
            "Object content is outside content: reading it taints the caller.",
            "Reads are capped (MaxReadBytes, and gzip at four times that); archive and cold-storage objects are refused unless ReadColdStorage.",
            "Read only: no write, copy, delete, restore or presign tools. Give the identity s3:ListBucket and s3:GetObject on the prefixes only.",
        ],
        SuggestedPolicyJson = $$"""{ "Tools": { "{{id}}.list": { "Mode": "Execute" }, "{{id}}.read": { "Mode": "Execute" } } }""",
    };

    public ValueTask<IHostModule> CreateAsync(ModuleContext context, CancellationToken ct = default)
    {
        var c = context.Bind<Config>();
        if (!string.Equals(c.Provider, "s3", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Config.Provider '{c.Provider}' is not supported; only \"s3\" (Azure Blob and GCS come later).");
        if (string.IsNullOrWhiteSpace(c.Bucket)) throw new ArgumentException("Config.Bucket is required.");
        if (c.Prefixes is not { Count: > 0 }) throw new ArgumentException("Config.Prefixes is required: the key prefixes the tools may list and read (\"\" allows the whole bucket).");
        if (string.IsNullOrWhiteSpace(c.Region) && string.IsNullOrWhiteSpace(c.ServiceUrl)) throw new ArgumentException("Config.Region (or ServiceUrl) is required.");
        var names = context.Credentials.Names;
        if (names.Contains(AccessKey) != names.Contains(SecretKey))
            throw new ArgumentException($"Map both '{AccessKey}' and '{SecretKey}', or neither (to use the ambient identity).");

        var http = context.Http.Create();
        var credentials = context.Credentials;
        var readerOptions = new ObjectStoreReaderOptions
        {
            Prefixes = c.Prefixes,
            MaxReadBytes = c.MaxReadBytes,
            MaxListItems = c.MaxListItems,
            ReadColdStorage = c.ReadColdStorage,
        };
        IHostModule module = new StorageModule(new StorageModuleOptions
        {
            Id = context.Id,
            Description = c.Description ?? c.Bucket,
            Prefixes = c.Prefixes,
            ContentIsUntrusted = c.ContentIsUntrusted,
            Manifest = ManifestFor(context.Id, c),
            Reader = async token =>
            {
                AWSCredentials? aws = names.Contains(AccessKey)
                    ? new BasicAWSCredentials((await credentials.GetAsync(AccessKey, token)).Secret, (await credentials.GetAsync(SecretKey, token)).Secret)
                    : null;
                var config = new AmazonS3Config { ForcePathStyle = c.ForcePathStyle };
                if (!string.IsNullOrWhiteSpace(c.ServiceUrl)) config.ServiceURL = c.ServiceUrl;
                if (!string.IsNullOrWhiteSpace(c.Region)) config.RegionEndpoint = RegionEndpoint.GetBySystemName(c.Region);
                var s3 = S3ObjectStoreClient.CreateSdkClient(http, config, aws);
                return new ObjectStoreReader(new S3ObjectStoreClient(s3, c.Bucket!), readerOptions);
            },
        });
        return ValueTask.FromResult(module);
    }

    internal sealed class Config
    {
        public string Provider { get; init; } = "s3";
        public string? Bucket { get; init; }
        public string? Region { get; init; }
        public string? ServiceUrl { get; init; }
        public bool ForcePathStyle { get; init; }
        public List<string>? Prefixes { get; init; }
        public string? Description { get; init; }
        public long MaxReadBytes { get; init; } = 256 * 1024;
        public int MaxListItems { get; init; } = 200;
        public bool ReadColdStorage { get; init; }
        public bool ContentIsUntrusted { get; init; } = true;
    }
}
