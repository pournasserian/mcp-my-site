# mcp-my-site

Make your website answer AI agents. A command-line indexer crawls the site and builds a search
index; a small web app serves that index as an MCP server (`search` and `fetch` tools) and as a
search API for the site's own search box. Hybrid keyword and vector search, one site per server,
built for sites under 1,000 pages. .NET 10, MIT licence.

## Quick start

```bash
mcpmysite init https://example.com          # writes appsettings.json for the indexer
export OPENROUTER_API_KEY=sk-or-...         # embeddings key, never stored in files
mcpmysite build                             # crawls, extracts, embeds, writes ./index
mcpmysite search "how do I reset my password"
Index__Path=./index dotnet run --project src/McpMySite.Server
```

Then connect an MCP client to `http://localhost:5000/mcp`, or call
`http://localhost:5000/api/search?q=password`.

`mcpmysite` is the indexer CLI; install it with `dotnet pack src/McpMySite.Indexer -c Release -o nupkg`
and `dotnet tool install --global mcpmysite --add-source nupkg`, or run it with
`dotnet run --project src/McpMySite.Indexer --`.

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
