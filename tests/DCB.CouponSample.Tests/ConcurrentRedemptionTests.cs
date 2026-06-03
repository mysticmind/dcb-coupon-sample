using DCB.CouponSample.Domain;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace DCB.CouponSample.Tests;

// Self-contained: Testcontainers spins up a throwaway Postgres per test class.
// No need to `docker compose up` first - just `dotnet test`.
public class ConcurrentRedemptionTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("dcb_sample")
        .WithUsername("dcb")
        .WithPassword("dcb")
        .Build();

    private IDocumentStore _store = null!;
    private CouponRedeemer _redeemer = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        _store = DocumentStore.For(opts =>
        {
            opts.Connection(_postgres.GetConnectionString());
            opts.Events.RegisterTagType<CouponCode>("coupon")
                .ForAggregate<CouponRedemptionGuard>();
            opts.Events.RegisterTagType<CustomerId>("customer")
                .ForAggregate<CouponRedemptionGuard>();
            opts.Events.DcbStorageMode = DcbStorageMode.HStore;
        });

        _redeemer = new CouponRedeemer(_store);
    }

    public async Task DisposeAsync()
    {
        _store.Dispose();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Per_customer_cap_is_respected_under_concurrency()
    {
        // Arrange: a coupon that allows 2 uses per customer, plenty of total uses.
        const string code = "SUMMER25";
        var customerId = Guid.NewGuid();
        await _redeemer.DefineCouponAsync(code, maxTotal: 1000, maxPerCustomer: 2);

        // Act: fire 50 parallel redemption attempts for the same customer.
        var attempts = Enumerable.Range(0, 50)
            .Select(_ => _redeemer.RedeemAsync(code, customerId, 9.99m))
            .ToArray();

        var results = await Task.WhenAll(attempts);

        var accepted = results.Count(r => r.Outcome == RedeemOutcome.Accepted);
        accepted.ShouldBeLessThanOrEqualTo(2);
        accepted.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Total_use_cap_is_respected_under_concurrency()
    {
        // Arrange: 5 total uses, 1 per customer. 50 distinct customers race.
        // This is the cross-entity invariant - no single customer stream
        // could possibly defend it. Only DCB can.
        const string code = "FLASH5";
        await _redeemer.DefineCouponAsync(code, maxTotal: 5, maxPerCustomer: 1);

        var customers = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToArray();

        // Act
        var attempts = customers
            .Select(id => _redeemer.RedeemAsync(code, id, 9.99m))
            .ToArray();

        var results = await Task.WhenAll(attempts);

        var accepted = results.Count(r => r.Outcome == RedeemOutcome.Accepted);
        accepted.ShouldBeLessThanOrEqualTo(5);
        accepted.ShouldBeGreaterThan(0);
    }
}
