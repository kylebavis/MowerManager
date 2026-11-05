FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
WORKDIR /src
COPY src/MowerManager/MowerManager.csproj src/MowerManager/
RUN dotnet restore src/MowerManager/MowerManager.csproj
COPY src/ src/
RUN dotnet publish src/MowerManager/MowerManager.csproj -c Release -o /app --no-restore

FROM build AS test
COPY global.json ./
COPY tests/ tests/
RUN dotnet test --project tests/MowerManager.Tests/MowerManager.Tests.csproj

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine@sha256:254fe017495996d3abcc487ffacdcce2b73affb380d3af87af12c8dff41fe0b0 AS final
WORKDIR /app
RUN apk upgrade --no-cache ca-certificates
RUN mkdir /data && chown $APP_UID /data
COPY --from=build /app .
ENV ConnectionStrings__Sqlite="Data Source=/data/mower.db" \
    DataProtection__KeysPath=/data/keys
VOLUME /data
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "MowerManager.dll"]
