using System.Text.Json;

[assembly: HostContractVersion(HostContract.Version)]
[assembly: HostModuleFactory(typeof(OpenTail.Host.Tests.Worker.ProbeBrickFactory))]

return await OpenTail.Host.ModuleWorker.TryRunAsync(args) ?? 64;

namespace OpenTail.Host.Tests.Worker
{
    /// <summary><c>"Type": "isolation-probe"</c>: tools that report where they run and misbehave on request.</summary>
    public sealed class ProbeBrickFactory : IHostModuleFactory
    {
        public string Type => "isolation-probe";

        public ModuleManifest Describe(string id, JsonElement config) => new()
        {
            Id = id,
            DisplayName = "Isolation probe",
            Credentials =
            [
                new CredentialRequirement("api", CredentialKind.Bearer, "token for the test API") { Optional = true },
                new CredentialRequirement("db", CredentialKind.Secret, "a password") { Optional = true },
            ],
        };

        public ValueTask<IHostModule> CreateAsync(ModuleContext context, CancellationToken ct = default) =>
            ValueTask.FromResult<IHostModule>(new ProbeBrick(context, Describe(context.Id, context.Config)));
    }

    public sealed class ProbeBrick(ModuleContext ctx, ModuleManifest manifest) : IHostModule
    {
        public string Id => ctx.Id;
        public ModuleManifest Manifest => manifest;

        public void Register(IToolRegistry r)
        {
            void Add(string tool, ToolRisk risk, Delegate fn, bool untrusted = false, IPreviewableTool? preview = null) =>
                r.Add(new ToolDescriptor($"{Id}.{tool}", tool, risk, Id) { ReadsUntrusted = untrusted }, AIFunctionFactory.Create(fn, $"{Id}.{tool}", tool), preview);

            Add("whoami", ToolRisk.Read, () => new { pid = Environment.ProcessId, module = Environment.GetEnvironmentVariable("OPENTAIL_MODULE_ID") });
            Add("env", ToolRisk.Read, (string name) => Environment.GetEnvironmentVariable(name) ?? "(not set)");
            Add("fetch", ToolRisk.Read, async (string url, CancellationToken ct) =>
            {
                using var http = ctx.Http.Create("api");
                return await http.GetStringAsync(url, ct);
            }, untrusted: true);
            Add("secret", ToolRisk.Read, async (string name, CancellationToken ct) => (await ctx.Credentials.GetAsync(name, ct)).Secret.Length);
            Add("hang", ToolRisk.Read, () => { Thread.Sleep(Timeout.Infinite); return "never"; });
            Add("crash", ToolRisk.Read, () => { Environment.Exit(3); return "never"; });
            Add("noisy", ToolRisk.Read, () => { Console.WriteLine("garbage on stdout {not json"); Console.Error.WriteLine("a line on stderr"); return "still fine"; });
            Add("big", ToolRisk.Read, (int bytes) => new string('x', bytes));
            Add("fail", ToolRisk.Read, string (string kind) => kind switch
            {
                "transient" => throw new ToolTransientException("try later", TimeSpan.FromSeconds(7)),
                "argument" => throw new ArgumentException("bad argument"),
                _ => throw new InvalidOperationException("broken"),
            });
            Add("send", ToolRisk.ExternalEffect, (string to) => $"sent to {to}", preview: new SendPreview());
        }

        private sealed class SendPreview : IPreviewableTool
        {
            public Task<PreviewResult> PreviewAsync(string argumentsJson, CancellationToken ct = default)
            {
                var to = JsonDocument.Parse(argumentsJson).RootElement.GetProperty("to").GetString() ?? "";
                if (!to.EndsWith("@corp.example", StringComparison.Ordinal)) throw new ArgumentException("Only corp.example recipients.");
                return Task.FromResult(new PreviewResult($"Send to {to} (from process {Environment.ProcessId})"));
            }
        }
    }
}
