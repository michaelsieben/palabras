FROM node:24-bookworm-slim AS frontend
WORKDIR /src/Frontend
COPY Frontend/package*.json ./
RUN npm ci
COPY Frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /src
# Build identity: the commit is supplied by the caller (CI, and the compose build args on the
# deploy host) and stamped into the assembly, so /healthz and Ajustes can name the build. Version
# and milestone are single-sourced in Backend/Palabras/Palabras.csproj. No arg means "unknown",
# never a stale or invented sha.
ARG COMMIT_SHA=unknown
COPY Backend/Palabras/Palabras.csproj Backend/Palabras/
RUN dotnet restore Backend/Palabras/Palabras.csproj
COPY Backend/Palabras/ Backend/Palabras/
COPY content/schema/ content/schema/
RUN dotnet publish Backend/Palabras/Palabras.csproj -c Release --no-restore -p:UseAppHost=true -p:CommitSha="${COMMIT_SHA}" -o /app/publish
COPY --from=frontend /src/Frontend/dist/ /app/publish/wwwroot/

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
# Re-declared so the image itself carries the revision: `docker inspect` then answers "which
# build is this untagged image?" even before the container runs.
ARG COMMIT_SHA=unknown
LABEL org.opencontainers.image.revision="${COMMIT_SHA}"
USER root
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data && chown app:app /data
WORKDIR /app
COPY --from=backend --chown=app:app /app/publish/ ./
ENV ASPNETCORE_URLS=http://+:18480
ENV DATA_DIR=/data
USER app
EXPOSE 18480
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl --fail --silent --show-error http://localhost:18480/healthz || exit 1
ENTRYPOINT ["dotnet", "Palabras.dll"]
