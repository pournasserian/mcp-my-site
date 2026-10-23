extern alias server;
using McpMySite.Server.Requests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace McpMySite.Tests.Server;

public class VisitorKeyTests
{
    [Fact]
    public void SameVisitorSameDayGetsTheSameKeyAndNothingElseDoes()
    {
        var salt = new VisitorSalt();
        var monday = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

        var key = VisitorKey.Compute(salt.Get(monday), "203.0.113.9", "Mozilla/5.0");

        Assert.Equal(16, key.Length);
        Assert.All(key, c => Assert.True(char.IsAsciiHexDigitLower(c)));
        Assert.Equal(key, VisitorKey.Compute(salt.Get(monday.AddHours(11)), "203.0.113.9", "Mozilla/5.0"));
        Assert.NotEqual(key, VisitorKey.Compute(salt.Get(monday), "203.0.113.10", "Mozilla/5.0"));
        Assert.NotEqual(key, VisitorKey.Compute(salt.Get(monday), "203.0.113.9", "curl/8.0"));
        Assert.DoesNotContain("203", key);
    }

    [Fact]
    public void SaltRotatesAtMidnightUtcAndIsNeverTheSameTwice()
    {
        var salt = new VisitorSalt();
        var lateMonday = new DateTimeOffset(2026, 10, 19, 23, 59, 59, TimeSpan.Zero);
        var mondayKey = VisitorKey.Compute(salt.Get(lateMonday), "203.0.113.9", "Mozilla/5.0");

        var tuesdayKey = VisitorKey.Compute(salt.Get(lateMonday.AddSeconds(1)), "203.0.113.9", "Mozilla/5.0");

        Assert.NotEqual(mondayKey, tuesdayKey);
        Assert.NotEqual(new VisitorSalt().Get(lateMonday), new VisitorSalt().Get(lateMonday));
    }

    [Fact]
    public void KeyIsAHashNotAnEncoding()
    {
        var key = VisitorKey.Compute(new byte[32], "203.0.113.9", "Mozilla/5.0");

        Assert.DoesNotContain(Convert.ToHexStringLower(Encoding.UTF8.GetBytes("203.0.113.9")), key);
    }
}

public class VisitorKeyServerTests
{
    private static (ActivityListener Listener, ConcurrentBag<Activity> Spans) Listen()
    {
        var spans = new ConcurrentBag<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "Microsoft.AspNetCore" or "McpMySite.Server",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return (listener, spans);
    }

    // Other tests run at the same time and their spans reach the same listener, so each test searches for a word of
    // its own and keeps only the traces that carry it.
    private static HttpRequestMessage Search(string peer, string userAgent, string query)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/search?q={query}");
        request.Headers.Add(TestPeerStartupFilter.Header, peer);
        request.Headers.UserAgent.ParseAdd(userAgent);
        return request;
    }

    [Fact]
    public async Task RequestAndSearchSpansCarryTheVisitorKeyWhenTurnedOn()
    {
        var (listener, spans) = Listen();
        using (listener)
        {
            var (factory, client) = await LoggedServer.StartAsync(new CapturedLogs(), new() { ["Telemetry:VisitorId"] = "true", ["Telemetry:LogQueryText"] = "true" },
                configureServices: services => services.AddTransient<IStartupFilter, TestPeerStartupFilter>());
            using (factory)
            using (client)
            {
                (await client.SendAsync(Search("203.0.113.9", "Mozilla/5.0", "visitor-on"))).EnsureSuccessStatusCode();
                (await client.SendAsync(Search("203.0.113.9", "Mozilla/5.0", "visitor-on"))).EnsureSuccessStatusCode();
                (await client.SendAsync(Search("203.0.113.10", "Mozilla/5.0", "visitor-on"))).EnsureSuccessStatusCode();
            }
        }

        var searches = spans.Where(s => s.OperationName == "mcpmysite.search" && s.GetTagItem("mcpmysite.query") as string == "visitor-on").ToList();
        Assert.Equal(3, searches.Count);
        var searchKeys = searches.Select(s => Assert.IsType<string>(s.GetTagItem(VisitorKeyMiddleware.Tag))).ToList();
        Assert.Equal(2, searchKeys.Distinct().Count());
        Assert.All(searchKeys, key => Assert.DoesNotContain("203.0.113", key));

        var traces = searches.Select(s => s.TraceId).ToHashSet();
        var requests = spans.Where(s => s.Source.Name == "Microsoft.AspNetCore" && traces.Contains(s.TraceId)).ToList();
        Assert.Equal(3, requests.Count);
        Assert.Equal(searchKeys.Order(), requests.Select(s => Assert.IsType<string>(s.GetTagItem(VisitorKeyMiddleware.Tag))).Order());
    }

    [Fact]
    public async Task NothingIsTaggedByDefault()
    {
        var (listener, spans) = Listen();
        using (listener)
        {
            var (factory, client) = await LoggedServer.StartAsync(new CapturedLogs(), new() { ["Telemetry:LogQueryText"] = "true" },
                configureServices: services => services.AddTransient<IStartupFilter, TestPeerStartupFilter>());
            using (factory)
            using (client)
            {
                (await client.SendAsync(Search("203.0.113.9", "Mozilla/5.0", "visitor-off"))).EnsureSuccessStatusCode();
            }
        }

        var search = Assert.Single(spans, s => s.OperationName == "mcpmysite.search" && s.GetTagItem("mcpmysite.query") as string == "visitor-off");
        var request = Assert.Single(spans, s => s.Source.Name == "Microsoft.AspNetCore" && s.TraceId == search.TraceId);
        Assert.Null(search.GetTagItem(VisitorKeyMiddleware.Tag));
        Assert.Null(request.GetTagItem(VisitorKeyMiddleware.Tag));
    }
}
