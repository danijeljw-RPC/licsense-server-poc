# syntax=docker/dockerfile:1.7
# Keep the container SDK aligned with the exact version required by global.json.
FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS restore
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json NuGet.Config SoftwareLicensing.slnx ./
COPY src/Licensing.Core/Licensing.Core.csproj src/Licensing.Core/packages.lock.json src/Licensing.Core/
COPY src/LicenseServer/LicenseServer.csproj src/LicenseServer/packages.lock.json src/LicenseServer/
RUN dotnet restore src/LicenseServer/LicenseServer.csproj --configfile NuGet.Config

FROM restore AS build
COPY src/Licensing.Core/ src/Licensing.Core/
COPY src/LicenseServer/ src/LicenseServer/
RUN dotnet publish src/LicenseServer/LicenseServer.csproj -c Release --no-restore -o /out /p:UseAppHost=false /p:AnalysisMode=Recommended

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
USER root
RUN apt-get update \
    && apt-get install --no-install-recommends -y curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /var/lib/licenseserver/data-protection \
    && chown -R "$APP_UID:$APP_UID" /var/lib/licenseserver
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /out/ ./
COPY --chown=$APP_UID:$APP_UID keys/license-primary-2026-public.pem /app/keys/license-primary-2026-public.pem
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Container \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
STOPSIGNAL SIGTERM
ENTRYPOINT ["dotnet", "LicenseServer.dll"]
