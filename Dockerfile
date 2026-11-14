# Aerie on Linux. Build:  docker build -t aerie .
# Data (the SQLite database, uploaded files, logs) is in /app/App_Data: keep it on a volume.

FROM node:22-bookworm-slim AS client
WORKDIR /src/client
COPY client/package.json client/package-lock.json ./
RUN npm ci --no-fund --no-audit
COPY client/ ./
# vite writes into ../src/Kb.Web/wwwroot
RUN mkdir -p ../src/Kb.Web && npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
COPY --from=client /src/src/Kb.Web/wwwroot src/Kb.Web/wwwroot
# global.json pins the SDK used on Windows; the image's own SDK 10 is used here.
RUN rm -f global.json \
 && dotnet publish src/Kb.Web -c Release -o /out \
 && rm -f /out/appsettings.Development.json

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# Liberation fonts stand in for Arial and Courier New in PDF exports.
RUN apt-get update && apt-get install -y --no-install-recommends fonts-liberation && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out .
RUN mkdir -p /app/App_Data && chown app:app /app/App_Data
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
    Kb__Database__Provider=Sqlite \
    Kb__Database__ConnectionString="Data Source=/app/App_Data/kb.db"
VOLUME /app/App_Data
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Kb.Web.dll"]
