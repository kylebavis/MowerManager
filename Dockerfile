FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/MowerManager/MowerManager.csproj src/MowerManager/
RUN dotnet restore src/MowerManager/MowerManager.csproj
COPY src/ src/
RUN dotnet publish src/MowerManager/MowerManager.csproj -c Release -o /app --no-restore

FROM build AS test
COPY tests/ tests/
RUN dotnet test tests/MowerManager.Tests/MowerManager.Tests.csproj

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --only-upgrade --no-install-recommends ca-certificates \
    && rm -rf /var/lib/apt/lists/*
RUN mkdir /data && chown $APP_UID /data
COPY --from=build /app .
ENV ConnectionStrings__Sqlite="Data Source=/data/mower.db" \
    DataProtection__KeysPath=/data/keys
VOLUME /data
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "MowerManager.dll"]
