#!/bin/sh
# With no arguments: start the Host from the files in /config (policy.json and http.json required; modules.json and
# notifications.json used when present). With arguments: run opentail-orca with exactly those, e.g. --new-api-key alice-agent.
set -e
if [ "$#" -gt 0 ]; then exec dotnet /app/opentail-orca.dll "$@"; fi

c="${OPENTAIL_CONFIG:-/config}"
for f in policy.json http.json; do
  if [ ! -f "$c/$f" ]; then
    echo "Missing $c/$f. Mount your configuration folder at $c (see OpenTail.Orca/DEPLOY.md)." >&2
    exit 64
  fi
done
set -- --policy "$c/policy.json" --http "$c/http.json" --db "${OPENTAIL_DB:-/data/opentail-orca.db}"
if [ -f "$c/modules.json" ]; then set -- "$@" --modules "$c/modules.json"; fi
if [ -f "$c/notifications.json" ]; then set -- "$@" --notifications "$c/notifications.json"; fi
exec dotnet /app/opentail-orca.dll "$@"
