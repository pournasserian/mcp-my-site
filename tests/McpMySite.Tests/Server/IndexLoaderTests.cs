using McpMySite.Server.Index;
using McpMySite.Server.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace McpMySite.Tests.Server;

public class IndexLoaderTests
{
    private static IndexLoader CreateLoader(IndexHolder holder, IndexOptions options)
    {
        var services = new ServiceCollection().AddHttpClient().BuildServiceProvider();
        return new IndexLoader(holder, Options.Create(options), services.GetRequiredService<IHttpClientFactory>(), NullLogger<IndexLoader>.Instance);
    }

    [Fact]
    public async Task LoadsFromPathAndSwapsOnlyWhenManifestChanges()
    {
        var directory = await TestIndex.WriteAsync(IndexFormat.JsonStoreType, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var holder = new IndexHolder();
        var loader = CreateLoader(holder, new IndexOptions { Path = directory });

        Assert.True(await loader.RefreshAsync(CancellationToken.None));
        var first = holder.Current;
        Assert.NotNull(first);

        Assert.False(await loader.RefreshAsync(CancellationToken.None));
        Assert.Same(first, holder.Current);

        // A rebuild writes a newer manifest into the same folder; the next poll must swap it in.
        await IndexStoreOpener.CreateWriter(IndexFormat.JsonStoreType).WriteAsync(
            directory, TestIndex.Manifest(IndexFormat.JsonStoreType, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)), TestIndex.Pages, TestIndex.Chunks, TestIndex.Vectors);

        Assert.True(await loader.RefreshAsync(CancellationToken.None));
        Assert.NotSame(first, holder.Current);
        Assert.Equal(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), holder.Current!.Manifest.BuiltAt);
    }

    [Fact]
    public async Task KeepsLastGoodIndexWhenNewOneIsBroken()
    {
        var directory = await TestIndex.WriteAsync(IndexFormat.JsonStoreType, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var holder = new IndexHolder();
        var loader = CreateLoader(holder, new IndexOptions { Path = directory });
        Assert.True(await loader.RefreshAsync(CancellationToken.None));
        var good = holder.Current;

        // Newer manifest but a corrupt vector file.
        await File.WriteAllTextAsync(Path.Combine(directory, IndexFormat.ManifestFileName), TestIndex.Manifest(IndexFormat.JsonStoreType, DateTimeOffset.UtcNow).ToJson());
        await File.WriteAllBytesAsync(Path.Combine(directory, IndexFormat.VectorsFileName), new byte[4]);

        Assert.False(await loader.RefreshAsync(CancellationToken.None));
        Assert.Same(good, holder.Current);
        Assert.Contains("vectors.bin", holder.LastError);
    }

    [Theory]
    [InlineData(IndexFormat.JsonStoreType)]
    [InlineData(IndexFormat.SqliteStoreType)]
    public async Task LoadsFromUrlServedByStaticHost(string storeType)
    {
        var directory = await TestIndex.WriteAsync(storeType);

        var host = WebApplication.CreateBuilder();
        host.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = host.Build();
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(directory),
            ServeUnknownFileTypes = true,
        });
        await app.StartAsync();
        var baseUrl = app.Urls.First();

        var holder = new IndexHolder();
        var loader = CreateLoader(holder, new IndexOptions { Url = baseUrl + "/" });

        Assert.True(await loader.RefreshAsync(CancellationToken.None));
        Assert.Equal(3, holder.Current!.Manifest.PageCount);
        Assert.Equal(storeType, holder.Current.Manifest.StoreType);
        var page = await holder.Current.GetPageAsync("https://example.test/docs/widgets");
        Assert.Equal("Widgets", page!.Title);

        // Unchanged manifest: no download, no swap.
        Assert.False(await loader.RefreshAsync(CancellationToken.None));

        await app.StopAsync();
    }
}
