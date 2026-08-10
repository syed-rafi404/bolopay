# syntax=docker/dockerfile:1

# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, as its own layer, so code edits don't re-download packages.
COPY src/BoloPay.Web/BoloPay.Web.csproj src/BoloPay.Web/
RUN dotnet restore src/BoloPay.Web/BoloPay.Web.csproj

COPY src/ src/

# wwwroot/css/app.css is committed, so Tailwind does not run here. The MSBuild
# target is Windows-only (tools/tailwindcss.exe) and skips itself when absent.
# Regenerate locally and commit if Styles/app.css changes — see README.
RUN dotnet publish src/BoloPay.Web/BoloPay.Web.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# ---------------------------------------------------------------------------
# Runtime
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# The .NET runtime images already ship a non-root user named "app". Reusing it
# avoids depending on adduser flags, which differ between Debian and BusyBox
# base images. --chown matters: chowning /app before COPY does nothing, because
# COPY would then write root-owned files into it.
COPY --from=build --chown=app:app /app/publish .

USER app

# One ENV per line. A comment inside a line continuation is not reliably
# parsed and silently corrupts the instruction.
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_RUNNING_IN_CONTAINER=true

# Data Protection has no persistent volume here, so it would log a noisy
# warning on every start. Keys only guard antiforgery tokens for a stateless
# demo, so an ephemeral in-memory keyring is correct.
ENV DOTNET_EnableDiagnostics=0

EXPOSE 8080

# Hosts inject PORT. Fall back to 8080 for plain `docker run`.
ENTRYPOINT ["/bin/sh", "-c", "ASPNETCORE_URLS=http://+:${PORT:-8080} exec dotnet BoloPay.Web.dll"]
