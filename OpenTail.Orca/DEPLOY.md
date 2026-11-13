# Running the Host where people and Teams can reach it

The Teams bot, company sign-in on the review page and links in notifications all need the Host at a **public HTTPS address**. Choose by
stage:

| | A. Trial through a tunnel | B. One server: Docker and Caddy | C. Your own platform |
|---|---|---|---|
| For | A demo, a first live Teams run | A pilot, a small team in production | Where IT already runs containers |
| Runs on | Your machine | Any Linux VM (Azure, another cloud, on premises) | Kubernetes, a VM fleet, ... |
| HTTPS from | Microsoft Dev Tunnels | Caddy (Let's Encrypt, renewed automatically) | Your ingress or load balancer |
| Address | `https://<id>-5600.<region>.devtunnels.ms` | `https://approvals.contoso.com` | yours |
| Effort | 10 minutes | about an hour, with DNS | depends |

Whatever you choose, it runs **one instance**: the state is one SQLite file (actions, audit trail, notification outbox, Teams
conversations). Run no second copy against the same file, and keep the file on local or block storage, never on a network file
share (see C).

## The container image

`OpenTail.Orca.Server` is the ready-to-run Host (program `opentail-orca`). It references **every module and profile package**, so
`modules.json` can enable any of them; being compiled in enables none.

Released images are in the private registry `ghcr.io/opentail-net/opentail-orca:<version>`. To pull one, sign in once with a token
that has `read:packages`: `docker login ghcr.io -u <github user>`. To build an image yourself from the repository root, with
`GITHUB_PACKAGES_TOKEN` set to such a token (it is passed as a build secret and never stored in the image):

```
docker build -f OpenTail.Orca.Server/Dockerfile --secret id=github_packages_token,env=GITHUB_PACKAGES_TOKEN \
    -t opentail-orca --build-arg VERSION=0.1.0 .
```

| Path | What |
|---|---|
| `/config` | Your configuration folder, mounted read-only. `policy.json` and `http.json` are required; `modules.json` and `notifications.json` are used when present. Starting points: [`OpenTail.Orca.Server/config`](../OpenTail.Orca.Server/config). |
| `/data` | A volume for the database `opentail-orca.db`. Back this up. |
| port 8080 | Plain http for the proxy in front of it. |
| environment | Secrets: reviewer tokens (`OPENTAIL_ADMIN_TOKENS`), the Teams bot secret, module credentials. Config files name the variables, never the values. One setting that is not a secret: `OPENTAIL_DB_SYNCHRONOUS` ([durability](OPERATIONS.md#surviving-a-power-cut)). |

The container runs as the unprivileged `app` user. With no arguments it starts the Host from `/config`; with arguments it runs exactly
those, which is how you run the Host's commands:

```
docker run --rm opentail-orca --new-api-key alice-agent                       # a key for an AI agent; paste the hash into http.json
docker run --rm -v /opt/opentail/config:/config:ro opentail-orca \
    --policy /config/policy.json --modules /config/modules.json --list-modules # what each module offers and what the policy does with it
docker run --rm -v /opt/opentail/config:/config opentail-orca \
    --policy /config/policy.json --modules /config/modules.json --adopt-policy github   # writes config/policy.d/github.json (rw mount)
```

The Host refuses to start rather than start open. It needs sign-in (API keys or `Jwt`) and a `PublicBaseUrl`, it rejects plain http
beyond loopback unless `TrustProxy` is set, and it stops if a module's credential variable is missing. The reason is in the log.

## A. Trial through a tunnel

Microsoft Dev Tunnels gives your machine a stable HTTPS address that Microsoft's Bot Framework and your reviewers' browsers can reach.
Teams Toolkit uses the same tool.

1. Install it and sign in once: `winget install Microsoft.devtunnel`, then `devtunnel user login`.
2. Create a **named** tunnel, so the address stays the same across restarts. The Azure Bot's messaging endpoint and the Teams app
   package both contain it.
   ```
   devtunnel create opentail --allow-anonymous
   devtunnel port create opentail -p 5600
   devtunnel host opentail
   ```
   It prints the address, e.g. `https://k8x2p1-5600.euw.devtunnels.ms`. Anonymous access is needed because Microsoft's Bot Framework
   cannot sign in to a tunnel. The Host's own sign-in protects everything behind it. `devtunnel show opentail` shows when the tunnel
   expires.
3. Run the Host on loopback, with the tunnel's address as `PublicBaseUrl` and sign-in configured. The Host refuses to start open when
   `PublicBaseUrl` is not a loopback address.
   ```json
   {
     "Urls": [ "http://127.0.0.1:5600" ],
     "PublicBaseUrl": "https://k8x2p1-5600.euw.devtunnels.ms",
     "ApiKeys": [ { "Name": "alice-agent", "Hash": "sha256:..." } ]
   }
   ```
   ```
   opentail-orca --policy policy.json --http http.json --modules modules.json --notifications notifications.json
   ```
4. Check `https://<tunnel address>/healthz`. Then follow [TEAMS-SETUP.md](TEAMS-SETUP.md) with the tunnel address as the public address.

What to expect: the first time a browser opens the address, Dev Tunnels shows a warning page; press Continue once. The Bot Framework's
requests are not a browser's and are not affected. The Host is only reachable while `devtunnel host` and the Host both run. Stop the tunnel when the trial is over.
Cloudflare's quick tunnels (`cloudflared tunnel --url http://127.0.0.1:5600`) also work for a look at the review page, but their
address changes on every start, which does not suit Teams.

## B. One server: Docker Compose and Caddy

What you need: a Linux VM (2 GB of memory is plenty), a DNS name for it such as `approvals.contoso.com`, ports 80 and 443 open to the
internet, and Docker with the compose plugin.

1. On the server, `docker login ghcr.io` with a token that has `read:packages`, then pull a released image and name it as the compose
   file expects: `docker pull ghcr.io/opentail-net/opentail-orca:0.1.0`, then set `OPENTAIL_IMAGE=ghcr.io/opentail-net/opentail-orca:0.1.0`
   in `compose/.env`. (Or build it yourself, as above.)
2. Copy the two folders `OpenTail.Orca.Server/config` and `OpenTail.Orca.Server/compose` to, for example, `/opt/opentail/config` and
   `/opt/opentail/compose`. They must stay side by side, because the compose file mounts `../config`.
3. In `config/http.json`, set `PublicBaseUrl` to `https://approvals.contoso.com` and add an API key for each agent
   (`docker run --rm opentail-orca --new-api-key <name>`). Add `Jwt` and `Teams` when you get to them.
4. In `compose/`:
   * `cp host.env.example host.env` and fill in the secrets that your configuration names;
   * create `.env` with `OPENTAIL_DOMAIN=approvals.contoso.com`, the same host as `PublicBaseUrl`.
5. `docker compose up -d`, then `curl https://approvals.contoso.com/healthz`. Caddy fetches the certificate on the first request; that
   can take a few seconds.

Port 8080 is not published. Only Caddy reaches the Host, and that is what `TrustProxy` relies on: with it set, the Host believes
`X-Forwarded-*` headers from anyone who can reach port 8080.

| Task | Command (in `compose/`) |
|---|---|
| Logs | `docker compose logs -f host` (the startup lists every module, its tools and warnings) |
| Change configuration | Edit `config/`, then `docker compose restart host` |
| Upgrade | Pull the new version and set it in `OPENTAIL_IMAGE` (or rebuild), then `docker compose up -d`. The Host migrates its database on start and keeps a backup copy of the file next to it. |
| Back up | `docker compose stop host`, then `docker run --rm -v opentail_host-data:/data -v "$PWD":/backup alpine cp -a /data/. /backup/opentail-data/`, then `docker compose start host`. See [OPERATIONS.md](OPERATIONS.md#what-to-back-up-and-how-to-upgrade). |

## C. Your own platform

* **Kubernetes:** one replica (`replicas: 1`, strategy `Recreate`), a `ReadWriteOnce` volume on block storage at `/data`, the config
  folder from a ConfigMap at `/config`, secrets from a Secret as environment variables, and HTTP probes on `/healthz`, which answers
  whatever Host header the probe sends: 503 when the maintenance sweep has stopped (restart it), 200 when healthy or merely degraded. Put the ingress in front with TLS, and set `TrustProxy`. Allow no other way into the pod,
  for example with a NetworkPolicy.
* **A VM without containers:** `dotnet publish OpenTail.Orca.Server -c Release -o /opt/opentail/app`, run it as a service (systemd) with
  the same arguments, and use any reverse proxy for TLS.
* **Azure Container Apps, App Service, or anything else whose only persistent storage is a network file share (Azure Files, SMB,
  NFS):** not recommended yet. SQLite's file locking is unreliable on network shares and can corrupt the database. An Azure VM with
  option B is the Azure route for now.

## Before people use it

* `https://<PublicBaseUrl>/healthz` answers from outside, and `/mcp` answers 401 without a key.
* Port 8080 (or the loopback port) cannot be reached except through the proxy or tunnel.
* Secrets are only in the environment (`host.env`, your platform's secret store), not in `config/`.
* `/data` is backed up.
* If losing the last few decisions in a power cut would matter, `OPENTAIL_DB_SYNCHRONOUS=full` is set
  ([OPERATIONS.md](OPERATIONS.md#surviving-a-power-cut)).
* Then: Teams ([TEAMS-SETUP.md](TEAMS-SETUP.md)), company sign-in and notifications ([OPERATIONS.md](OPERATIONS.md)).
