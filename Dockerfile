FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS build
WORKDIR /src
COPY src/MowerManager/MowerManager.csproj src/MowerManager/
RUN dotnet restore src/MowerManager/MowerManager.csproj
COPY src/ src/
RUN dotnet publish src/MowerManager/MowerManager.csproj -c Release -o /app --no-restore

FROM build AS test
COPY tests/ tests/
RUN dotnet test tests/MowerManager.Tests/MowerManager.Tests.csproj

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac AS final
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
