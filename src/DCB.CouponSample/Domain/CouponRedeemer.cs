using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Tags;
using Marten;

namespace DCB.CouponSample.Domain;

public enum RedeemOutcome { Accepted, Rejected, NotFound }

public record RedeemResult(RedeemOutcome Outcome, string? Reason = null);

public class CouponRedeemer
{
    private readonly IDocumentStore _store;
    // Enough to absorb the normal optimistic-concurrency conflicts: a losing
    // redemption re-reads, re-decides (usually now rejecting), and re-writes.
    private const int MaxRetries = 16;

    public CouponRedeemer(IDocumentStore store) => _store = store;

    public async Task DefineCouponAsync(string code, int maxTotal, int maxPerCustomer, CancellationToken ct = default)
    {
        await using var session = _store.LightweightSession();
        var couponTag = new CouponCode(code);

        var defined = session.Events.BuildEvent(new CouponDefined(code, maxTotal, maxPerCustomer));
        defined.WithTag(couponTag);
        session.Events.Append(Guid.NewGuid(), defined);

        await session.SaveChangesAsync(ct);
    }

    // ---- Cap semantics: a HARD cap (exact ceiling) on Marten 9.4.0+ ----
    //
    // DCB under the default READ COMMITTED isolation now gives an *exact*
    // guarantee. At SaveChangesAsync, Marten serializes concurrent same-tag
    // appends on a row-level constraint (a side table that turns the old
    // optimistic EXISTS check into a real write conflict) and throws
    // DcbConcurrencyException if a newer matching event landed since we read.
    // Two redemptions that both read the same baseline can no longer both
    // commit: one wins, the other conflicts and retries. So the cap holds
    // exactly - you see the cap, never cap+1.
    //
    // (On Marten 9.3.x this was a SOFT cap: the EXISTS check ran as a separate
    // non-locking statement, so truly-simultaneous writers could each pass it
    // before either committed and the cap could be exceeded by one. Marten
    // 9.4.0 - issue #4591 - closed that race; this sample targets 9.5.0.)
    //
    // Exactness is not free: same-tag writers now take turns at commit, so a
    // hot coupon serializes through one decision point. That is the right
    // default here, but mind the global-lock warning in Part 4 - a broad tag
    // that matches a huge set of events turns this serialization into a
    // bottleneck.
    public async Task<RedeemResult> RedeemAsync(string code, Guid customerId, decimal orderTotal, CancellationToken ct = default)
    {
        var couponTag = new CouponCode(code);
        var customerTag = new CustomerId(customerId);

        // Bounded retry loop - concurrency conflicts re-read, re-decide, re-write.
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            await using var session = _store.LightweightSession();

            // The query IS the consistency boundary. We are telling Marten:
            // "this decision depends on events tagged with THIS coupon OR THIS customer.
            //  defend both."
            var query = new EventTagQuery()
                .Or<CouponCode>(couponTag)
                .Or<CustomerId>(customerTag);

            var boundary = await session.Events
                .FetchForWritingByTags<CouponRedemptionGuard>(query, ct);

            var guard = boundary.Aggregate;
            if (guard is null || guard.MaxTotalUses == 0)
                return new RedeemResult(RedeemOutcome.NotFound, $"Coupon {code} not defined");

            var decision = guard.CanRedeem(customerId);
            if (!decision.Allowed)
                return new RedeemResult(RedeemOutcome.Rejected, decision.Reason);

            var redeemed = session.Events.BuildEvent(
                new CouponRedeemed(code, customerId, orderTotal));
            redeemed.WithTag(couponTag, customerTag);
            boundary.AppendOne(redeemed);

            try
            {
                await session.SaveChangesAsync(ct);
                return new RedeemResult(RedeemOutcome.Accepted);
            }
            catch (ConcurrencyException)
            {
                // Catches both DcbConcurrencyException (DCB) and
                // EventStreamUnexpectedMaxEventIdException (per-stream).
                // The losing writer re-reads, re-decides (usually now
                // rejecting), and re-writes. The small cooldown is just
                // backoff to spread out a thundering herd; on 9.4.0+ it is no
                // longer required for correctness (the row-level constraint
                // serializes the appends, so the retry already sees the
                // committed state). On 9.3.x it WAS needed - an immediate
                // retry could race the just-committed event becoming visible
                // to the old non-locking EXISTS check.
                await Task.Delay(50, ct);
            }
        }

        return new RedeemResult(RedeemOutcome.Rejected, "Too many concurrent attempts");
    }
}
