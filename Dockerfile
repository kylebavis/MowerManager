FROM mcr.microsoft.com/dotnet/sdk:11.0@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea AS build
WORKDIR /src
COPY src/MowerManager/MowerManager.csproj src/MowerManager/
RUN dotnet restore src/MowerManager/MowerManager.csproj
COPY src/ src/
RUN dotnet publish src/MowerManager/MowerManager.csproj -c Release -o /app --no-restore

FROM build AS test
COPY global.json ./
COPY tests/ tests/
RUN dotnet test --project tests/MowerManager.Tests/MowerManager.Tests.csproj

FROM mcr.microsoft.com/dotnet/aspnet:11.0-alpine@sha256:d39b72ad1534fa0faf52b488bffb0762df7ba269d120f0338713d4dda2a41bf5 AS final
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
