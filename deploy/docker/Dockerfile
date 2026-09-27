# Multi-stage build, parameterized by PROJECT so one Dockerfile builds every TomYum
# host/service. Pass --build-arg PROJECT=src/TomYum.Server/TomYum.Server.csproj (etc.).
# Must match the SDK pinned in global.json (net10.0 in Directory.Build.props).
ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG PROJECT
WORKDIR /src

# Copy solution-wide build config first so restore layers cache well.
COPY global.json Directory.Build.props ./
COPY src/ src/
COPY services/ services/

RUN dotnet restore "$PROJECT"
RUN dotnet publish "$PROJECT" -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
WORKDIR /app
COPY --from=build /app ./
# data/ (replays, .osu cache, local storage) must be writable by the non-root user; /app is root's.
RUN mkdir -p /app/data && chown "$APP_UID" /app/data
# Run as the non-root user shipped in the base image.
USER $APP_UID
# The published entry assembly name varies by project; the publish output sets it.
# Each service overrides ENTRYPOINT via compose/k8s if needed.
ENTRYPOINT ["dotnet", "TomYum.Server.dll"]
