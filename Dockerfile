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

# Run as a non-root user. The endpoint is public and unauthenticated, so there
# is no reason for the process to have more rights than serving needs.
RUN adduser --disabled-password --gecos "" --uid 5678 appuser \
    && chown -R appuser /app
USER appuser

COPY --from=build /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    # Trim startup work; this app has no need for diagnostics pipes.
    DOTNET_EnableDiagnostics=0

EXPOSE 8080

# Render injects PORT. Fall back to 8080 for plain `docker run`.
ENTRYPOINT ["/bin/sh", "-c", "ASPNETCORE_URLS=http://+:${PORT:-8080} exec dotnet BoloPay.Web.dll"]
