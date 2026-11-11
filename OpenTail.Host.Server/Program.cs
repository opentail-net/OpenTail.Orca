using OpenTail.Host.Http;
using OpenTail.Modules.Skills;

// Every module package is referenced, so modules.json can enable any of them; none is enabled by being here.
//   opentail-host --policy policy.json --http http.json [--modules modules.json] [--notifications notifications.json] [--db file]
//   opentail-host --check-skill-store <store folder>   lists the skill store's problems; serves and writes nothing
// In the container, docker-entrypoint.sh passes the files it finds in /config. See OpenTail.Host/DEPLOY.md.
if (SkillStoreCli.TryRun(args, Console.Out) is { } storeExit) return storeExit;
return await HttpDeploymentRunner.RunAsync(args, "opentail-host");
