# mcpmysite

Command-line indexer for [mcp-my-site](https://github.com/pournasserian/mcp-my-site). It crawls a
website, chunks the text, embeds it through any OpenAI-compatible endpoint and writes a hybrid
keyword and vector index. The companion server (`ghcr.io/pournasserian/mcp-my-site`) serves that
index to ChatGPT, Claude and any other MCP client, and as a JSON search API.

```bash
dotnet tool install --global mcpmysite
export AI_API_KEY=sk-...          # key for your embeddings provider, OpenRouter by default
mcpmysite init https://example.com   # writes appsettings.json
mcpmysite build                      # crawls the site into ./index
mcpmysite search "how can I contact you"
```

Requires the .NET 10 SDK or runtime. The API key is read from the environment and never written
to a file. Every setting, other providers and running the server are covered in the
[user guide](https://github.com/pournasserian/mcp-my-site/blob/master/docs/user-guide.md).
