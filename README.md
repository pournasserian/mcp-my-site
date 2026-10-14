# mcp-my-site

Make your website answer AI agents. A command-line indexer crawls the site and builds a search
index; a small web app serves that index as an MCP server (`search` and `fetch` tools) and as a
search API for the site's own search box. Hybrid keyword and vector search, one site per server,
built for sites under 1,000 pages. .NET 10, MIT licence.

## Quick start

You need [Git](https://git-scm.com), the [.NET 10 SDK](https://dotnet.microsoft.com/download) (or
only [Docker](https://www.docker.com/products/docker-desktop/), see below) and an
[OpenRouter](https://openrouter.ai) API key for embeddings. The key is read from the
`OPENROUTER_API_KEY` environment variable and never stored in files.

In every variant, `init` writes `appsettings.json`, `build` crawls the site and writes `./index`, and
`search` checks the index before you serve it. The server then needs an absolute `Index__Path`,
because `dotnet run` starts it in its own project folder.

### macOS and Linux (bash, zsh)

Install the SDK with `brew install --cask dotnet-sdk` on macOS, or your distribution's package (see
[Install .NET on Linux](https://learn.microsoft.com/dotnet/core/install/linux)).

```bash
git clone https://github.com/pournasserian/mcp-my-site.git
cd mcp-my-site
export OPENROUTER_API_KEY=sk-or-...
dotnet run --project src/McpMySite.Indexer -- init https://example.com
dotnet run --project src/McpMySite.Indexer -- build
dotnet run --project src/McpMySite.Indexer -- search "how do I reset my password"
Index__Path=$PWD/index dotnet run --project src/McpMySite.Server
```

### Windows PowerShell

Install the SDK with `winget install Microsoft.DotNet.SDK.10`, then open a new terminal.

```powershell
git clone https://github.com/pournasserian/mcp-my-site.git
cd mcp-my-site
$env:OPENROUTER_API_KEY = "sk-or-..."
dotnet run --project src/McpMySite.Indexer -- init https://example.com
dotnet run --project src/McpMySite.Indexer -- build
dotnet run --project src/McpMySite.Indexer -- search "how do I reset my password"
$env:Index__Path = "$PWD\index"
dotnet run --project src/McpMySite.Server
```

### Windows Command Prompt (cmd)

Same SDK install as PowerShell. In `cmd`, do not put quotes around the value of `set`.

```bat
git clone https://github.com/pournasserian/mcp-my-site.git
cd mcp-my-site
set OPENROUTER_API_KEY=sk-or-...
dotnet run --project src/McpMySite.Indexer -- init https://example.com
dotnet run --project src/McpMySite.Indexer -- build
dotnet run --project src/McpMySite.Indexer -- search "how do I reset my password"
set Index__Path=%CD%\index
dotnet run --project src/McpMySite.Server
```

The variables last for the current window only. To keep the key for new terminals, run
`setx OPENROUTER_API_KEY sk-or-...` (cmd) or
`[Environment]::SetEnvironmentVariable("OPENROUTER_API_KEY", "sk-or-...", "User")` (PowerShell).

### Docker

The server ships as a container image; the indexer runs in the .NET SDK image, so the host needs only
Git and Docker. Set `OPENROUTER_API_KEY` in your shell as above (`-e OPENROUTER_API_KEY` passes it
through), then from the repository root:

```bash
docker run --rm -v "${PWD}:/src" -w /src -e OPENROUTER_API_KEY mcr.microsoft.com/dotnet/sdk:10.0 sh -c "dotnet run --project src/McpMySite.Indexer -- init https://example.com && dotnet run --project src/McpMySite.Indexer -- build"
docker build -f src/McpMySite.Server/Dockerfile -t mcp-my-site .
docker run --rm -p 8080:8080 -v "${PWD}/index:/index:ro" -e Index__Path=/index -e OPENROUTER_API_KEY mcp-my-site
```

These lines work as written in bash, zsh and PowerShell; in `cmd`, write `%CD%` instead of `${PWD}`.
If .NET is installed on the host, you can run the indexer there instead and use only the last two
lines. The container listens on port 8080 rather than 5000. On Linux, the SDK container leaves
root-owned `bin` and `obj` folders in the clone; add `--user "$(id -u):$(id -g)" -e HOME=/tmp` to
the first command to avoid that.

### Next

Connect an MCP client to `http://localhost:5000/mcp` (`8080` with Docker), or open
`http://localhost:5000/api/search?q=password`. `/healthz` shows the loaded index.

Everything after `--` goes to the indexer CLI. To type `mcpmysite build` instead, install it as a
.NET tool: `dotnet pack src/McpMySite.Indexer -c Release -o nupkg`, then
`dotnet tool install --global mcpmysite --add-source nupkg`.

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
