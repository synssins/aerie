# Aerie on Linux. Build:  docker build -t aerie .
# Data (the SQLite database, uploaded files, logs) is in /app/App_Data: keep it on a volume.

# Base images are named by digest, so a build uses the image that was looked at, not whatever the tag points to that day.
# Dependabot (.github/dependabot.yml) proposes the new digest when a base image is updated.
FROM node:22-bookworm-slim@sha256:e9f565d32859f0cbfb52125eafe8be39390f24dac302dc3cdf566e8ba3ab21ee AS client
WORKDIR /src/client
COPY client/package.json client/package-lock.json ./
RUN npm ci --no-fund --no-audit
COPY client/ ./
# vite writes into ../src/Kb.Web/wwwroot
RUN mkdir -p ../src/Kb.Web && npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
WORKDIR /src
COPY . .
COPY --from=client /src/src/Kb.Web/wwwroot src/Kb.Web/wwwroot
# global.json pins the SDK used on Windows; the image's own SDK 10 is used here.
RUN rm -f global.json \
 && dotnet publish src/Kb.Web -c Release -o /out \
 && rm -f /out/appsettings.Development.json

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:12b1135f5f8b3c0a214f2a680ff2033dff4f684f445d6c697d9524ceeb303586
WORKDIR /app
COPY --from=build /out .
RUN mkdir -p /app/App_Data && chown app:app /app/App_Data
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    Kb__Database__Provider=Sqlite \
    Kb__Database__ConnectionString="Data Source=/app/App_Data/kb.db"
VOLUME /app/App_Data
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Kb.Web.dll"]
