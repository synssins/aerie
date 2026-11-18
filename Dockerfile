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

FROM mcr.microsoft.com/dotnet/sdk:11.0@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea AS build
WORKDIR /src
COPY . .
COPY --from=client /src/src/Kb.Web/wwwroot src/Kb.Web/wwwroot
# global.json pins the SDK used on Windows; the image's own SDK 10 is used here.
RUN rm -f global.json \
 && dotnet publish src/Kb.Web -c Release -o /out \
 && rm -f /out/appsettings.Development.json

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:bfd912c83295ce66d4b706ebb8541b9f14259b0e76fbb88b2751f58ce2399d2e
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
