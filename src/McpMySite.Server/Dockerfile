FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/McpMySite.Core/McpMySite.Core.csproj src/McpMySite.Core/
COPY src/McpMySite.Server/McpMySite.Server.csproj src/McpMySite.Server/
RUN dotnet restore src/McpMySite.Server/McpMySite.Server.csproj
COPY src/McpMySite.Core/ src/McpMySite.Core/
COPY src/McpMySite.Server/ src/McpMySite.Server/
RUN dotnet publish src/McpMySite.Server/McpMySite.Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "McpMySite.Server.dll"]
