# Changelog

All notable changes to this project are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- `GET {Mcp:Path}/server-card` serves the MCP server card of SEP-2127 as
  `application/mcp-server-card+json`: the registry name, version and repository, the site's name,
  and one `remotes` entry with the public MCP URL, the supported protocol versions and the
  `Authorization` header when `Mcp:ApiKey` is set. Cached for an hour with an `ETag`, open to every
  origin, rate limited, and reachable without the key (#53).
- The search API and the MCP `search` tool page through results: `offset` skips that many pages
  of the same ranking, capped by the new `SearchApi:MaxOffset` setting (default 100), and every
  response carries `total` and `hasMore` (#62).
- `Telemetry:Metrics` names the metric groups the server exports (`Server`, `AspNetCore`,
  `HttpClient`, `Runtime`; default all), so a deployment can drop the HTTP client and runtime series
  that make up most of what a workspace ingests. The startup line names the groups in use (#46).
- The MCP SDK's spans and the server's `mcpmysite.search` and `mcpmysite.fetch` spans and counters
  carry `mcp.client.name`, `mcp.client.version` and `mcp.protocol.version` from the client's
  `clientInfo`; on the stateless transport the client is known on `initialize` and, from the
  `2026-07-28` revision, on every request (#44).
- `Telemetry:VisitorId` (default `false`) tags every request, search and fetch with
  `mcpmysite.visitor`, a hash of the client address and user agent with a random salt that is
  replaced daily and never stored, so a dashboard can count distinct visitors without the server
  keeping addresses (#45).

### Removed

- The browser playground at `/playground`. The landing page's new "Test the server" section points
  at the MCP Inspector instead, with the server's URL and header filled in and a CLI line for a
  scripted check. `/playground` redirects to that section for now; the redirect goes away in a later
  release (#61).

### Changed

- The architecture document describes `ResponseHeadersMiddleware` and `HeadSupport`, the response
  headers and `HEAD` handling, and lists the `Pages/` folder and the `Api/` files that had been added
  since it was written. The user guide names the current version instead of "0.1 (V1)" and uses the
  current image tag in its examples. The indexer's default `Crawl:UserAgent` is now
  `McpMySiteIndexer/0.2` (#63).
- The user guide gives the current reason for the 2,000-character cap on server instructions:
  Claude Code cuts them at 4,096 characters (2,048 before v2.1.296), so the cap keeps the text
  whole in both (#54).

### Fixed

- A heading that is a link, or carries inline code or emphasis, enters a chunk's heading path as
  plain text instead of Markdown source, so the `section` of a search result reads `FluentCMS`
  rather than `[FluentCMS](https://...)` (#52).
- `server/discover` on the `2026-07-28` revision no longer advertises the `logging` capability that
  revision retires (MCP SDK 2.3.0). `initialize` still lists it, because the SDK answers
  `logging/setLevel` on every server; the user guide now says so (#55).

### Security

- Every response carries `Content-Security-Policy: frame-ancestors 'none'`, `X-Frame-Options: DENY`,
  `X-Content-Type-Options: nosniff` and `Referrer-Policy: strict-origin-when-cross-origin`, so the
  pages can no longer be framed by another site. `Ui:FrameAncestors` lists origins that may embed
  them (#27).

## [0.2.2] - 2026-10-17

### Fixed

- Every page and JSON endpoint answers `HEAD` like `GET`, without a body, instead of `405`; link
  checkers, link previews and uptime monitors that send `HEAD` first no longer see the server as
  broken (#47). The OpenAPI document still describes the `GET` operations only.
- A browser opening the MCP endpoint's URL is redirected to the landing page instead of getting a
  bare `405`, while `Ui:Enabled` is on. Requests that ask for `text/event-stream` reach the
  transport as before (#48).

## [0.2.1] - 2026-10-17

### Added

- `server.json` at the repository root, the manifest for the project's listing in the official MCP
  registry, and the `io.modelcontextprotocol.server.name` label on the server image that the
  registry uses to verify the listing.

## [0.2.0] - 2026-10-16

### Added

- Keyword-only mode: `mcpmysite init <url> --keyword-only` (or `Embeddings:Enabled` set to `false`)
  builds an index without vectors, so a first try needs no embeddings provider or API key. The
  manifest records it, and the server serves such an index from keyword search and says so on the
  landing page, in `/healthz` and `/api/info`, and in the `search` tool description.

## [0.1.2] - 2026-10-16

### Added

- Release workflow that publishes the indexer to NuGet.org as the .NET tool `mcpmysite` on every
  version tag, so it installs with `dotnet tool install --global mcpmysite`.

## [0.1.1] - 2026-10-16

### Added

- Release workflow that publishes the server image to GitHub Container Registry as
  `ghcr.io/pournasserian/mcp-my-site` on every version tag.
- The server image is built for `linux/arm64` as well as `linux/amd64`, cross-compiled by the SDK
  rather than emulated.

## [0.1.0] - 2026-10-16

First public release.

### Added

- Indexer CLI (`init`, `build`, `search`) that crawls a site, extracts and chunks the text, embeds
  it through any OpenAI-compatible endpoint and writes a JSON or SQLite index.
- ASP.NET Core server exposing the index as an MCP server (`search` and `fetch` tools, streamable
  HTTP) and as a JSON search API with CORS and rate limiting.
- Hybrid search: BM25 keyword ranking fused with vector search by reciprocal rank fusion, with a
  keyword-only fallback when the embedding provider is unavailable.
- Server instructions generated from the indexed site, with an optional preamble or override.
- Index hot swap from a local folder or a URL, with manifest validation.
- Landing page, MCP playground, OpenAPI 3.1 document with a Scalar reference, `llms.txt`,
  `sitemap.xml` and `robots.txt`.
- OpenTelemetry traces, metrics and logs with Application Insights and OTLP exporters.
- Dockerfile for the server and a CI workflow that builds and tests on Linux and Windows.
- README, user guide and architecture documentation.

[Unreleased]: https://github.com/pournasserian/mcp-my-site/compare/v0.2.2...master
[0.2.2]: https://github.com/pournasserian/mcp-my-site/compare/v0.2.1...v0.2.2
[0.2.1]: https://github.com/pournasserian/mcp-my-site/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/pournasserian/mcp-my-site/compare/v0.1.2...v0.2.0
[0.1.2]: https://github.com/pournasserian/mcp-my-site/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/pournasserian/mcp-my-site/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/pournasserian/mcp-my-site/releases/tag/v0.1.0
