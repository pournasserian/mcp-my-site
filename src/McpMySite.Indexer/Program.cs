using McpMySite.Core.Stores;
using McpMySite.Indexer.Build;
using Microsoft.Extensions.AI;
using System.CommandLine;

var root = new RootCommand("Crawls a website and builds the search index served by the MCP my site server.");
root.Subcommands.Add(BuildCommand());
root.Subcommands.Add(InitCommand());
root.Subcommands.Add(SearchCommand());
return await root.Parse(args).InvokeAsync();

static Option<string> ConfigOption() => new("--config", "-c")
{
    Description = "Path to appsettings.json. appsettings.{Environment}.json next to it is applied on top.",
    DefaultValueFactory = _ => IndexerConfig.DefaultFileName,
};

static Option<string?> EnvironmentOption() => new("--environment", "-e")
{
    Description = "Environment name (default: DOTNET_ENVIRONMENT, ASPNETCORE_ENVIRONMENT, then Production).",
};

static Option<string[]> SetOption() => new("--set")
{
    Description = "Override any setting: --set Crawl:MaxPages=50. Repeatable.",
};

static Command BuildCommand()
{
    var configOption = ConfigOption();
    var environmentOption = EnvironmentOption();
    var setOption = SetOption();
    var siteUrlOption = new Option<string?>("--site-url") { Description = "Overrides Site:Url." };
    var folderOption = new Option<string?>("--folder") { Description = "Index a local folder of built HTML instead of crawling." };
    var outputOption = new Option<string?>("--output", "-o") { Description = "Overrides Output:Directory." };
    var storeOption = new Option<string?>("--store") { Description = "Overrides Output:Store: json or sqlite." };
    var maxPagesOption = new Option<int?>("--max-pages") { Description = "Overrides Crawl:MaxPages." };
    var verboseOption = new Option<bool>("--verbose", "-v") { Description = "Debug logging." };

    var command = new Command("build", "Builds the index. Every run is a full rebuild.");
    foreach (var option in new Option[] { configOption, environmentOption, setOption, siteUrlOption, folderOption, outputOption, storeOption, maxPagesOption, verboseOption })
    {
        command.Options.Add(option);
    }

    command.SetAction(async (parseResult, cancellationToken) =>
    {
        IndexerConfigLoader.Loaded loaded;
        try
        {
            loaded = IndexerConfigLoader.Load(parseResult.GetValue(configOption), parseResult.GetValue(environmentOption), parseResult.GetValue(setOption));
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        var config = loaded.Config;
        if (parseResult.GetValue(siteUrlOption) is { } siteUrl) config.Site.Url = siteUrl;
        if (parseResult.GetValue(folderOption) is { } folder) { config.Source.Type = SourceType.Folder; config.Source.Folder = folder; }
        if (parseResult.GetValue(outputOption) is { } output) config.Output.Directory = output;
        if (parseResult.GetValue(storeOption) is { } store) config.Output.Store = store;
        if (parseResult.GetValue(maxPagesOption) is { } maxPages) config.Crawl.MaxPages = maxPages;

        if (loaded.ConfigFile is null && string.IsNullOrWhiteSpace(config.Site.Url))
        {
            Console.Error.WriteLine($"No {IndexerConfig.DefaultFileName} found at {Path.GetFullPath(parseResult.GetValue(configOption)!)}. Run 'mcpmysite init <site-url>' or pass --site-url.");
            return 2;
        }

        using var loggerFactory = CreateLoggerFactory(loaded, parseResult.GetValue(verboseOption));
        var logger = loggerFactory.CreateLogger<IndexBuilder>();
        logger.LogInformation("Environment {Environment}; config {Config}", loaded.EnvironmentName, loaded.ConfigFile ?? "(defaults and overrides only)");

        BuildReport report;
        try
        {
            report = await new IndexBuilder(logger).RunAsync(config, loaded.BaseDirectory, cancellationToken);
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
    var configOption = new Option<string>("--config", "-c") { Description = "Where to write the file.", DefaultValueFactory = _ => IndexerConfig.DefaultFileName };
    var storeOption = new Option<string>("--store") { Description = "json or sqlite.", DefaultValueFactory = _ => IndexFormat.JsonStoreType };

    var command = new Command("init", "Writes a starter appsettings.json with every setting and its default.");
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

        var config = new IndexerConfig();
        config.Site.Url = parseResult.GetValue(siteUrlArgument)!;
        config.Output.Store = parseResult.GetValue(storeOption)!;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, IndexerConfigLoader.ToAppSettingsJson(config));
        Console.WriteLine($"Wrote {path}");
        Console.WriteLine($"Set the {config.Embeddings.ApiKeyEnvironmentVariable} environment variable, then run: mcpmysite build --config {parseResult.GetValue(configOption)}");
        return 0;
    });

    return command;
}

static Command SearchCommand()
{
    var queryArgument = new Argument<string>("query") { Description = "Search text." };
    var configOption = ConfigOption();
    var environmentOption = EnvironmentOption();
    var indexOption = new Option<string?>("--index", "-i") { Description = "Index directory. Defaults to Output:Directory from the config, else ./index." };
    var limitOption = new Option<int>("--limit", "-n") { Description = "Number of results.", DefaultValueFactory = _ => 5 };
    var keywordOnlyOption = new Option<bool>("--keyword-only") { Description = "Skip the embedding call and run keyword search only." };

    var command = new Command("search", "Runs a query against a built index. Handy for checking an index before deploying it.");
    command.Arguments.Add(queryArgument);
    foreach (var option in new Option[] { configOption, environmentOption, indexOption, limitOption, keywordOnlyOption })
    {
        command.Options.Add(option);
    }

    command.SetAction(async (parseResult, cancellationToken) =>
    {
        var loaded = IndexerConfigLoader.Load(parseResult.GetValue(configOption), parseResult.GetValue(environmentOption));
        var indexDirectory = parseResult.GetValue(indexOption) is { } explicitIndex
            ? Path.GetFullPath(explicitIndex)
            : Path.GetFullPath(Path.Combine(loaded.BaseDirectory, loaded.Config.Output.Directory));

        var query = parseResult.GetValue(queryArgument)!;
        using var store = await IndexStoreOpener.OpenAsync(indexDirectory, cancellationToken);

        ReadOnlyMemory<float>? vector = null;
        if (!parseResult.GetValue(keywordOnlyOption))
        {
            var generator = EmbeddingGeneratorFactory.Create(loaded.Config.Embeddings, store.Manifest.EmbeddingModel, store.Manifest.EmbeddingDimensions);
            vector = await generator.GenerateVectorAsync(query, cancellationToken: cancellationToken);
        }

        var response = await store.SearchAsync(new SearchRequest { Query = query, QueryVector = vector, Limit = parseResult.GetValue(limitOption) }, cancellationToken);
        Console.WriteLine($"{response.Hits.Count} result(s){(response.KeywordOnly ? " (keyword only)" : string.Empty)} for \"{query}\" in {indexDirectory}");
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

static ILoggerFactory CreateLoggerFactory(IndexerConfigLoader.Loaded loaded, bool verbose) => LoggerFactory.Create(logging =>
{
    logging.AddConfiguration(loaded.Configuration.GetSection("Logging"));
    logging.AddSimpleConsole(o =>
    {
        o.SingleLine = true;
        o.TimestampFormat = "HH:mm:ss ";
    });
    if (verbose)
    {
        logging.SetMinimumLevel(LogLevel.Debug);
    }
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
