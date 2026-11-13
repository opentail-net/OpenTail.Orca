using OpenTail.Orca.Http;
using OpenTail.Modules.Files;
using OpenTail.Modules.OpenApi;

//   FILES_ROOT=C:\data\shared            the only folder the files tools can see
//   OPENAPI_SPEC=C:\specs\crm.json       optional: an OpenAPI document to import as tools
//   OPENAPI_NAMESPACE=crm                tool name prefix (default "api")
//   OPENAPI_TOKEN_VAR=CRM_API_TOKEN      NAME of the environment variable holding the API token (never the token itself)
//   OPENTAIL_ADMIN_TOKENS="rita:<16+ chars>|review,admin,group:finance;jo:<16+ chars>|review"
static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : throw new InvalidOperationException($"Set the environment variable {name}.");

return await HttpDeploymentRunner.RunAsync(args, "opentail-orca-team", builder =>
{
    builder.AddModule(new FilesModule(new FilesModuleOptions { Root = Required("FILES_ROOT") }));
    if (Environment.GetEnvironmentVariable("OPENAPI_SPEC") is { Length: > 0 } spec)
        builder.AddModule(OpenApiModule.Create(File.ReadAllText(spec), new OpenApiModuleOptions
        {
            Namespace = Environment.GetEnvironmentVariable("OPENAPI_NAMESPACE") ?? "api",
            AuthTokenEnvVar = Environment.GetEnvironmentVariable("OPENAPI_TOKEN_VAR"),
        }));
});
