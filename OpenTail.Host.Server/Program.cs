using OpenTail.Host.Http;
using OpenTail.Modules.Skills;

// Every module package is referenced, so modules.json can enable any of them; none is enabled by being here.
//   opentail-host --policy policy.json --http http.json [--modules modules.json] [--notifications notifications.json] [--db file]
//   opentail-host --check-skill-store <store folder>   lists the skill store's problems; serves and writes nothing
// In the container, docker-entrypoint.sh passes the files it finds in /config. See OpenTail.Host/DEPLOY.md.
if (SkillStoreCli.TryRun(args, Console.Out) is { } storeExit) return storeExit;
// The A2A front door is served only when the HTTP config enables A2A (its module and endpoint are added then, and not otherwise).
return await HttpDeploymentRunner.RunAsync(args, "opentail-host", _ => { }, extensions: [new OpenTail.Host.A2A.A2AFrontDoor()]);
