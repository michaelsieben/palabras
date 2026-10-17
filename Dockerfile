FROM node:24-bookworm-slim AS frontend
WORKDIR /src/Frontend
COPY Frontend/package*.json ./
RUN npm ci
COPY Frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /src
COPY Backend/Palabras/Palabras.csproj Backend/Palabras/
RUN dotnet restore Backend/Palabras/Palabras.csproj
COPY Backend/Palabras/ Backend/Palabras/
RUN dotnet publish Backend/Palabras/Palabras.csproj -c Release --no-restore -o /app/publish
COPY --from=frontend /src/Frontend/dist/ /app/publish/wwwroot/

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
USER root
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data && chown app:app /data
WORKDIR /app
COPY --from=backend --chown=app:app /app/publish/ ./
ENV ASPNETCORE_URLS=http://+:8080
USER app
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl --fail --silent --show-error http://localhost:8080/healthz || exit 1
ENTRYPOINT ["dotnet", "Palabras.dll"]
