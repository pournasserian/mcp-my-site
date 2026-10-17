# Changelog

All notable changes to this project are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

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

[Unreleased]: https://github.com/pournasserian/mcp-my-site/compare/v0.2.1...master
[0.2.1]: https://github.com/pournasserian/mcp-my-site/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/pournasserian/mcp-my-site/compare/v0.1.2...v0.2.0
[0.1.2]: https://github.com/pournasserian/mcp-my-site/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/pournasserian/mcp-my-site/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/pournasserian/mcp-my-site/releases/tag/v0.1.0
