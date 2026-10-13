using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace McpMySite.Indexer.Configuration;

/// <summary>
/// Builds the indexer configuration the same way an ASP.NET Core app does, in increasing precedence:
/// appsettings.defaults.json shipped with the tool, the site's appsettings.json, appsettings.{Environment}.json,
/// user secrets (Development only), environment variables (Crawl__MaxPages=50), then explicit --set Key=Value overrides.
/// </summary>
public static class IndexerConfigLoader
{
    public const string DefaultEnvironment = "Production";

    public sealed record Loaded(
        IndexerConfig Config,
        IConfigurationRoot Configuration,
        /// <summary>Folder that relative paths in the config resolve against: the config file's folder, or the current directory.</summary>
        string BaseDirectory,
        string? ConfigFile,
        string EnvironmentName);

    public static Loaded Load(string? configPath, string? environment, IEnumerable<string>? overrides = null)
    {
        var path = Path.GetFullPath(configPath ?? IndexerConfig.DefaultFileName);
        var fileExists = File.Exists(path);
        var baseDirectory = fileExists ? Path.GetDirectoryName(path)! : Directory.GetCurrentDirectory();
        var environmentName = ResolveEnvironment(environment);

        var builder = new ConfigurationBuilder().SetBasePath(baseDirectory);

        // Optional because the code defaults in IndexerConfig apply anyway; the file is there to be read.
        builder.AddJsonFile(source =>
        {
            source.Path = Path.Combine(AppContext.BaseDirectory, IndexerConfig.DefaultsFileName);
            source.Optional = true;
            source.ReloadOnChange = false;
            source.ResolveFileProvider();
        });

        if (fileExists)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);
            builder.AddJsonFile(Path.GetFileName(path), optional: false, reloadOnChange: false);
            builder.AddJsonFile($"{name}.{environmentName}{extension}", optional: true, reloadOnChange: false);
        }

        if (string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase))
        {
            builder.AddUserSecrets(typeof(IndexerConfigLoader).Assembly, optional: true);
        }

        builder.AddEnvironmentVariables();
        builder.AddInMemoryCollection(ParseOverrides(overrides ?? []));

        var root = builder.Build();
        var config = root.Get<IndexerConfig>() ?? new IndexerConfig();
        return new Loaded(config, root, baseDirectory, fileExists ? path : null, environmentName);
    }

    public static string ResolveEnvironment(string? explicitName) =>
        explicitName
        ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
        ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
        ?? DefaultEnvironment;

    /// <summary>Turns "Crawl:MaxPages=50" style entries into configuration keys. "__" is accepted as a separator too.</summary>
    public static Dictionary<string, string?> ParseOverrides(IEnumerable<string> overrides)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in overrides)
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0)
            {
                throw new InvalidOperationException($"--set expects Key=Value, got '{entry}'.");
            }

            var key = entry[..separator].Trim().Replace("__", ConfigurationPath.KeyDelimiter);
            result[key] = entry[(separator + 1)..];
        }

        return result;
    }

    /// <summary>Serialises a config as an appsettings.json document, with a Logging section, for the init command.</summary>
    public static string ToAppSettingsJson(IndexerConfig config)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() },
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        var node = JsonSerializer.SerializeToNode(config, options)!.AsObject();
        node["Logging"] = new JsonObject
        {
            ["LogLevel"] = new JsonObject
            {
                ["Default"] = "Information",
                ["System.Net.Http"] = "Warning",
            },
        };

        return node.ToJsonString(options);
    }
}
