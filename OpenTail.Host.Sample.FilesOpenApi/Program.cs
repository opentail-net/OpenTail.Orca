using OpenTail.Host.Admin;
using OpenTail.Modules.Files;
using OpenTail.Modules.OpenApi;

//   FILES_ROOT=C:\data\shared            the only folder the files tools can see
//   OPENAPI_SPEC=C:\specs\crm.json       the OpenAPI document to import
//   OPENAPI_NAMESPACE=crm                tool name prefix (default "api")
//   OPENAPI_SERVER=https://api.crm.test  optional override of the server in the document
//   OPENAPI_TOKEN_VAR=CRM_API_TOKEN      NAME of the environment variable holding the API token (the token itself never appears here)
static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : throw new InvalidOperationException($"Set the environment variable {name}.");

return await DeploymentRunner.RunAsync(args, "opentail-host-files-api", builder =>
{
    builder.AddModule(new FilesModule(new FilesModuleOptions { Root = Required("FILES_ROOT") }));
    builder.AddModule(OpenApiModule.Create(File.ReadAllText(Required("OPENAPI_SPEC")), new OpenApiModuleOptions
    {
        Namespace = Environment.GetEnvironmentVariable("OPENAPI_NAMESPACE") ?? "api",
        ServerUrlOverride = Environment.GetEnvironmentVariable("OPENAPI_SERVER"),
        AuthTokenEnvVar = Environment.GetEnvironmentVariable("OPENAPI_TOKEN_VAR"),
    }));
});
