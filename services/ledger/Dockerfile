FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# Restore numa camada separada, que fica em cache até algum .csproj mudar.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/PayFlow.Ledger.Domain/*.csproj src/PayFlow.Ledger.Domain/
COPY src/PayFlow.Ledger.Application/*.csproj src/PayFlow.Ledger.Application/
COPY src/PayFlow.Ledger.Infrastructure/*.csproj src/PayFlow.Ledger.Infrastructure/
COPY src/PayFlow.Ledger.Api/*.csproj src/PayFlow.Ledger.Api/
RUN dotnet restore src/PayFlow.Ledger.Api/PayFlow.Ledger.Api.csproj

COPY src/ src/
RUN dotnet publish src/PayFlow.Ledger.Api/PayFlow.Ledger.Api.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "PayFlow.Ledger.Api.dll"]
