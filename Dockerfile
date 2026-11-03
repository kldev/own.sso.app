# The one host of Great SSO: the REST API, the Blazor panel, OIDC and MCP (ADR 0002). No Node: wwwroot/css/app.css is
# committed.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG CONFIGURATION=Release
# The Husky target must not run in the image (no .git, no tools).
ENV HUSKY=0
WORKDIR /src

# 1) Only what restore needs - cached until dependencies change. Keep the list the closure of Sso.Api's references.
COPY global.json Directory.Build.props Directory.Packages.props nuget.config ./
COPY apps/Sso.Api/Sso.Api.csproj apps/Sso.Api/
COPY apps/Sso.ApiClient/Sso.ApiClient.csproj apps/Sso.ApiClient/
COPY libs/Sso.Messaging.Contracts/Sso.Messaging.Contracts.csproj libs/Sso.Messaging.Contracts/
COPY libs/Sso.Observability/Sso.Observability.csproj libs/Sso.Observability/
COPY libs/Sso.Observability.AspNetCore/Sso.Observability.AspNetCore.csproj libs/Sso.Observability.AspNetCore/
COPY libs/Sso.Data/Sso.Data.csproj libs/Sso.Data/
RUN dotnet restore apps/Sso.Api/Sso.Api.csproj

# 2) Sources and publish.
COPY libs/ libs/
COPY apps/ apps/
RUN dotnet publish apps/Sso.Api/Sso.Api.csproj \
    --configuration $CONFIGURATION \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false \
    /p:SkipTailwind=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# The sessions' keys (DataProtection__KeysPath): a named volume takes this owner on its first use.
RUN mkdir /keys && chown $APP_UID /keys
WORKDIR /app
COPY --from=build --chown=$APP_UID /app/publish .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

USER $APP_UID

# No curl in the aspnet image: the app checks its own /health (ContainerHealthCheck).
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD ["dotnet", "Sso.Api.dll", "healthcheck"]

ENTRYPOINT ["dotnet", "Sso.Api.dll"]
