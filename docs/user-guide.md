# mcp-my-site user guide

Install, configure and run mcp-my-site: the indexer that turns a website into a search index, and the
server that exposes that index to AI agents over MCP and to visitors over a search API.

This guide covers version 0.1 (V1). Everything in it was checked against the code; where behaviour is
a deliberate design choice, the guide says so.

## Contents

1. [What it is](#1-what-it-is)
2. [Requirements](#2-requirements)
3. [Installation](#3-installation)
4. [Quick start](#4-quick-start)
5. [The embedding provider and your API key](#5-the-embedding-provider-and-your-api-key)
6. [The indexer](#6-the-indexer)
7. [The server](#7-the-server)
8. [Connecting MCP clients](#8-connecting-mcp-clients)
9. [Adding a search box to your site](#9-adding-a-search-box-to-your-site)
10. [Deployment](#10-deployment)
11. [Operations](#11-operations)
12. [Security](#12-security)
13. [Making your site index well](#13-making-your-site-index-well)
14. [Troubleshooting](#14-troubleshooting)
15. [FAQ](#15-faq)
16. [Reference](#16-reference)

---

## 1. What it is

mcp-my-site has two parts that share one index format.

```
                 build (CLI, runs offline or in CI)
 your website ──────────────────────────────────────▶  index/
   sitemap, HTML      crawl → extract → chunk → embed      manifest.json
                                                           pages.json  chunks.json  vectors.bin
                                                           (or index.sqlite)
                                                              │
                                                              │ local path or URL, polled
                                                              ▼
 AI agents  ◀── MCP: search, fetch ──┐          ┌──────  McpMySite.Server  ──────┐
 visitors   ◀── GET /api/search ─────┤          │  hybrid search: keyword + vector │
 operators  ◀── GET /healthz ────────┘          │  rate limiting, optional API key  │
                                                └───────────────────────────────────┘
```

- **The indexer** (`mcpmysite`) reads the site from its sitemap, or from a folder of built HTML,
  extracts the main content of each page as Markdown, splits it into heading-aware chunks, embeds the
  chunks with an OpenAI-compatible embeddings model, and writes an index. Every run is a full rebuild.
- **The server** (`McpMySite.Server`) loads the index, keeps it fresh by polling, and answers two kinds
  of callers with the same search code: MCP clients through the `search` and `fetch` tools, and your
  site's search box through `GET /api/search`.

Principles that shape everything below:

- One website per index and per server. There is no multi-tenancy.
- Built for small sites: mostly under 1,000 pages, often under 100.
- Search is always hybrid (keyword plus vector), so an embedding provider is required.
- Content selection is automatic. There are no per-site CSS selectors to maintain.
- Configuration is file based. There is no admin UI.
- Open source, MIT licence.

## 2. Requirements

| Need | Details |
|---|---|
| .NET 10 SDK | To build and run. The runtime alone is enough for a published server. |
| An embeddings endpoint | Any OpenAI-compatible `/embeddings` endpoint: an AI gateway (OpenRouter, Vercel AI Gateway, LiteLLM), a provider's own API (OpenAI, Azure OpenAI, Google Gemini) or a local server (Ollama). The default is OpenRouter with `openai/text-embedding-3-small` at 512 dimensions. See section 5.3. |
| An API key | For that endpoint, read from an environment variable, `AI_API_KEY` by default. Never written to a config file or the index. |
| A plain-HTML site | Pages must be fetchable over HTTP as HTML. JavaScript-rendered sites, PDFs and pages behind a login are out of scope. If your build produces static HTML, point the indexer at the folder instead. |
| Docker (optional) | For the container image of the server. |

The indexer and server run on Windows, macOS and Linux.

## 3. Installation

### 3.1 From source

```bash
git clone https://github.com/pournasserian/mcp-my-site.git
cd mcp-my-site
dotnet build
dotnet test
```

Run the indexer and server straight from the source tree:

```bash
dotnet run --project src/McpMySite.Indexer -- --help
dotnet run --project src/McpMySite.Server
```

### 3.2 The indexer as a .NET tool

The indexer packs as a .NET tool named `mcpmysite`. Until it is published on NuGet.org, pack it
yourself and install from the local package folder:

```bash
dotnet pack src/McpMySite.Indexer -c Release -o ./nupkg
dotnet tool install --global mcpmysite --add-source ./nupkg
mcpmysite --version
```

To update after pulling new changes, pack again and run `dotnet tool update --global mcpmysite --add-source ./nupkg`.
To remove it, `dotnet tool uninstall --global mcpmysite`.

The rest of this guide writes `mcpmysite ...`. If you did not install the tool, replace it with
`dotnet run --project src/McpMySite.Indexer --` (note the trailing `--`).

### 3.3 The server as a published app

```bash
dotnet publish src/McpMySite.Server -c Release -o ./publish
Index__Path=/path/to/index dotnet ./publish/McpMySite.Server.dll
```

The published folder is self-contained apart from the .NET 10 runtime. Configuration comes from
`appsettings.json` in that folder and from environment variables (see section 7).

### 3.4 The server as a container

The Dockerfile lives at `src/McpMySite.Server/Dockerfile` and must be built from the repository root:

```bash
docker build -f src/McpMySite.Server/Dockerfile -t mcp-my-site .
docker run --rm -p 8080:8080 \
  -e Index__Url=https://example.com/index/ \
  -e AI_API_KEY=... \
  mcp-my-site
```

The image listens on port 8080 and runs as a non-root user.

## 4. Quick start

Five steps from nothing to an MCP server answering questions about your site. Run them from the root
of a clone (section 3.1). The commands are the same on every OS; only setting environment variables
differs, so pick the block for your shell.

### 4.1 macOS and Linux (bash, zsh)

```bash
# 1. Make a config file for your site (writes appsettings.json in the current folder)
mcpmysite init https://example.com

# 2. Put your embeddings provider's API key in the environment (see section 5.2)
export AI_API_KEY=sk-...

# 3. Build the index into ./index
mcpmysite build

# 4. Try a few queries without starting anything
mcpmysite search "how can I contact you"

# 5. Serve it
Index__Path=index dotnet run --project src/McpMySite.Server
```

### 4.2 Windows PowerShell

```powershell
# 1. Make a config file for your site
mcpmysite init https://example.com

# 2. Put your embeddings key in the environment (see section 5.2)
$env:AI_API_KEY = "sk-..."

# 3. Build the index into .\index
mcpmysite build

# 4. Try a few queries without starting anything
mcpmysite search "how can I contact you"

# 5. Serve it
$env:Index__Path = "index"
dotnet run --project src/McpMySite.Server
```

### 4.3 Windows Command Prompt (cmd)

`set` takes the value without quotes; quotes would become part of the value.

```bat
rem 1. Make a config file for your site
mcpmysite init https://example.com

rem 2. Put your embeddings key in the environment (see section 5.2)
set AI_API_KEY=sk-...

rem 3. Build the index into .\index
mcpmysite build

rem 4. Try a few queries without starting anything
mcpmysite search "how can I contact you"

rem 5. Serve it
set Index__Path=index
dotnet run --project src/McpMySite.Server
```

Variables set with `set` or `$env:` last for the current window only. Section 5.2 shows how to keep
the key for new terminals.

### 4.4 Docker

With only Git and Docker on the host, run the indexer in the .NET SDK image and the server in its own
image (section 3.4). Set `AI_API_KEY` in your shell as above; `-e AI_API_KEY` without a
value passes it through to the container.

```bash
# 1 and 3. Make the config and build the index into ./index
docker run --rm -v "${PWD}:/src" -w /src -e AI_API_KEY mcr.microsoft.com/dotnet/sdk:10.0 sh -c "dotnet run --project src/McpMySite.Indexer -- init https://example.com && dotnet run --project src/McpMySite.Indexer -- build"

# 5. Build the server image and serve the index from a read-only mount
docker build -f src/McpMySite.Server/Dockerfile -t mcp-my-site .
docker run --rm -p 8080:8080 -v "${PWD}/index:/index:ro" -e Index__Path=/index -e AI_API_KEY mcp-my-site
```

The commands work as written in bash, zsh and PowerShell; in `cmd`, write `%CD%` instead of `${PWD}`.
If .NET is installed on the host, build the index there (4.1 to 4.3) and use only the last two
commands. On Linux the SDK container leaves root-owned `bin` and `obj` folders in the clone; add
`--user "$(id -u):$(id -g)" -e HOME=/tmp` to the first command to avoid that.

The container listens on port 8080, so use `http://localhost:8080` in place of
`http://localhost:5000` below.

### 4.5 Try it

The server prints the URL it listens on (by default `http://localhost:5000`). Then:

| URL | What you get |
|---|---|
| `http://localhost:5000/healthz` | JSON with the loaded index: site, page count, build time |
| `http://localhost:5000/api/search?q=contact` | JSON search results |
| `http://localhost:5000/mcp` | The MCP endpoint for agents (POST, JSON-RPC) |

Point an MCP client at `http://localhost:5000/mcp` (section 8) and ask it something about the site.

## 5. The embedding provider and your API key

Embeddings turn text into vectors so the search understands meaning as well as words. The indexer
embeds every chunk once at build time; the server embeds each query at search time.

mcp-my-site talks to any endpoint that speaks the OpenAI embeddings API, so you can use an AI
gateway, a model provider directly, or a model running on your own machine. Nothing in the code is
tied to one vendor; the endpoint, model and key variable are plain settings.

### 5.1 Default: OpenRouter

The shipped defaults point at [OpenRouter](https://openrouter.ai), so they work as-is with an
OpenRouter key. To use something else, see section 5.3.

| Setting | Default |
|---|---|
| Endpoint | `https://openrouter.ai/api/v1` |
| Model | `openai/text-embedding-3-small` |
| Dimensions | 512 (the model's native 1,536 reduced to 512; OpenRouter honours the `Embeddings:Dimensions` parameter) |
| Key variable | `AI_API_KEY` |

### 5.2 Setting the key

The key is read from an environment variable at run time. Set it in the shell that runs the indexer
or the server:

```bash
# macOS and Linux (bash, zsh)
export AI_API_KEY=sk-...
```

```powershell
# Windows PowerShell, current session
$env:AI_API_KEY = "sk-..."

# Windows, persistent for your user (new terminals only)
[Environment]::SetEnvironmentVariable("AI_API_KEY", "sk-...", "User")
```

```bat
rem Windows Command Prompt, current session (no quotes around the value)
set AI_API_KEY=sk-...

rem Windows, persistent for your user (new terminals only)
setx AI_API_KEY sk-...
```

In CI, store it as a secret and expose it as an environment variable for the build step (section 10.3).
On Azure App Service, add it as an application setting or a Key Vault reference (section 10.2).

Never put the key in `appsettings.json` or source control. If you prefer to keep it in configuration
rather than a named variable, use the standard .NET layers that stay out of the repository: the
`Embeddings__ApiKey` environment variable, or `dotnet user-secrets set Embeddings:ApiKey <key>` when
running with the `Development` environment. `Embeddings:ApiKey` takes precedence over
`Embeddings:ApiKeyEnvironmentVariable` when both are present.

### 5.3 Other providers

Any OpenAI-compatible endpoint works. In the indexer's `appsettings.json`, change
`Embeddings:Endpoint` and `Embeddings:Model`, and `Embeddings:Dimensions` if the model needs it. On
the server, set the same `Embeddings:Endpoint`. Put that provider's key in `AI_API_KEY`. If you'd
rather keep a variable name you already use, such as `OPENAI_API_KEY`, set
`Embeddings:ApiKeyEnvironmentVariable` to that name on both sides.

AI gateways sit in front of many providers behind one key and one bill:

| Gateway | Endpoint | Example model | Notes |
|---|---|---|---|
| [OpenRouter](https://openrouter.ai) | `https://openrouter.ai/api/v1` | `openai/text-embedding-3-small` | Default. |
| [Vercel AI Gateway](https://vercel.com/ai-gateway) | `https://ai-gateway.vercel.sh/v1` | `openai/text-embedding-3-small` | Use an AI Gateway API key. |
| [LiteLLM](https://docs.litellm.ai) proxy (self-hosted) | `http://localhost:4000/v1` | the `model_name` from your proxy config | The key is a LiteLLM virtual key or the proxy's master key. |

Or call a provider directly:

| Provider | Endpoint | Example model | Notes |
|---|---|---|---|
| OpenAI | `https://api.openai.com/v1` | `text-embedding-3-small` | |
| Azure OpenAI | `https://<resource>.openai.azure.com/openai/v1` | your embedding deployment name | Uses the v1 API. The model is the deployment name, not the model name. |
| Google Gemini | `https://generativelanguage.googleapis.com/v1beta/openai` | `gemini-embedding-001` | Use a Gemini API key. |
| Ollama (local) | `http://localhost:11434/v1` | `nomic-embed-text` | Set `Embeddings:Dimensions` to the model's native size (768 for this one). `AI_API_KEY` must hold any non-empty value. |

Anything else works too, as long as it accepts `POST /embeddings` with `model`, `input` and
`dimensions`, using a bearer-token key. Gateways that need extra request headers are not supported.
Every request sends `dimensions` (the value of `Embeddings:Dimensions`). For a model that cannot
shorten its vectors, set it to the model's native size.

Rules of thumb:

- The server always embeds queries with the model and dimensions recorded in the index manifest, so
  the server's own `Embeddings:Model` and `Embeddings:Dimensions` settings are ignored. Only the
  endpoint and key settings matter on the server side.
- Changing the model or the dimensions means a full re-embed on the next build. The cache is keyed by
  model and dimensions, so nothing is reused.
- If the model does not support the `Embeddings:Dimensions` parameter, set `Embeddings:Dimensions` to its native size or
  the build fails with a clear message.

### 5.4 Cost

`text-embedding-3-small` costs about USD 0.02 per million tokens. A page produces roughly 1,500 to
4,000 tokens of chunks, so a 50-page site embeds for a fraction of a cent and a 1,000-page site for a
few cents. Rebuilds re-embed only chunks whose text changed, thanks to the cache. Query embeddings are
a few tokens each.

## 6. The indexer

### 6.1 Commands

```
mcpmysite init <site-url> [--config <file>] [--store json|sqlite]
mcpmysite build [--config <file>] [--environment <name>] [--set Key=Value]... [--site-url <url>] [--folder <dir>] [--output <dir>] [--store json|sqlite] [--max-pages <n>] [--verbose]
mcpmysite search <query> [--config <file>] [--environment <name>] [--index <dir>] [--limit <n>] [--keyword-only]
```

**`init`** writes a starter `appsettings.json` with every setting and its default, plus a `Logging`
section. It refuses to overwrite an existing file. It is a convenience: a file with only `Site:Url` in
it works too, because every other setting falls back to the shipped defaults (section 6.2).

**`build`** loads the configuration (section 6.2), applies the command-line overrides below, and
performs a full rebuild. Relative paths (`Output:Directory`, `Cache:Path`, `Source:Folder`) resolve
against the folder that holds the config file. Without a config file you can still run
`mcpmysite build --site-url https://example.com`; defaults then apply and relative paths resolve
against the current folder.

| Option | Effect |
|---|---|
| `-c, --config <file>` | Path to `appsettings.json` (default: `appsettings.json` in the current folder) |
| `-e, --environment <name>` | Environment name; selects `appsettings.{name}.json`. Default: `DOTNET_ENVIRONMENT`, then `ASPNETCORE_ENVIRONMENT`, then `Production` |
| `--set Key=Value` | Overrides any setting, for example `--set Crawl:MaxPages=50`. Repeat for several. `__` works as a separator too |
| `--site-url <url>` | `Site:Url` |
| `--folder <dir>` | Sets `Source:Type` to `Folder` and `Source:Folder` to the directory |
| `-o, --output <dir>` | `Output:Directory` |
| `--store json\|sqlite` | `Output:Store` |
| `--max-pages <n>` | `Crawl:MaxPages` |
| `-v, --verbose` | Debug logging, including skipped URLs and retries |

**`search`** runs a query against a built index without a server. It is the quickest way to judge an
index. It reads the same configuration as `build` (so the index folder defaults to
`Output:Directory` and the embeddings endpoint and key variable come from `Embeddings`), embeds the
query with the model recorded in the manifest, and prints the hits. Pass `--index` to point at another
folder and `--keyword-only` to skip the embedding call.

Exit codes:

| Code | Meaning |
|---|---|
| 0 | Index built (or `init`/`search` succeeded) |
| 1 | Build ran but failed a check or hit an error; see the printed reason and `report.json` |
| 2 | Could not start: config file missing or invalid (`Site:Url` not an absolute URL, unknown store, missing folder) |

### 6.2 Configuration reference

The indexer is configured exactly like an ASP.NET Core app. Settings are read from these layers, each
overriding the one before:

0. `appsettings.defaults.json`, shipped next to the tool with every setting and its default. Read it
   to see what can be set; do not edit it, because updating the tool replaces it. The same file is in
   the repository at `src/McpMySite.Indexer/appsettings.defaults.json`;
1. `appsettings.json` (the file named by `--config`);
2. `appsettings.{Environment}.json` in the same folder, for example `appsettings.Staging.json` with a
   lower `Crawl:MaxPages` for a smoke test;
3. user secrets, only when the environment is `Development` (`dotnet user-secrets --id mcpmysite-indexer`);
4. environment variables, with `__` between levels: `Crawl__MaxPages=50`, `Site__Url=https://...`;
5. `--set Section:Key=Value` arguments;
6. the convenience flags (`--site-url`, `--folder`, `--output`, `--store`, `--max-pages`).

Keys are written `Section:Key` below; JSON nests them (`"Crawl": { "MaxPages": 50 }`). Matching is
case-insensitive. Comments and trailing commas are allowed in the JSON files.

If the site itself is a .NET project, its own `appsettings.json` lives in the same repository. Keep
the indexer's file in a folder of its own, such as `mcp/appsettings.json`, and pass
`--config mcp/appsettings.json`; relative paths then resolve against `mcp/`.

Every key is optional except `Site:Url`, so your `appsettings.json` only needs the keys you change.
List values such as `Crawl:Exclude` merge by position across layers rather than replacing each other,
which is why their shipped defaults are empty.

| Key | Type | Default | Meaning |
|---|---|---|---|
| `Site:Url` | string | (required) | Absolute `http(s)` URL of the site. Also the base for folder-sourced URLs. |
| `Site:Name` | string | from the site | Display name used in the server instructions. Falls back to the home page's `og:site_name`, then the host name. |
| `Site:Description` | string | from the site | One or two sentences on what the site is about, used in the server instructions. Falls back to the home page's meta description. |
| `Source:Type` | `Crawl` or `Folder` | `Crawl` | Where pages come from. |
| `Source:Folder` | string | | Folder of built HTML when `Source:Type` is `Folder`. Relative to the config file. |
| `Source:FolderUrlStyle` | `Clean` or `File` | `Clean` | `Clean`: `about.html` becomes `/about`, `docs/index.html` becomes `/docs/`. `File`: paths keep `.html`. A `<link rel="canonical">` in the page wins over both. |
| `Crawl:UseSitemap` | bool | `true` | Read `sitemap.xml` (and sitemap indexes) listed in `robots.txt`, or `/sitemap.xml`. |
| `Crawl:AlwaysFollowLinks` | bool | `false` | Follow in-page links even when the sitemap has entries. Links are always followed when there is no sitemap. |
| `Crawl:RespectRobotsTxt` | bool | `true` | Obey `robots.txt` `Disallow`/`Allow` for `*` or `McpMySiteIndexer`. `noindex` (meta tag or `X-Robots-Tag`) is always respected. |
| `Crawl:MaxPages` | int | `1000` | Stop after this many pages. Applies to the folder source too. |
| `Crawl:Include` | string[] | `[]` | Path globs. When non-empty, only matching paths are fetched. |
| `Crawl:Exclude` | string[] | `[]` | Path globs that are never fetched. Exclude wins over include. |
| `Crawl:Concurrency` | int | `4` | Parallel requests. |
| `Crawl:DelayMilliseconds` | int | `0` | Pause before each request. Use with a low concurrency for fragile hosts. |
| `Crawl:TimeoutSeconds` | int | `30` | Per request. Minimum 5. |
| `Crawl:UserAgent` | string | `McpMySiteIndexer/0.1 (+https://github.com/pournasserian/mcp-my-site)` | Sent with every request. The robots.txt group `User-agent: McpMySiteIndexer` targets this crawler. |
| `Chunking:MaxChars` | int | `1500` | Upper bound for a chunk. Roughly 350 tokens. |
| `Chunking:MinChars` | int | `200` | Chunks shorter than this are merged with a neighbour. |
| `Embeddings:Endpoint` | string | `https://openrouter.ai/api/v1` | OpenAI-compatible base URL. |
| `Embeddings:Model` | string | `openai/text-embedding-3-small` | Model id as the endpoint expects it. |
| `Embeddings:Dimensions` | int | `512` | Requested vector size. Must match what the model returns. |
| `Embeddings:ApiKeyEnvironmentVariable` | string | `AI_API_KEY` | Environment variable that holds the key. |
| `Embeddings:ApiKey` | string | | Direct key value. Takes precedence when set. Avoid outside local experiments. |
| `Embeddings:BatchSize` | int | `64` | Chunks per embeddings request. |
| `Cache:Enabled` | bool | `true` | Cache embeddings so unchanged chunks are not re-embedded. |
| `Cache:Path` | string | `.mcpmysite/embedding-cache.sqlite` | Cache file, relative to the config file. Safe to delete at any time. |
| `Output:Directory` | string | `index` | Where the index is written, relative to the config file. |
| `Output:Store` | `json` or `sqlite` | `json` | Index store (section 6.6). |
| `Safety:MinPages` | int | `1` | Fail when fewer pages were indexed. |
| `Safety:MaxPageDropPercent` | int | `50` | Fail when the page count fell by more than this against the previous index in the output folder. |
| `Safety:MaxErrorPercent` | int | `20` | Fail when more than this share of fetches errored. |
| `Safety:LowTextChars` | int | `200` | Pages with less extracted text are listed in `report.json`. |
| `Instructions:Preamble` | string | | Text placed before the generated site outline in the MCP server instructions. |
| `Logging:LogLevel:*` | | `Information` | Standard .NET logging levels, for example `"Logging": { "LogLevel": { "Default": "Debug" } }`. |

#### Path globs

`Crawl:Include` and `Crawl:Exclude` match the URL path. `*` matches within one path segment, `**` spans segments,
`?` matches one character. A pattern without a leading `/` is anchored at the root anyway, and a
pattern that does not end in `*` also matches everything below it.

| Pattern | Matches | Does not match |
|---|---|---|
| `/docs/**` | `/docs/a`, `/docs/a/b` | `/blog/a` |
| `/docs/*` | `/docs/a` | `/docs/a/b` |
| `/docs` | `/docs`, `/docs/intro` | `/docsx` |
| `/*.html` | `/page.html` | `/dir/page.html` |
| `/tag/**` (in exclude) | every tag page | |

### 6.3 Content sources

#### Crawl

1. `robots.txt` is fetched. Rules for `User-agent: McpMySiteIndexer` are used if present, otherwise
   the `*` group. `Sitemap:` lines are collected. A missing or unreadable `robots.txt` means no rules.
2. Sitemaps are read: the ones from `robots.txt`, or `/sitemap.xml`. Sitemap indexes are followed,
   gzip is handled, and `<lastmod>` becomes the page's modified date when the page has none.
3. If the sitemap produced no URLs, or `Crawl:AlwaysFollowLinks` is on, the crawler starts at `Site:Url` and
   follows `<a href>` links. Only links on the same host (with or without `www.`), without a query
   string and without a non-HTML extension (`.pdf`, `.png`, `.css`, `.js`, `.xml`, ...) are followed.
4. Each URL is checked against `Crawl:Include`/`Crawl:Exclude` and robots rules, then fetched with
   `Accept: text/html`. Responses that are not HTML, `404`/`410`, or carry `X-Robots-Tag: noindex`
   are skipped silently. Other failures count as errors; `5xx` and timeouts are retried once.
   Pages over 5 MB are rejected.
5. After redirects, the final URL is used. If the page declares a same-host
   `<link rel="canonical">`, that URL becomes the page URL. A second page resolving to the same URL is
   dropped as a duplicate. Pages with `<meta name="robots" content="noindex">` are dropped.

Fragments are removed from all URLs. `Crawl:MaxPages` counts accepted pages.

#### Folder

Point `Source:Folder` at the output of your static site build. Every `.html` and `.htm` file is read
and mapped to a URL under `Site:Url` according to `Source:FolderUrlStyle`. Metadata that normally comes from
HTTP (last modified) comes from the file timestamp instead. Robots rules are not consulted, but
`noindex` meta tags and canonical links still apply. This is the right source when the live site is
behind a CDN that blocks crawlers, or when you want to index before the deploy goes live.

### 6.4 Extraction

For each page the indexer picks one content element, cleans it and converts it to Markdown.

**Content selection**, in order:

1. `<main>`. If the main element contains exactly one `<article>` holding at least half of its text,
   the article is used (so sidebars inside `main` are skipped).
2. A single `<article>` when there is no `<main>`.
3. Otherwise a density heuristic: block containers are scored by text length, penalised by the share
   of text that sits inside links, and the best block (widened to its parent while the parent is not
   much larger) wins. Menus and tag clouds score badly; prose scores well.

The report records which strategy was used for each page (`main`, `article`, `auto`) so you can see
whether your markup is being understood.

**Removed from the chosen element:** `script`, `style`, `noscript`, `template`, `svg`, `canvas`,
`iframe`, `object`, `embed`, `form`, `button`, `input`, `select`, `textarea`, `nav`, `aside`, elements
with `role` navigation/banner/contentinfo/complementary/search, `hidden` or `aria-hidden="true"`
elements, `.skip-link`, `.visually-hidden`, `.sr-only`, HTML comments, and `footer` elements that
look like page chrome (three or more links, or almost no text). Headers inside the content are kept,
because article headers often carry the title and date.

**Metadata:**

| Field | Sources, first match wins |
|---|---|
| Title | `<title>` (with a trailing ` \| Site name` or ` - Site name` removed when it equals `og:site_name`), `og:title`, first `<h1>`, the URL path |
| Description | `<meta name="description">`, `og:description` |
| Language | `<html lang>`, `og:locale` (`en_CA` becomes `en-CA`), `<meta http-equiv="content-language">`, else `und` |
| Modified | `article:modified_time`, `og:updated_time`, `<meta name="last-modified">`, `<meta name="date">`, `article:published_time`, `dcterms.modified`, `dcterms.date`, the first `<time datetime>` in the content, sitemap `<lastmod>`, HTTP `Last-Modified` |
| Headings | Every `h1`–`h6` inside the content, in order |

Links and images inside the content are rewritten to absolute URLs before conversion, so Markdown
links keep working wherever the text is shown. No HTML and no link graph are stored.

### 6.5 Chunking

Markdown is split by headings into sections, and each section remembers its heading path (for example
`Pricing > Plans > Enterprise`). Sections longer than `Chunking:MaxChars` are split at paragraph
boundaries, then at sentence ends, then hard-wrapped as a last resort. Fenced code blocks are never
split. Chunks shorter than `Chunking:MinChars` are folded into the following chunk when that fits, or
into the previous one.

The text that is embedded and keyword-indexed for a chunk is:

```
Page title > Heading > Subheading

chunk text
```

so a chunk about "Enterprise" under "Pricing" matches a query about enterprise pricing even if the
chunk body never says "pricing". A leading heading that merely repeats the page title is not repeated
in the path.

Tuning: smaller `Chunking:MaxChars` gives sharper snippets and more precise matches at the cost of more
embeddings; larger values keep more context per chunk. The defaults suit documentation and articles.

### 6.6 Index stores

Both stores hold the same data and return the same results. Pick by size.

| | `json` | `sqlite` |
|---|---|---|
| Files | `manifest.json`, `pages.json`, `chunks.json`, `vectors.bin` | `manifest.json`, `index.sqlite` |
| Keyword search | In-memory BM25 built at load | SQLite FTS5 (`unicode61`, diacritics removed) |
| Vector search | Brute-force cosine over `vectors.bin` | Brute-force cosine over vectors read from the database at load |
| Memory on the server | Whole index in memory | Vectors in memory, text read on demand |
| Good for | Up to a few hundred pages | Larger sites, or when you want to query the index with other tools |

Size guide: vectors take 4 bytes per dimension per chunk (2 KB per chunk at 512 dimensions). A
41-page site produced 471 chunks, about 1 MB of vectors and 1.8 MB of JSON text; the same index as
SQLite was 4 MB.

Both stores merge keyword and vector rankings with reciprocal rank fusion, collapse chunks to one hit
per page, and build a snippet around the first matched term in the best chunk. The manifest is written
last (and the SQLite file is written to a temporary name first), so a reader never sees a half-written
index.

### 6.7 Safety checks and the run report

A broken build must never replace a working index. Before writing anything the indexer checks:

- at least `Safety:MinPages` pages were indexed;
- if the output folder already holds an index, the page count did not fall by more than
  `Safety:MaxPageDropPercent`;
- no more than `Safety:MaxErrorPercent` of fetches failed.

Any failure leaves the previous index untouched, prints the reason and exits with code 1. The run
also refuses to start when the embeddings key is missing, because hybrid search cannot be built
without vectors.

`report.json` is written to the output folder on every run, success or failure:

| Field | Meaning |
|---|---|
| `startedAt`, `finishedAt`, `succeeded`, `failureReason` | Run outcome |
| `Site:Url`, `store` | What was built |
| `pagesFetched`, `pagesIndexed` | Pages read, pages kept |
| `pagesSkippedNoIndex`, `pagesSkippedDuplicate`, `pagesSkippedEmpty` | Why pages were dropped |
| `chunks`, `embeddingsGenerated`, `embeddingsFromCache` | Chunk count and how many embeddings the provider had to compute |
| `previousPageCount` | Page count of the index that was in the output folder before |
| `errors` | `url: message` for each failed fetch or extraction |
| `lowTextPages` | Pages with less than `Safety:LowTextChars` of text, with the content strategy used |
| `contentSources` | How many pages used `main`, `article` or `auto` |
| `errorPercent` | Errors over fetched plus errors |

Look at `lowTextPages` after the first build. They are the pages whose markup the extractor did not
understand, or pages that genuinely have little text.

### 6.8 Server instructions from the index

The indexer writes short instructions into the manifest, and the server hands them to MCP clients
in its `initialize` answer. They tell the model what the site is, when to use the server and how:

- the site name, URL and description (`Site:Description`, or the home page's meta description), with
  the description cut at 300 characters;
- how many pages were indexed and the snapshot date;
- when to use the server: questions about the site or its subjects, and answers that should rest on
  the site rather than general knowledge;
- how to use `search` and `fetch`, to cite the page URL, and to say so when nothing relevant is found;
- the content language;
- an outline: up to 12 top-level sections, largest first, each labelled with the title of its own
  landing page (such as `/docs`) when that page was indexed, its page count and up to four page
  titles, newest first.

The text is built from the crawled pages by a fixed template, so the same site gives the same text
whatever order the crawl found the pages in. It is kept to 2,000 characters: Claude Code is reported
to cut server instructions at 2,048, and several servers share the model's attention anyway. When a
site has more to say, the outline gives way first, dropping page titles and then whole sections (the
last line then says how many were left out); everything above the outline always stays.

`Instructions:Preamble` adds your own text in front (for example, "Answer in the site's own voice and
link to the page you used."). It counts toward the 2,000 characters, so a long preamble shortens the
outline, and the indexer warns when the preamble alone pushes the text past the limit. On the server,
`Mcp:Instructions` replaces the whole text and is not checked.

Section landing-page titles and the home page description do most of the work, so give them content
a reader would understand out of context: "Writing: MCP servers, AI tooling, IoT" says more than
"Blog".

## 7. The server

### 7.1 Running

```bash
# From source
Index__Path=index dotnet run --project src/McpMySite.Server

# Published
Index__Path=/srv/index ASPNETCORE_URLS=http://0.0.0.0:8080 dotnet McpMySite.Server.dll

# Container
docker run -p 8080:8080 -e Index__Url=https://example.com/index/ -e AI_API_KEY=... mcp-my-site
```

`dotnet run` listens on `http://localhost:5000`, as set in `Properties/launchSettings.json`. A
relative `Index:Path` resolves from the folder you run the command in; the server still reads its own
`appsettings.json` from `src/McpMySite.Server`.

The server starts even when the index cannot be loaded; it answers `503` on `/healthz`, returns tool
errors on MCP calls and `503` on the search API, and keeps retrying on the poll interval. Once an
index is loaded, a later failed refresh never removes it.

### 7.2 Configuration reference

Settings live in `appsettings.json` next to the server and can be overridden with environment
variables using `__` as the separator (`Index__Url`, `SearchApi__AllowedOrigins__0`). Environment
variables win over the file. All settings are read at startup; restart after changing them.

| Setting | Default | Meaning |
|---|---|---|
| `Index:Path` | | Local folder holding the index. |
| `Index:Url` | | URL of `manifest.json`, or of the folder that contains it. Takes precedence over `Path`. |
| `Index:PollIntervalSeconds` | `300` | How often to check for a new manifest. `0` loads once and never polls. |
| `Embeddings:Endpoint` | `https://openrouter.ai/api/v1` | OpenAI-compatible base URL used to embed queries. |
| `Embeddings:ApiKeyEnvironmentVariable` | `AI_API_KEY` | Environment variable that holds the key. |
| `Embeddings:ApiKey` | | Direct key value; prefer the variable or user-secrets. |
| `Mcp:Path` | `/mcp` | MCP endpoint path. |
| `Mcp:ServerName` | `mcp-my-site` | Reported as the server name to MCP clients and used as the telemetry service name. |
| `Mcp:ApiKey` | | When set, MCP requests must carry it as `Authorization: Bearer <key>` or `X-Api-Key: <key>`. Set via `Mcp__ApiKey`. The search API is never key-protected. |
| `Mcp:Instructions` | | Replaces the instructions generated by the indexer. |
| `Mcp:AllowedOrigins` | `[]` | Browser origins allowed to call the MCP endpoint (CORS). Empty allows none, which is right for non-browser clients. |
| `SearchApi:Enabled` | `true` | Serve the HTTP search endpoint. |
| `SearchApi:Path` | `/api/search` | |
| `SearchApi:AllowedOrigins` | `[]` | Browser origins allowed to call the search API, typically your site's origin. Empty allows none. |
| `SearchApi:DefaultLimit` | `10` | Results when `limit` is not given. |
| `SearchApi:MaxLimit` | `20` | Upper bound for `limit`. |
| `SearchApi:MaxQueryLength` | `500` | Longer queries get `400`. |
| `Ui:Enabled` | `true` | Serve the pages meant for people: the landing page at `/`, the MCP playground at `/playground`, the API reference at `/docs`, `llms.txt` and `sitemap.xml`. The OpenAPI document, `/api/info` and `robots.txt` are served either way. |
| `Ui:PublicUrl` | | Public base URL of the server, such as `https://mcp.example.com`, used for canonical links, the sitemap and the connection snippets. Empty takes it from each request. Set it when the server sits behind a proxy that changes the host. |
| `Ui:StaleAfterDays` | `30` | The landing page and `/api/info` flag the index as stale when it is older than this. `0` turns the flag off. |
| `RateLimiting:PermitPerMinute` | `60` | Requests per client IP per minute, shared across the MCP endpoint and the search API. Excess requests get `429`. |
| `RateLimiting:UseForwardedHeaders` | `true` | Take the client IP from `X-Forwarded-For`, as set by Azure App Service and most reverse proxies. Set `false` when clients reach Kestrel directly. |
| `Telemetry:LogQueryText` | `false` | Include query text in logs and trace tags. Off by default because queries can carry personal data. |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | | When set, traces, metrics and logs go to Application Insights. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | | When set (and no Application Insights string), everything goes to that OTLP collector. |
| `ASPNETCORE_URLS` | `http://localhost:5000` | Listening address, standard ASP.NET Core. The container sets port 8080. |
| `Logging:LogLevel:*` | `Information` | Standard .NET logging configuration. |

### 7.3 Index source, polling and hot swap

**Local path.** The server reads `manifest.json` from the folder on the poll interval. When the
manifest's build time or page count differs from the loaded index, it opens the new files, validates
them, and swaps the new store in. Requests in flight finish on the old store, which is disposed
30 seconds later.

**URL.** `Index:Url` may point at the manifest or at the folder (`https://example.com/index/`). The
server downloads the manifest; if it is newer, it downloads the rest of the files for that store type
into a temporary folder, opens and validates them, then swaps. The previous download is deleted a
minute later. Unchanged manifests cost one small GET per poll.

**Validation.** A manifest is accepted when its format version is supported (currently 1), its store
type is known, it names a site, an embedding model and dimensions, and it has at least one page. The
store then checks that the data files agree with the manifest (vector file length, page and chunk
counts). Anything wrong is logged, recorded in `/healthz` as `lastError`, and the last good index
stays in service.

This is how you ship a new index without touching the server: build in CI, publish the folder to the
URL, and the server picks it up within the poll interval.

### 7.4 The MCP endpoint

- Transport: MCP Streamable HTTP, **stateless**. Clients `POST` JSON-RPC to `Mcp:Path`; there are no
  sessions, no `GET` stream and no server-to-client requests. Any number of server instances can sit
  behind a load balancer without affinity.
- Clients must send `Accept: application/json, text/event-stream` (the MCP specification requires
  it); the response is a single JSON or SSE message.
- Capabilities: tools only. No resources, no prompts.
- `initialize` returns the server name and version and the instructions described in section 6.8.
- `tools/list` describes `search` with the loaded site's name, URL, description and page count, so a
  model knows which site the server covers even when its client ignores the instructions.
- The tool shapes follow the `search` and `fetch` contract ChatGPT uses for connectors and deep
  research: every search result has an `id`, and `fetch` takes that `id` and returns the page as
  `text`. Other clients can keep passing a `url`.

**Tool `search`**

| Parameter | Type | Meaning |
|---|---|---|
| `query` | string, required | Natural language or keywords |
| `limit` | integer, optional | Pages to return, clamped to 1–20, default 10 |

Returns structured content:

```json
{
  "query": "contact email",
  "keywordOnly": false,
  "results": [
    { "id": "https://example.com/contact",
      "title": "Contact", "url": "https://example.com/contact",
      "snippet": "…plain text excerpt around the first match…",
      "section": "Get in touch > Email", "score": 0.0325 }
  ]
}
```

`keywordOnly` is `true` when the query could not be embedded and the result came from keyword search
alone. Scores are reciprocal-rank-fusion scores, useful for ordering, not as probabilities. `id` is
what to pass to `fetch`; today it is the page URL.

**Tool `fetch`**

| Parameter | Type | Meaning |
|---|---|---|
| `id` | string, optional | The `id` of a `search` result. |
| `url` | string, optional | A page URL, as an alternative to `id`. Scheme, host case, trailing slash and fragment differences are tolerated. |

Pass one of the two; `id` wins when both are given. Returns `id`, `url`, `title`, the full page as
Markdown in `text`, `description`, `language` and `modified`. Passing neither, or an unknown page, is
a tool error ("No indexed page at ..."), not a protocol error, so the model can recover by searching
again.

Both tools are annotated read-only, idempotent and closed-world. The same response is also included
as a JSON text block for clients that ignore structured content.

### 7.5 The search API

```
GET /api/search?q=<text>&limit=<n>
```

| Status | When |
|---|---|
| `200` | Results (possibly empty) |
| `400` | `q` missing or longer than `SearchApi:MaxQueryLength` |
| `429` | Rate limit exceeded |
| `503` | No index loaded; the body says why |

```json
{
  "query": "contact email",
  "keywordOnly": false,
  "results": [
    {
      "title": "Contact",
      "url": "https://example.com/contact",
      "description": "How to reach us by email or phone.",
      "snippet": "…To contact us, send an email to hello@example.com…",
      "highlightedSnippet": "…To <mark>contact</mark> us, send an <mark>email</mark> to hello@example.com…",
      "section": ["Get in touch", "Email"],
      "score": 0.0325
    }
  ]
}
```

`highlightedSnippet` is HTML-escaped by the server with only `<mark>` tags added, so it can be
inserted into the page as HTML. `title` and `description` are plain text and must be escaped by the
caller. Search runs on submit only; there is no search-as-you-type endpoint, and the per-IP rate limit
is sized for that.

The API is described by an OpenAPI 3.1 document at `/openapi/v1.json`, titled after the loaded site.
Point a client generator or an API tool at it. `/docs` renders the same document as an interactive
reference (Scalar) where you can try queries; its script is served by the server itself, so the page
works without access to a CDN. Set `Ui:Enabled` to `false` to drop the page. The MCP endpoint is not
part of the document; MCP clients discover its tools through `tools/list`.

### 7.6 Rate limiting

Every MCP and search request counts against a fixed one-minute window per client IP
(`RateLimiting:PermitPerMinute`, default 60). Excess requests receive `429` immediately; nothing is
queued. `/healthz` and `/` are not limited.

The client IP is taken from `X-Forwarded-For` when `RateLimiting:UseForwardedHeaders` is `true`
(the default, right for Azure App Service and reverse proxies). Two mistakes to avoid:

- Behind a proxy with the setting `false`, every visitor shares the proxy's IP and they are all
  throttled together.
- Exposed directly to the internet with the setting `true`, a client can forge the header and bypass
  the limit. Set it to `false` in that case.

### 7.7 Health and status

`GET /healthz` returns `200` with the loaded index, or `503` when none is loaded:

```json
{ "status": "ok", "site": "https://example.com/", "pages": 41,
  "builtAt": "2026-10-10T22:20:05+00:00", "loadedAt": "2026-10-10T22:21:45+00:00", "lastError": null }
```

`lastError` holds the most recent load or refresh failure even while an older index is still being
served, which makes it the first place to look when a new index did not appear.

`GET /api/info` returns everything the landing page shows, as JSON: the site (name, URL, description,
main language), the index (build and load times, `stale`, page and chunk counts, store, embedding
model, indexer version and the build `stats` described in section 16.1), the absolute URLs of every
endpoint, whether MCP needs a key, and the server name and version. It answers `503` with the same
shape and no `index` until an index is loaded, and it is in the OpenAPI document.

### 7.8 The landing page

With `Ui:Enabled` on, `GET /` is a page for people who open the server's URL:

- the site's name, description and link;
- how to connect: the MCP URL with a copy button and ready-made setup for Claude Code, Claude.ai and
  Claude Desktop, ChatGPT, VS Code, Cursor and stdio-only clients. When `Mcp:ApiKey` is set it says a
  key is needed (never the key) and leaves out the clients that cannot send one;
- what is indexed: pages, approximate words, sections with their page counts, build date, newest page
  and languages;
- the last build: source, duration, pages fetched and skipped, errors, embeddings generated and cached,
  chunks, embedding model and store;
- the search API with an example and links to the OpenAPI document and reference;
- a notice when the index is older than `Ui:StaleAfterDays`.

The page is rendered on the server, so search engines and link previews see all of it; script only
powers the copy buttons. It carries a description, a canonical link, Open Graph tags and schema.org
`WebAPI` data. `robots.txt` keeps crawlers out of the MCP endpoint and `/api/`, and points them at
`sitemap.xml`, which lists `/`, `/playground` and `/docs/` with the index build date. Until an index is loaded the
page answers `503` and asks not to be indexed. With `Ui:Enabled` off, `/` is a short text summary and
`robots.txt` disallows everything.

`GET /llms.txt` follows the [llms.txt](https://llmstxt.org/) convention: the site name and
description, a line on how to reach this server over MCP and HTTP, then every indexed page as a
Markdown link with its description, grouped under the same section names as the landing page and
newest first. It is built once per loaded index and kept in memory. It describes the site, so you
can also publish a copy at your site's own `/llms.txt`.

Indexes from indexers older than this feature have no build statistics; the page then shows the
manifest's page and chunk counts and build date only. Rebuild to get the rest.

### 7.9 The MCP playground

`GET /playground` is a small MCP client in the page, so anyone can try the server without installing
one. It connects to this server's MCP endpoint (on load, or after an API key is entered when
`Mcp:ApiKey` is set), shows the server name, protocol version, capabilities and the instructions it
gives to models, then builds a form for every tool from its input schema. Search results come back as
a list with a "Fetch this page" button, fetched pages as their Markdown, and every JSON-RPC request
and response is logged with its timing.

The requests are the real thing: same endpoint, same rate limit, same API key check as any MCP client.
The key stays in the page and is not stored. The playground always calls the endpoint on its own
origin, so it needs no CORS settings. It needs JavaScript; the server home page covers clients for
anyone without it.

### 7.10 Telemetry

The server is instrumented with OpenTelemetry (incoming requests, outgoing HTTP calls, runtime
metrics) plus its own signals:

| Signal | Name | Tags |
|---|---|---|
| Counter | `mcpmysite.search.requests` | `channel` (`mcp` or `api`), `fallback` (`true` when keyword-only) |
| Histogram | `mcpmysite.search.duration` (ms) | same |
| Counter | `mcpmysite.fetch.requests` | `channel`, `found` |
| Span | `mcpmysite.search` | `mcpmysite.channel`, `mcpmysite.limit`, `mcpmysite.fallback`, `mcpmysite.hits`, `mcpmysite.query` (only with `Telemetry:LogQueryText`) |
| Span | `mcpmysite.fetch` | `mcpmysite.channel`, `mcpmysite.found` |

Export: set `APPLICATIONINSIGHTS_CONNECTION_STRING` for Application Insights, or
`OTEL_EXPORTER_OTLP_ENDPOINT` for any OTLP collector (Grafana, Honeycomb, Jaeger and so on). With
neither set, telemetry stays local.

Example Application Insights queries (adjust table names to your workspace):

```kusto
// Searches per hour by channel, and how many fell back to keyword search
customMetrics
| where name == "mcpmysite.search.requests"
| extend channel = tostring(customDimensions.channel), fallback = tostring(customDimensions.fallback)
| summarize requests = sum(value) by bin(timestamp, 1h), channel, fallback
```

```kusto
// Slow searches
dependencies
| where name == "mcpmysite.search"
| summarize p50 = percentile(duration, 50), p95 = percentile(duration, 95) by bin(timestamp, 1h), tostring(customDimensions["mcpmysite.channel"])
```

Query text is not logged or tagged unless `Telemetry:LogQueryText` is `true`.

## 8. Connecting MCP clients

The endpoint is `https://<your-host>/mcp` (or whatever `Mcp:Path` is). Use the streamable HTTP
transport. Examples below assume no API key; where a key is set, add the header shown. The server's
landing page (section 7.8) shows the same setup with its own URL filled in.

**Claude Code**

```bash
claude mcp add --transport http mysite https://example.com/mcp
# with an API key
claude mcp add --transport http mysite https://example.com/mcp --header "Authorization: Bearer <key>"
```

**Visual Studio Code** (`.vscode/mcp.json`)

```json
{
  "servers": {
    "mysite": {
      "type": "http",
      "url": "https://example.com/mcp",
      "headers": { "Authorization": "Bearer <key>" }
    }
  }
}
```

**Cursor** (`.cursor/mcp.json`)

```json
{ "mcpServers": { "mysite": { "url": "https://example.com/mcp" } } }
```

**Claude.ai and Claude Desktop custom connectors.** Add the URL as a custom connector in Settings.
These clients cannot send a static header, so leave `Mcp:ApiKey` unset for them and rely on the rate
limit; this is the public mode the spec describes.

**In the browser.** The server's own `/playground` page (section 7.9) lists the tools and calls them,
which covers most quick checks. For deeper debugging use the **MCP Inspector**:

```bash
npx @modelcontextprotocol/inspector
```

Choose the Streamable HTTP transport, enter the URL, connect, and call `search` and `fetch` by hand.

**curl**, to see the raw protocol:

```bash
curl -s https://example.com/mcp \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"pricing","limit":3}}}'
```

## 9. Adding a search box to your site

1. Set `SearchApi:AllowedOrigins` to your site's origin, exactly as the browser sends it: scheme and
   host, no path, no trailing slash (`https://example.com`). Add `https://www.example.com` too if both
   are served.
2. Call `/api/search` on submit and render the results. The server already escapes
   `highlightedSnippet`; escape `title` yourself.

```html
<form id="site-search">
  <input name="q" type="search" placeholder="Search this site" required>
  <button type="submit">Search</button>
</form>
<ol id="search-results"></ol>

<script>
  const form = document.getElementById('site-search');
  const list = document.getElementById('search-results');
  const endpoint = 'https://search.example.com/api/search';

  form.addEventListener('submit', async (event) => {
    event.preventDefault();
    const q = new FormData(form).get('q').trim();
    if (!q) return;
    list.innerHTML = '<li>Searching…</li>';
    try {
      const response = await fetch(`${endpoint}?q=${encodeURIComponent(q)}&limit=10`);
      if (!response.ok) throw new Error(response.status);
      const data = await response.json();
      list.innerHTML = data.results.length
        ? data.results.map(r =>
            `<li><a href="${r.url}">${escapeHtml(r.title)}</a><p>${r.highlightedSnippet}</p></li>`).join('')
        : '<li>No results.</li>';
    } catch {
      list.innerHTML = '<li>Search is unavailable right now.</li>';
    }
  });

  function escapeHtml(text) {
    return text.replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  }
</script>
```

Style `mark` to match your site. Because requests are rate limited per visitor IP, do not fire a
request on every keystroke.

## 10. Deployment

### 10.1 Where the index lives

Three patterns work; pick the one that matches how you deploy the site.

| Pattern | How | When |
|---|---|---|
| Next to the site | Publish `index/` as part of the static site (for example at `/index/`), set `Index:Url` to that folder | Static sites on any host or CDN. The index deploys with the content it describes. |
| Object storage | Upload the folder to blob storage or S3 with public read, set `Index:Url` | Sites that are not static, or when the index should not be public on the main domain. |
| Baked into the server | Copy the folder into the container image or the app's file system, set `Index:Path` | Simplest, but every index update is a server deploy. |

Everything in the index is content that was public on the site anyway, so serving it from a public
URL does not expose anything new.

### 10.2 Azure App Service (reference deployment)

Push the image to a registry you control (Azure Container Registry or GitHub Container Registry),
then:

```bash
az group create --name mcp-my-site --location canadacentral
az appservice plan create --resource-group mcp-my-site --name mcp-my-site-plan --is-linux --sku B1
az webapp create --resource-group mcp-my-site --plan mcp-my-site-plan --name <app-name> \
  --deployment-container-image-name ghcr.io/<you>/mcp-my-site:latest

az webapp config appsettings set --resource-group mcp-my-site --name <app-name> --settings \
  WEBSITES_PORT=8080 \
  Index__Url=https://example.com/index/ \
  SearchApi__AllowedOrigins__0=https://example.com \
  APPLICATIONINSIGHTS_CONNECTION_STRING="<connection string>" \
  AI_API_KEY="@Microsoft.KeyVault(SecretUri=https://<vault>.vault.azure.net/secrets/ai-api-key/)"
```

Notes:

- App Service terminates TLS and sets `X-Forwarded-For`; keep `RateLimiting:UseForwardedHeaders` at
  its default `true`.
- Use a Key Vault reference (as above) or the portal for the key rather than a plain value in a
  script that might be committed.
- Add `Mcp__ApiKey` the same way if you want the MCP endpoint keyed.
- Azure API Management in front is optional. It adds OAuth, quotas and analytics; the built-in rate
  limit and API key cover the small profile.
- A Bicep template for this setup is planned; until then the commands above are the reference.

### 10.3 Building the index in CI

Build the index where the site is built, with the key as a secret, and publish the output folder. The
example keeps the indexer configuration in `mcp/appsettings.json`, so the cache and the index land in
`mcp/`. Cache `mcp/.mcpmysite/` between runs so unchanged chunks are not re-embedded.

```yaml
name: Build site index
on:
  push:
    branches: [main]
  schedule:
    - cron: "0 3 * * *"
  workflow_dispatch:

jobs:
  index:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - uses: actions/checkout@v4
        with:
          repository: pournasserian/mcp-my-site
          path: .mcp-my-site

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.0.x

      - uses: actions/cache@v4
        with:
          path: mcp/.mcpmysite
          key: embeddings-${{ github.run_id }}
          restore-keys: embeddings-

      - name: Build index
        env:
          AI_API_KEY: ${{ secrets.AI_API_KEY }}
        run: dotnet run --project .mcp-my-site/src/McpMySite.Indexer -c Release -- build --config mcp/appsettings.json

      - name: Publish index
        run: az storage blob upload-batch --account-name <account> --destination '$web/index' --source mcp/index --overwrite
```

The build step exits non-zero when a safety check fails, so a broken crawl stops the workflow before
the publish step and the server keeps serving the previous index.

### 10.4 Other hosts

The server is a plain ASP.NET Core container with no Azure dependency. It runs on Azure Container
Apps, AWS App Runner or ECS, Google Cloud Run, Fly.io, a VM with Docker, or Kubernetes. Give it the
environment variables from section 7.2, expose port 8080, point `/healthz` at the platform's health
probe, and make sure the platform sets `X-Forwarded-For` (or set `RateLimiting:UseForwardedHeaders`
to `false`).

## 11. Operations

**Updating the index.** Rebuild, publish, wait for the poll interval (default five minutes). Check
`/healthz`: `builtAt` moves to the new build time. Nothing restarts.

**Rolling back.** Publish the previous index folder again (keep the last few builds in storage). The
server treats any change in build time or page count as new and loads it.

**Changing the embedding model.** Rebuild with the new `Embeddings:Model`/`Embeddings:Dimensions`; everything is
re-embedded once. Deploy the index; the server reads the model from the manifest and switches query
embedding automatically. The server's endpoint and key must be able to reach the new model.

**Scaling.** The server is stateless; run as many instances as you like. Each instance loads the index
into memory, so size instances by index size rather than request volume. A few hundred pages fit
comfortably in the smallest App Service plan.

**Monitoring.** Watch `/healthz` for `503` and for a `lastError` that persists, the `fallback=true`
share of `mcpmysite.search.requests` (your embedding provider is failing), `429` counts (limit too
low or proxy misconfigured), and `report.json` in your CI artifacts for rising `lowTextPages` or
`errors`.

**Costs.** Embedding is cents per rebuild (section 5.4). Query embeddings are negligible. The server's
cost is the hosting plan.

## 12. Security

- **Public by default.** The MCP endpoint and the search API are meant for public use and only
  expose content that was public on the site. Protection is the per-IP rate limit, the optional MCP
  API key, and the CORS allowlists for browsers.
- **Read-only.** The tools cannot change anything. There are no write paths in the server.
- **Secrets.** Keys are read from environment variables. Nothing in the index, the config files or the
  logs contains a key. `Embeddings:ApiKey` exists for user secrets and environment variables, not for files in the repository.
- **Query privacy.** Query text is not logged or exported unless `Telemetry:LogQueryText` is on.
- **Injection safety.** Snippets are HTML-escaped (only `<mark>` is added). SQLite FTS queries are
  built from quoted tokens, never from raw input. JSON-RPC errors do not include stack traces.
- **Crawling politely.** The indexer identifies itself, obeys `robots.txt` and `noindex`, retries only
  once, and lets you add a delay. Index only sites you own or are allowed to index.
- **Forwarded headers.** See section 7.6. The one setting that can weaken the rate limit is trusting
  `X-Forwarded-For` when there is no proxy.
- **Dependencies.** The server has no Azure dependency beyond the optional telemetry exporter; the
  core takes none.

## 13. Making your site index well

The extractor is automatic by design, so the lever is your markup. A page indexes best when it has:

- a single `<main>` around the content, with the article in an `<article>` if there is sidebar
  material inside `main`;
- navigation in `<nav>`, related links in `<aside>`, page chrome in `<header>`/`<footer>`;
- a descriptive `<title>` (a trailing ` | Site name` is fine when `og:site_name` matches),
  `<meta name="description">` and `<html lang="...">`;
- `article:modified_time` or `article:published_time` meta tags, or `<time datetime>` in the article,
  so answers can mention dates and the search API can show them;
- real headings (`h2`, `h3`) that describe the sections. They become part of what is embedded;
- a sitemap with `<lastmod>`, listing only canonical pages;
- `noindex` on pages that should not be found (search result pages, tag archives, drafts), or an
  `Crawl:Exclude` glob in the indexer config.

After the first build, read `report.json`: pages under `auto` content selection or in `lowTextPages`
are the ones to look at. Run `mcpmysite search` with a few real questions and check that the right
page comes first.

## 14. Troubleshooting

### Indexer

**`No appsettings.json found at ... Run 'mcpmysite init <site-url>' or pass --site-url.`**
You are in the wrong folder or have not created the config yet. Run `mcpmysite init <url>` or pass
`--config path/to/appsettings.json`.

**`Site:Url must be an absolute http(s) URL.`**
The setting is missing or malformed in the layer that won. Check `appsettings.json`, any
`appsettings.{Environment}.json` that applies, and a stray `Site__Url` environment variable.

**`No embedding API key. Set the AI_API_KEY environment variable.`**
The variable named by `Embeddings:ApiKeyEnvironmentVariable` is not set in this shell or CI job. The
indexer refuses to run without it because hybrid search needs vectors. Section 5.2.

**`Only 0 page(s) indexed; the minimum is 1.`**
Nothing was fetched or everything was dropped. Run with `--verbose` and check: does
`/sitemap.xml` exist and list URLs on the same host as `Site:Url`? Does `robots.txt` disallow the
crawler? Are `Crawl:Include`/`Crawl:Exclude` too strict? Does every page carry `noindex`? Is the site rendered by
JavaScript (the fetched HTML has no content)? Is the host blocking the user agent (`403` errors in
`report.json`)?

**`Page count fell from 120 to 40 (67%), more than the allowed 50%. The previous index was kept.`**
Either the site really shrank, or the crawl failed partway. Check `errors` in `report.json`. If the
drop is intended, raise `Safety:MaxPageDropPercent` for one run or delete the old `manifest.json`
from the output folder.

**`N of M pages failed (x%), more than the allowed 20%.`**
Look at `errors` in `report.json`. Timeouts: raise `Crawl:TimeoutSeconds`, lower `Crawl:Concurrency`,
add `Crawl:DelayMilliseconds`. `403`/`429` from the host: it is blocking the crawler; use the folder
source or allow the user agent.

**`Provider returned 1536 dimensions; 512 were requested.`**
The model ignores the `Embeddings:Dimensions` parameter. Set `Embeddings:Dimensions` to the model's native size.

**`Embedding request failed (attempt 1): ... Retrying in 2s`**
Transient provider error; the indexer retries three times with backoff. If it keeps failing, check the
key, the endpoint URL (it must end with `/v1` for OpenAI-style gateways) and your provider's status.

**Many pages in `lowTextPages`.**
The content was not found or is thin. Check `contentSources`: `auto` means no `<main>`/`<article>`;
add them. If the pages are rendered by JavaScript, use the folder source with prerendered output.

**Titles are wrong or include the site name.**
Add `<meta property="og:site_name">` so the suffix can be recognised, or make `<title>` the page title
alone.

**Build is slow.**
Embedding dominates the first run; later runs hit the cache. Make sure `Cache:Enabled` is on and the
cache folder persists between CI runs (section 10.3). Crawling: raise `Crawl:Concurrency` moderately.

### Server

**`/healthz` returns 503 with `"status": "no-index"`.**
Read `lastError`. Typical causes: `Index:Path`/`Index:Url` not set or wrong (a relative `Index:Path`
resolves from the folder the server was started in; `lastError` shows the full path it tried);
the URL is not reachable from the server; the manifest fails validation (unsupported format version,
zero pages); data files disagree with the manifest (partial upload: publish the whole folder,
manifest last).

**A new index was published but `builtAt` has not changed.**
Wait for `Index:PollIntervalSeconds`. If `lastError` is set, the new index failed validation and the
old one is still serving. If the URL is behind a CDN, check that `manifest.json` is not cached longer
than the poll interval.

**`401 {"error":"A valid API key is required."}`**
`Mcp:ApiKey` is set and the client did not send it. Send `Authorization: Bearer <key>` or
`X-Api-Key: <key>`, or unset the key for clients that cannot send headers.

**`406 Not Acceptable` on `/mcp`.**
The client did not send `Accept: application/json, text/event-stream`. Real MCP clients do; add the
header to hand-written requests.

**`429 Too Many Requests`.**
The per-IP limit (`RateLimiting:PermitPerMinute`) was exceeded. If everyone is being throttled at
once, you are behind a proxy with `RateLimiting:UseForwardedHeaders` set to `false`; set it to `true`.

**Browser console: blocked by CORS policy.**
Add the page's origin to `SearchApi:AllowedOrigins` exactly (`https://example.com`, no trailing
slash, no path). `www` and non-`www` are different origins.

**Every search response has `"keywordOnly": true`.**
Query embedding is failing: no key on the server, wrong `Embeddings:Endpoint`, or the provider is
down. Results still come from keyword search. Check the server log for
"Embedding failed; answering from keyword search".

**`Index format version 2 is not supported.`**
The index was built by a newer indexer than the server understands. Update the server, or rebuild
with a matching indexer. The server accepts the current version and, once there is one, the previous.

**`Query vector has 768 dimensions; the index uses 512.`**
Should not happen in normal use, since the server embeds with the manifest's model; it indicates a
gateway that ignores `Embeddings:Dimensions`. Rebuild with the model's native size.

## 15. FAQ

**Why is search always hybrid? Can I run without an embedding provider?**
No. Keyword-only search misses paraphrases, vector-only search misses exact names and codes; the
product is the combination. The only keyword-only path is the per-request fallback when the provider
fails at query time, and that is recorded so you notice.

**Which embedding models can I use?**
Any model behind an OpenAI-compatible `/embeddings` endpoint, whether a gateway, a provider or a
local server (section 5.3). The documented default is
`text-embedding-3-small` at 512 dimensions through OpenRouter. Changing model or dimensions triggers a
full re-embed on the next build; the index records what it was built with and the server follows it.

**Can I index several sites?**
One site per index and per server, by design. Run one server per site (they are small). Multi-site
hosting is out of scope for V1.

**My site is a JavaScript app. Will it work?**
Not from a live crawl: the indexer fetches HTML over plain HTTP and does not run scripts. If your
build can prerender pages to static HTML, index that folder with `Source:Type` set to `Folder`.

**PDFs, Word documents, images?**
Not indexed. HTML only in V1.

**How often should I rebuild?**
On every deploy of the site, or nightly. Rebuilds are full but cheap: the cache means only changed
chunks are embedded, and a 50-page site rebuilds in seconds.

**Is there incremental indexing?**
No; every run is a full rebuild, which is what makes deleted pages disappear reliably. The embedding
cache gives most of the benefit of incremental runs without the bookkeeping.

**How big a site can it handle?**
It is aimed at sites under 1,000 pages. The JSON store is best under a few hundred pages; use SQLite
above that. Vector search is brute force, which is fast at this scale and needs no extra service.

**What does it cost to run?**
Embeddings: cents per rebuild. Hosting: the smallest plan that holds the index in memory. There is no
per-query model call other than the query embedding.

**Can agents see pages that are not public?**
No. The indexer only fetches what any anonymous visitor can fetch, obeys `robots.txt` and `noindex`,
and pages behind a login are out of scope. The server serves only what is in the index.

**Can I tell the extractor which element to use?**
No, by design: there are no selector overrides to maintain or break. Mark up the page with `<main>`
or `<article>` instead (section 13). The report shows which strategy each page used.

**How do I keep certain pages out?**
Any of: `Crawl:Exclude` globs, `noindex` on the page, `Disallow` in `robots.txt`, or leaving them out
of the sitemap when links are not followed.

**Does the server need the API key too?**
Yes, to embed queries. Without it every search falls back to keyword search and is marked
`keywordOnly`. Builds, however, refuse to run without a key.

**What happens when the embedding provider is down?**
Searches still work, from keyword search, and the fallback is counted in telemetry. Index builds
fail rather than produce an index without vectors.

**Is the server stateful? Can I run several instances?**
It is stateless: no sessions, no server-to-client requests. Run as many instances as you want behind
any load balancer.

**How do I authenticate MCP clients?**
V1 offers a static API key (`Mcp:ApiKey`) sent as a bearer token or `X-Api-Key` header, plus per-IP
rate limiting for the public mode. OAuth is not built in; put Azure API Management or another gateway
in front if you need it.

**Can I change what the agent is told about the site?**
Yes. `Instructions:Preamble` in the indexer config prepends text to the generated outline;
`Mcp:Instructions` on the server replaces it entirely.

**Multilingual sites?**
Each page records its language and the embedding model is multilingual. There is no language filter in
search (by design), and the keyword tokenizer is language-neutral with no stemming.

**Can I turn the search API off, or the MCP endpoint?**
`SearchApi:Enabled: false` disables the search API. The MCP endpoint is always on.

**Why one result per page rather than per chunk?**
Pages are what people and agents open. Chunks are ranked, then collapsed to the best chunk per page,
whose text becomes the snippet; `fetch` returns the whole page.

**Is HTML stored?**
No. Pages are stored as Markdown with title, description, language, headings and modified date. No
HTML and no link graph.

**Can the tools modify my site?**
No. Both tools are read-only.

**Does it do search-as-you-type?**
No, and the rate limit is sized for search on submit.

**Is a chat endpoint included?**
Not in V1. An OpenAI-compatible chat API (Chat Completions and Responses, streaming) built on
Microsoft Agent Framework is planned for V2.

**Does it need Azure?**
No. Azure App Service is the reference deployment and Application Insights the default telemetry
target, but the server is a plain container and exports over OTLP to anything.

**What licence?**
MIT.

## 16. Reference

### 16.1 Index layout

```
index/
  manifest.json      always; read first by the server
  report.json        run report, not used by the server
  pages.json         json store: array of pages
  chunks.json        json store: array of chunks
  vectors.bin        json store: float32 little-endian, chunkCount × dimensions, in chunk order
  index.sqlite       sqlite store: tables manifest, pages, chunks (vector BLOB), chunks_fts (FTS5)
```

`manifest.json`:

| Field | Meaning |
|---|---|
| `formatVersion` | Index format version; `1` today |
| `storeType` | `json` or `sqlite` |
| `siteUrl`, `siteName`, `siteDescription` | The site |
| `embeddingModel`, `embeddingDimensions` | How vectors were produced; the server embeds queries the same way |
| `builtAt` | Build time; a change triggers a reload |
| `pageCount`, `chunkCount` | Checked against the data files on load |
| `instructions` | Generated MCP server instructions |
| `indexerVersion` | Indexer that wrote it |
| `stats` | Numbers from the run, for status pages: `source`, `startedAt`, `durationSeconds`, `pagesFetched`, `pagesSkippedNoIndex`, `pagesSkippedDuplicate`, `pagesSkippedEmpty`, `errors`, `embeddingsGenerated`, `embeddingsFromCache`, `words` (approximate), `newestPageModified`, `languages` (pages per language), `sections` (`path`, `title`, `pages`). Absent in indexes from older indexers. |

Page record: `id`, `url`, `title`, `description`, `language`, `headings[]`, `modified`, `markdown`.
Chunk record: `id`, `pageId`, `ordinal`, `headingPath[]`, `text`. Chunk `id` equals its row in
`vectors.bin`.

### 16.2 Exit codes (indexer)

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Build ran and failed (safety check, missing key, crawl or write error) or `init` found an existing file |
| 2 | Invalid or missing configuration |

### 16.3 HTTP endpoints (server)

| Method and path | Purpose | Rate limited | Key |
|---|---|---|---|
| `POST {Mcp:Path}` | MCP JSON-RPC (default `/mcp`) | yes | optional |
| `GET {SearchApi:Path}?q=&limit=` | Search API (default `/api/search`) | yes | no |
| `GET /api/info` | Site, index, build statistics and endpoint URLs as JSON | no | no |
| `GET /openapi/v1.json` | OpenAPI document for the search API and `/api/info` | no | no |
| `GET /docs` | Interactive API reference, when `Ui:Enabled` | no | no |
| `GET /healthz` | Health and index status | no | no |
| `GET /` | Landing page, when `Ui:Enabled`; otherwise a one-line text description | no | no |
| `GET /playground` | MCP playground, when `Ui:Enabled` | no | no |
| `GET /llms.txt` | The indexed pages as a Markdown list for language models, when `Ui:Enabled` | no | no |
| `GET /robots.txt` | Crawler rules | no | no |
| `GET /sitemap.xml` | Pages for search engines, when `Ui:Enabled` | no | no |

### 16.4 Glossary

- **Chunk**: a heading-aware slice of a page, the unit that is embedded and ranked.
- **Hybrid search**: keyword ranking (BM25 or FTS5) and vector ranking (cosine similarity) merged with
  reciprocal rank fusion.
- **Manifest**: the file that describes an index and lets the server validate it before loading.
- **Hot swap**: replacing the in-memory index with a newly loaded one without restarting the server.
- **Fallback**: answering a search from keyword ranking alone because the query could not be embedded.
- **Stateless MCP**: the Streamable HTTP mode without sessions, where every request is independent.
