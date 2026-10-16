# Contributing

Thanks for taking an interest. This page covers how to build, test and send changes.

## Before you start

- **Ideas and questions** go to [Discussions](https://github.com/pournasserian/mcp-my-site/discussions).
  Open one before a large change so the approach is agreed first.
- **Bugs** go to [Issues](https://github.com/pournasserian/mcp-my-site/issues) using the bug template.
- **Small fixes** (typos, a wrong default in the docs, a one-line bug) can go straight to a pull request.

## Build and test

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). Docker is optional and only
needed for the server image.

```bash
dotnet build McpMySite.slnx
dotnet test tests/McpMySite.Tests
```

The tests need no API key and no network. The indexer and server need `AI_API_KEY` to run against a
real site; see [the user guide](docs/user-guide.md#5-the-embedding-provider-and-your-api-key).

On Windows, keep the checkout in a short path (for example `C:\Projects`). Deep paths can make the
SQLite tests fail with `DllNotFoundException`.

## Making a change

1. Fork the repository and create a branch from `master`.
2. Make the change. Match the existing style: plain C# with nullable enabled, no XML doc comments,
   comments only where they explain why.
3. Add or update tests when behaviour changes. Tests live in `tests/McpMySite.Tests`, mirroring the
   source layout.
4. Update `docs/user-guide.md` if a setting, command or endpoint changed, and add a line under
   **Unreleased** in `CHANGELOG.md`.
5. Run `dotnet build` and `dotnet test` and make sure both pass.
6. Open a pull request against `master`. Describe what changed and why in a few sentences. CI builds
   and tests on Linux and Windows and builds the container image.

Commits are short and imperative ("Add FTS5 injection test"). `master` is protected and only
changes through pull requests.

## What not to commit

`.env` files, API keys, built indexes and anything under `index/`. The `.gitignore` covers these,
but check `git status` before you push.

## Licence

By contributing you agree that your contributions are licensed under the [MIT licence](LICENSE).
