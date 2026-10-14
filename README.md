# mcp-my-site

Make your website answer AI agents. A command-line indexer crawls the site and builds a search
index; a small web app serves that index as an MCP server (`search` and `fetch` tools) and as a
search API for the site's own search box. Hybrid keyword and vector search, one site per server,
built for sites under 1,000 pages. .NET 10, MIT licence.

## Quick start

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and an API key for any
OpenAI-compatible embeddings endpoint. That can be an AI gateway such as
[OpenRouter](https://openrouter.ai) (the default), [Vercel AI Gateway](https://vercel.com/ai-gateway)
or a self-hosted [LiteLLM](https://docs.litellm.ai) proxy, or a provider directly: OpenAI, Azure
OpenAI, Google Gemini, or [Ollama](https://ollama.com) running locally. See
[Other providers](docs/user-guide.md#53-other-providers) to switch away from OpenRouter. From a fresh clone:

```bash
git clone https://github.com/pournasserian/mcp-my-site.git
cd mcp-my-site
export AI_API_KEY=sk-...    # your provider's API key, never stored in files
dotnet run --project src/McpMySite.Indexer -- init https://example.com
dotnet run --project src/McpMySite.Indexer -- build
dotnet run --project src/McpMySite.Indexer -- search "how do I reset my password"
Index__Path=$PWD/index dotnet run --project src/McpMySite.Server
```

On Windows PowerShell, set the variables with `$env:AI_API_KEY = "sk-..."` and
`$env:Index__Path = "$PWD/index"` instead.

`init` writes `appsettings.json`, `build` crawls the site and writes `./index`, and `search` checks the
index before you serve it. Then connect an MCP client to `http://localhost:5000/mcp`, or open
`http://localhost:5000/api/search?q=password`.

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
