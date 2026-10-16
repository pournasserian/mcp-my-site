# Changelog

All notable changes to this project are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

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

[Unreleased]: https://github.com/pournasserian/mcp-my-site/commits/master
