using OpenTail.Host.Http;

// Every module package is referenced, so modules.json can enable any of them; none is enabled by being here.
//   opentail-host --policy policy.json --http http.json [--modules modules.json] [--notifications notifications.json] [--db file]
// In the container, docker-entrypoint.sh passes the files it finds in /config. See OpenTail.Host/DEPLOY.md.
return await HttpDeploymentRunner.RunAsync(args, "opentail-host");
