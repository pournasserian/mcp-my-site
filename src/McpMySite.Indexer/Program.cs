using System.CommandLine;
using System.Text.Json;
using McpMySite.Core;
using McpMySite.Core.Embeddings;
using McpMySite.Core.Model;
using McpMySite.Core.Stores;
using McpMySite.Indexer.Build;
using McpMySite.Indexer.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

var root = new RootCommand("Crawls a website and builds the search index served by the MCP my site server.");
root.Subcommands.Add(BuildCommand());
root.Subcommands.Add(InitCommand());
root.Subcommands.Add(SearchCommand());
return await root.Parse(args).InvokeAsync();

static Command BuildCommand()
{
    var configOption = new Option<string>("--config", "-c") { Description = "Path to the indexer config file.", DefaultValueFactory = _ => IndexerConfig.DefaultFileName };
    var siteUrlOption = new Option<string?>("--site-url") { Description = "Overrides siteUrl." };
    var folderOption = new Option<string?>("--folder") { Description = "Index a local folder of built HTML instead of crawling." };
    var outputOption = new Option<string?>("--output", "-o") { Description = "Overrides output.directory." };
    var storeOption = new Option<string?>("--store") { Description = "Overrides output.store: json or sqlite." };
    var maxPagesOption = new Option<int?>("--max-pages") { Description = "Overrides crawl.maxPages." };
    var verboseOption = new Option<bool>("--verbose", "-v") { Description = "Debug logging." };

    var command = new Command("build", "Builds the index. Every run is a full rebuild.");
    command.Options.Add(configOption);
    command.Options.Add(siteUrlOption);
    command.Options.Add(folderOption);
    command.Options.Add(outputOption);
    command.Options.Add(storeOption);
    command.Options.Add(maxPagesOption);
    command.Options.Add(verboseOption);

    command.SetAction(async (parseResult, cancellationToken) =>
    {
        var configPath = Path.GetFullPath(parseResult.GetValue(configOption)!);
        IndexerConfig config;
        if (File.Exists(configPath))
        {
            config = IndexerConfig.Load(configPath);
        }
        else if (parseResult.GetValue(siteUrlOption) is not null)
        {
            config = new IndexerConfig();
        }
        else
        {
            Console.Error.WriteLine($"Config file not found: {configPath}. Run 'mcpmysite init' or pass --site-url.");
            return 2;
        }

        if (parseResult.GetValue(siteUrlOption) is { } siteUrl) config.SiteUrl = siteUrl;
        if (parseResult.GetValue(folderOption) is { } folder) { config.Source.Type = SourceType.Folder; config.Source.Folder = folder; }
        if (parseResult.GetValue(outputOption) is { } output) config.Output.Directory = output;
        if (parseResult.GetValue(storeOption) is { } store) config.Output.Store = store;
        if (parseResult.GetValue(maxPagesOption) is { } maxPages) config.Crawl.MaxPages = maxPages;

        using var loggerFactory = CreateLoggerFactory(parseResult.GetValue(verboseOption));
        var builder = new IndexBuilder(loggerFactory.CreateLogger<IndexBuilder>());
        var baseDirectory = File.Exists(configPath) ? Path.GetDirectoryName(configPath)! : Directory.GetCurrentDirectory();

        BuildReport report;
        try
        {
            report = await builder.RunAsync(config, baseDirectory, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        PrintSummary(report);
        return report.Succeeded ? 0 : 1;
    });

    return command;
}

static Command InitCommand()
{
    var siteUrlArgument = new Argument<string>("site-url") { Description = "Base URL of the site, e.g. https://example.com" };
    var configOption = new Option<string>("--config", "-c") { Description = "Where to write the config file.", DefaultValueFactory = _ => IndexerConfig.DefaultFileName };
    var storeOption = new Option<string>("--store") { Description = "json or sqlite.", DefaultValueFactory = _ => IndexFormat.JsonStoreType };

    var command = new Command("init", "Writes a starter config file.");
    command.Arguments.Add(siteUrlArgument);
    command.Options.Add(configOption);
    command.Options.Add(storeOption);

    command.SetAction(parseResult =>
    {
        var path = Path.GetFullPath(parseResult.GetValue(configOption)!);
        if (File.Exists(path))
        {
            Console.Error.WriteLine($"{path} already exists.");
            return 1;
        }

        var config = new IndexerConfig { SiteUrl = parseResult.GetValue(siteUrlArgument)! };
        config.Output.Store = parseResult.GetValue(storeOption)!;
        File.WriteAllText(path, JsonSerializer.Serialize(config, IndexerConfig.JsonOptions));
        Console.WriteLine($"Wrote {path}");
        Console.WriteLine($"Set the {config.Embeddings.ApiKeyEnvironmentVariable} environment variable, then run: mcpmysite build");
        return 0;
    });

    return command;
}

static Command SearchCommand()
{
    var queryArgument = new Argument<string>("query") { Description = "Search text." };
    var indexOption = new Option<string>("--index", "-i") { Description = "Index directory.", DefaultValueFactory = _ => "index" };
    var limitOption = new Option<int>("--limit", "-n") { Description = "Number of results.", DefaultValueFactory = _ => 5 };
    var keywordOnlyOption = new Option<bool>("--keyword-only") { Description = "Skip the embedding call and run keyword search only." };
    var endpointOption = new Option<string?>("--endpoint") { Description = "Embedding endpoint; defaults to OpenRouter." };

    var command = new Command("search", "Runs a query against a built index. Handy for checking an index before deploying it.");
    command.Arguments.Add(queryArgument);
    command.Options.Add(indexOption);
    command.Options.Add(limitOption);
    command.Options.Add(keywordOnlyOption);
    command.Options.Add(endpointOption);

    command.SetAction(async (parseResult, cancellationToken) =>
    {
        var query = parseResult.GetValue(queryArgument)!;
        using var store = await IndexStoreOpener.OpenAsync(Path.GetFullPath(parseResult.GetValue(indexOption)!), cancellationToken);

        ReadOnlyMemory<float>? vector = null;
        if (!parseResult.GetValue(keywordOnlyOption))
        {
            var options = new EmbeddingOptions();
            if (parseResult.GetValue(endpointOption) is { } endpoint) options.Endpoint = endpoint;
            var generator = EmbeddingGeneratorFactory.Create(options, store.Manifest.EmbeddingModel, store.Manifest.EmbeddingDimensions);
            vector = await generator.GenerateVectorAsync(query, cancellationToken: cancellationToken);
        }

        var response = await store.SearchAsync(new SearchRequest { Query = query, QueryVector = vector, Limit = parseResult.GetValue(limitOption) }, cancellationToken);
        Console.WriteLine($"{response.Hits.Count} result(s){(response.KeywordOnly ? " (keyword only)" : string.Empty)} for \"{query}\"");
        foreach (var hit in response.Hits)
        {
            Console.WriteLine();
            Console.WriteLine($"{hit.Score:F4}  {hit.Title}");
            Console.WriteLine($"        {hit.Url}");
            if (hit.HeadingPath.Count > 0) Console.WriteLine($"        {string.Join(" > ", hit.HeadingPath)}");
            Console.WriteLine($"        {hit.Snippet}");
        }

        return 0;
    });

    return command;
}

static ILoggerFactory CreateLoggerFactory(bool verbose) => LoggerFactory.Create(logging =>
{
    logging.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information);
    logging.AddSimpleConsole(o =>
    {
        o.SingleLine = true;
        o.TimestampFormat = "HH:mm:ss ";
    });
    logging.AddFilter("System.Net.Http", LogLevel.Warning);
});

static void PrintSummary(BuildReport report)
{
    Console.WriteLine();
    Console.WriteLine(report.Succeeded ? "Index built." : $"Index NOT built: {report.FailureReason}");
    Console.WriteLine($"  pages fetched {report.PagesFetched}, indexed {report.PagesIndexed}, noindex {report.PagesSkippedNoIndex}, duplicates {report.PagesSkippedDuplicate}, empty {report.PagesSkippedEmpty}");
    Console.WriteLine($"  chunks {report.Chunks}, embeddings generated {report.EmbeddingsGenerated}, from cache {report.EmbeddingsFromCache}");
    Console.WriteLine($"  errors {report.Errors.Count} ({report.ErrorPercent:F0}%)");
    if (report.LowTextPages.Count > 0)
    {
        Console.WriteLine($"  {report.LowTextPages.Count} page(s) with little text:");
        foreach (var page in report.LowTextPages.Take(10))
        {
            Console.WriteLine($"    {page.TextLength,5} chars  {page.Url}");
        }
    }

    foreach (var error in report.Errors.Take(10))
    {
        Console.WriteLine($"  error: {error}");
    }

    Console.WriteLine($"  report: {IndexFormat.ReportFileName} in the output folder");
}
