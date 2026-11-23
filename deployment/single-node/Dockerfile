FROM mcr.microsoft.com/dotnet/sdk:10.0.300@sha256:c0790639332692a0d56cdd81ed581cfd24d040d9839764c138994866df89a3b6 AS build
WORKDIR /src
COPY artifacts/candidates/4c5bbe9/packages /packages
COPY deployment/single-node/host/Audorix.ObjectStorage.DeploymentHost.csproj deployment/single-node/host/
RUN printf '%s\n' '<?xml version="1.0" encoding="utf-8"?><configuration><packageSources><clear/><add key="candidate" value="/packages"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>' > deployment/single-node/host/NuGet.Config \
 && dotnet restore deployment/single-node/host/Audorix.ObjectStorage.DeploymentHost.csproj --configfile deployment/single-node/host/NuGet.Config
COPY deployment/single-node/host/Program.cs deployment/single-node/host/
RUN dotnet publish deployment/single-node/host/Audorix.ObjectStorage.DeploymentHost.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12@sha256:2d584d8147faddb0d678c5748d47953e5b8e18621ed4fb7049a91381d9d7746f
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates \
 && install -d /usr/share/postgresql-common/pgdg \
 && curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc \
 && echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://apt.postgresql.org/pub/repos/apt noble-pgdg main" > /etc/apt/sources.list.d/pgdg.list \
 && apt-get update && apt-get install -y --no-install-recommends postgresql-client-18=18.6-1.pgdg24.04+2 libpq5=18.6-1.pgdg24.04+2 \
 && rm -rf /var/lib/apt/lists/* \
 && groupadd --gid 10001 audorix && useradd --uid 10001 --gid 10001 --no-create-home audorix \
 && mkdir -p /app /var/lib/audorix /var/lib/audorix-dp /var/backups/audorix \
 && chown -R 10001:10001 /app /var/lib/audorix /var/lib/audorix-dp /var/backups/audorix
WORKDIR /app
COPY --from=build --chown=10001:10001 /out .
USER 10001:10001
ENV ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS=http://+:8080 DOTNET_EnableDiagnostics=0
EXPOSE 8080
ENTRYPOINT ["dotnet", "Audorix.ObjectStorage.DeploymentHost.dll"]
