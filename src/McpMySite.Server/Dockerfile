# The build stage always runs on the builder's own architecture and the SDK cross-compiles for
# TARGETARCH, because building .NET under QEMU emulation is slow and unreliable. Only the runtime
# stage is pulled for the target platform.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY Directory.Build.props ./
COPY src/McpMySite.Core/McpMySite.Core.csproj src/McpMySite.Core/
COPY src/McpMySite.Server/McpMySite.Server.csproj src/McpMySite.Server/
RUN dotnet restore src/McpMySite.Server/McpMySite.Server.csproj -a $TARGETARCH
COPY src/McpMySite.Core/ src/McpMySite.Core/
COPY src/McpMySite.Server/ src/McpMySite.Server/
RUN dotnet publish src/McpMySite.Server/McpMySite.Server.csproj -c Release -o /app --no-restore -a $TARGETARCH

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# The MCP registry reads this label from the published image to prove the listing in server.json
# belongs to this repository, so the value must equal the "name" there. CI checks that they agree.
LABEL io.modelcontextprotocol.server.name="io.github.pournasserian/mcp-my-site"
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "McpMySite.Server.dll"]
