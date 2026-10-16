# mcpmysite

Command-line indexer for [mcp-my-site](https://github.com/pournasserian/mcp-my-site). It crawls a
website, chunks the text and writes a search index: keyword-only with no API key, or hybrid keyword
and vector with embeddings from any OpenAI-compatible endpoint. The companion server (`ghcr.io/pournasserian/mcp-my-site`) serves that
index to ChatGPT, Claude and any other MCP client, and as a JSON search API.

```bash
dotnet tool install --global mcpmysite
mcpmysite init https://example.com --keyword-only   # writes appsettings.json; no API key needed
mcpmysite build                                     # crawls the site into ./index
mcpmysite search "how can I contact you"
```

Requires the .NET 10 SDK or runtime. For hybrid search, set `Embeddings:Enabled` to `true` in
`appsettings.json` and put your provider's key in `AI_API_KEY` (OpenRouter by default); the key is
read from the environment and never written to a file. Every setting, other providers and running
the server are covered in the
[user guide](https://github.com/pournasserian/mcp-my-site/blob/master/docs/user-guide.md).
