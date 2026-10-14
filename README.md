# mcp-my-site

Make your website answer AI agents. A command-line indexer crawls the site and builds a search
index; a small web app serves that index as an MCP server (`search` and `fetch` tools) and as a
search API for the site's own search box. Hybrid keyword and vector search, one site per server,
built for sites under 1,000 pages. .NET 10, MIT licence.

## Quick start

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and an API key for any
OpenAI-compatible embeddings endpoint: OpenRouter by default, or
[another provider](docs/user-guide.md#53-other-providers). The key is read from `AI_API_KEY` and
never stored in files.

macOS and Linux:

```bash
git clone https://github.com/pournasserian/mcp-my-site.git
cd mcp-my-site
export AI_API_KEY=sk-...
dotnet run --project src/McpMySite.Indexer -- init https://example.com    # writes appsettings.json
dotnet run --project src/McpMySite.Indexer -- build                       # crawls the site into ./index
dotnet run --project src/McpMySite.Indexer -- search "reset my password"  # checks the index
Index__Path=$PWD/index dotnet run --project src/McpMySite.Server
```

Windows PowerShell:

```powershell
git clone https://github.com/pournasserian/mcp-my-site.git
cd mcp-my-site
$env:AI_API_KEY = "sk-..."
dotnet run --project src/McpMySite.Indexer -- init https://example.com
dotnet run --project src/McpMySite.Indexer -- build
dotnet run --project src/McpMySite.Indexer -- search "reset my password"
$env:Index__Path = "$PWD\index"
dotnet run --project src/McpMySite.Server
```

Docker only, no .NET on the host (the server listens on port 8080):

```bash
docker run --rm -v "${PWD}:/src" -w /src -e AI_API_KEY mcr.microsoft.com/dotnet/sdk:10.0 sh -c "dotnet run --project src/McpMySite.Indexer -- init https://example.com && dotnet run --project src/McpMySite.Indexer -- build"
docker build -f src/McpMySite.Server/Dockerfile -t mcp-my-site .
docker run --rm -p 8080:8080 -v "${PWD}/index:/index:ro" -e Index__Path=/index -e AI_API_KEY mcp-my-site
```

The [user guide](docs/user-guide.md#4-quick-start) covers `cmd`, keeping the key across terminals,
and Docker on Linux.

### Connect an agent

```bash
claude mcp add --transport http mysite http://localhost:5000/mcp
```

Other clients (VS Code, Cursor, Claude Desktop) are in
[Connecting MCP clients](docs/user-guide.md#8-connecting-mcp-clients). Without an agent, open
`http://localhost:5000/api/search?q=password`; `/healthz` shows the loaded index.

## Documentation

- [User guide](docs/user-guide.md): installation, every setting, deployment, troubleshooting, FAQ.
- [Architecture](docs/architecture.md): how the indexer, the index format and the server fit together.

## Repository

| Path | What |
|---|---|
| `src/McpMySite.Core` | Index model and manifest, JSON and SQLite stores, hybrid search, embedding client |
| `src/McpMySite.Indexer` | The `mcpmysite` CLI: crawl, extract, chunk, embed, write |
| `src/McpMySite.Server` | ASP.NET Core app: MCP endpoint, search API, index loading, telemetry |
| `tests/McpMySite.Tests` | Unit and integration tests |

```bash
dotnet build
dotnet test
```

Changes land on `master` through pull requests only.
