# syntax=docker/dockerfile:1
# The OpenTail Host as a container. Build from the repository root, with a token that can read the Connectivity packages
# (GitHub Packages, read:packages) in GITHUB_PACKAGES_TOKEN; it is a build secret, so it never lands in an image layer:
#   docker build -f OpenTail.Orca.Server/Dockerfile --secret id=github_packages_token,env=GITHUB_PACKAGES_TOKEN -t opentail-orca .
# Run it with a configuration folder at /config and a volume at /data; see OpenTail.Orca/DEPLOY.md.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG VERSION=0.0.0-local
WORKDIR /src
COPY . .
# No .git in the build context, so the version comes from VERSION instead of the host-v* tags. nuget.config reads the token from
# GITHUB_PACKAGES_TOKEN, which the secret mount provides for this one step.
RUN --mount=type=secret,id=github_packages_token,env=GITHUB_PACKAGES_TOKEN \
    dotnet publish OpenTail.Orca.Server/OpenTail.Orca.Server.csproj -c Release -o /app \
      -p:MinVerSkip=true -p:Version=$VERSION --nologo \
 && sed 's/\r$//' OpenTail.Orca.Server/docker-entrypoint.sh > /app/docker-entrypoint.sh \
 && chmod 755 /app/docker-entrypoint.sh

FROM mcr.microsoft.com/dotnet/aspnet:11.0
WORKDIR /app
COPY --from=build /app .
# The database (actions, audit, notification outbox, Teams conversations) lives on /data; configuration is mounted read-only at /config.
RUN mkdir -p /data /config && chown app:app /data
USER app
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["/app/docker-entrypoint.sh"]
