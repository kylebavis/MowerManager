FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e AS build
WORKDIR /src
COPY src/MowerManager/MowerManager.csproj src/MowerManager/
RUN dotnet restore src/MowerManager/MowerManager.csproj
COPY src/ src/
RUN dotnet publish src/MowerManager/MowerManager.csproj -c Release -o /app --no-restore

FROM build AS test
COPY global.json ./
COPY tests/ tests/
RUN dotnet test --project tests/MowerManager.Tests/MowerManager.Tests.csproj

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine@sha256:486297ae006e95ee2ee3112a2c03e3d26e41fbbc64b7a29e28e7a18cc36b12bb AS final
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
