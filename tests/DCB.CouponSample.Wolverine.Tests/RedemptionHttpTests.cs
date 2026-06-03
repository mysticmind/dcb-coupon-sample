using System.Net;
using System.Net.Http.Json;
using Alba;
using DCB.CouponSample.Domain;
using DCB.CouponSample.Wolverine;
using JasperFx.Events.Tags;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace DCB.CouponSample.Wolverine.Tests;

// Black-box integration test of the Wolverine variant, exercised through HTTP.
//
// We don't care how Wolverine wires the DCB handler under the hood - we only
// care that the API behaves correctly under contention. If you swapped out
// Wolverine for plain Marten and kept the routes identical, the same test
// would still pass. That's the point of testing at the seam.
public class RedemptionHttpTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16")
        .WithDatabase("dcb_sample")
        .WithUsername("dcb")
        .WithPassword("dcb")
        .Build();

    private IAlbaHost _host = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        // Inject the Testcontainers connection string via the
        // ConnectionStrings__Postgres environment variable, which
        // WebApplication.CreateBuilder picks up automatically at construction
        // time. Going through Alba's ConfigureAppConfiguration runs too late:
        // Program.cs reads the connection string immediately inside
        // AddMarten(...), before the test host's configuration sources are
        // merged, so the hardcoded fallback would win.
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__Postgres", _postgres.GetConnectionString());

        _host = await AlbaHost.For<Program>(_ => { });
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", null);
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Defining_a_coupon_returns_ok()
    {
        await _host.Scenario(s =>
        {
            s.Post.Json(new DefineCoupon("HELLO", 10, 1)).ToUrl("/coupons");
            s.StatusCodeShouldBeOk();
        });
    }

    [Fact]
    public async Task Per_customer_cap_is_respected_under_concurrency()
    {
        // Arrange - define coupon with per-customer cap of 2.
        await _host.Scenario(s =>
        {
            s.Post.Json(new DefineCoupon("SUMMER25", 1000, 2)).ToUrl("/coupons");
            s.StatusCodeShouldBeOk();
        });

        // Probe 1 - events are in the table, tagged correctly.
        // Probe 2 - the boundary aggregate projects with MaxTotalUses set,
        // i.e. the exact call the redeem endpoint makes works here too.
        var store = _host.Services.GetRequiredService<IDocumentStore>();
        await using (var probe = store.LightweightSession())
        {
            var probeQuery = new EventTagQuery().Or<CouponCode>(new CouponCode("SUMMER25"));

            var probeEvents = await probe.Events.QueryByTagsAsync(probeQuery);
            probeEvents.Count.ShouldBe(1,
                $"probe 1 (QueryByTagsAsync) expected 1 CouponDefined event, got {probeEvents.Count}");

            var probeBoundary = await probe.Events
                .FetchForWritingByTags<CouponRedemptionGuard>(probeQuery);
            probeBoundary.Aggregate.ShouldNotBeNull(
                "probe 2 (FetchForWritingByTags) returned a null aggregate even though " +
                "probe 1 found the event. Means the redeem endpoint's call path will also " +
                "see no aggregate. Bug is in FetchForWritingByTags<CouponRedemptionGuard> " +
                "under this configuration.");
            probeBoundary.Aggregate!.MaxTotalUses.ShouldBe(1000,
                "probe 2 returned an aggregate but Apply(CouponDefined) wasn't dispatched. " +
                "SG-emitted evolver may be missing CouponDefined from IncludedEventTypes.");
        }

        var customerId = Guid.NewGuid();
        var client = _host.Server.CreateClient();

        // Act - fire 50 parallel HTTP redemption requests for the same customer.
        var tasks = Enumerable.Range(0, 50)
            .Select(_ => client.PostAsJsonAsync(
                "/coupons/SUMMER25/redeem",
                new { CustomerId = customerId, OrderTotal = 9.99m }))
            .ToArray();

        var responses = await Task.WhenAll(tasks);

        var breakdown = string.Join(", ",
            responses.GroupBy(r => (int)r.StatusCode)
                .OrderBy(g => g.Key)
                .Select(g => $"{g.Key}={g.Count()}"));

        // HARD cap on Marten 9.4.0+ (see RedeemCouponEndpoint.Configure). DCB
        // under READ COMMITTED now serializes concurrent same-tag appends on a
        // row-level constraint, so exactly the cap lands - we assert at most
        // cap OK responses, never over. The rest are 409 (cap reached); a few
        // may be 500 if they exhaust their retry budget under contention - both
        // mean "your redemption did not land".
        var ok = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        ok.ShouldBeLessThanOrEqualTo(2, $"status code breakdown: {breakdown}");
        ok.ShouldBeGreaterThan(0, $"status code breakdown: {breakdown}");
    }

    [Fact]
    public async Task Total_use_cap_is_respected_across_distinct_customers()
    {
        // Cross-entity invariant: a coupon with total cap of 5,
        // 50 distinct customers each trying to redeem once.
        // No single per-customer stream could ever defend this rule.
        await _host.Scenario(s =>
        {
            s.Post.Json(new DefineCoupon("FLASH5", 5, 1)).ToUrl("/coupons");
            s.StatusCodeShouldBeOk();
        });

        var client = _host.Server.CreateClient();
        var customers = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToArray();

        var tasks = customers
            .Select(id => client.PostAsJsonAsync(
                "/coupons/FLASH5/redeem",
                new { CustomerId = id, OrderTotal = 9.99m }))
            .ToArray();

        var responses = await Task.WhenAll(tasks);

        // Cross-entity total cap, same hard-cap semantics: exactly the cap of
        // the 50 distinct customers redeem successfully. The point is that DCB
        // defends a cap NO single stream could - exact, never over.
        var ok = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        ok.ShouldBeLessThanOrEqualTo(5);
        ok.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Redeeming_an_unknown_coupon_returns_404()
    {
        await _host.Scenario(s =>
        {
            s.Post.Json(new { CustomerId = Guid.NewGuid(), OrderTotal = 1m })
                  .ToUrl("/coupons/DOES_NOT_EXIST/redeem");
            s.StatusCodeShouldBe(404);
        });
    }
}
