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

    // ---- Cap semantics: a SOFT cap (at-most-N with bounded slack) ----
    // This is a deliberate design choice, not a limitation we failed to fix.
    //
    // DCB under the default READ COMMITTED isolation gives an *optimistic*
    // guarantee: at SaveChangesAsync, Marten runs an EXISTS check for newer
    // events matching the tag query and throws DcbConcurrencyException if any
    // appeared since we read. That stops wild overshoot (you will never get 50
    // redemptions through a cap of 2), but it is not a hard ceiling: two
    // redemptions that both read the same baseline can each pass the EXISTS
    // check before either commits, so under heavy contention the cap can be
    // exceeded by one (you see cap, occasionally cap+1).
    //
    // Making it a HARD cap would require serializing every redemption of the
    // same coupon (an advisory lock or Serializable isolation), which puts a
    // throughput ceiling on hot coupons and pins a DB connection per waiter.
    // For a marketing coupon, an occasional extra redemption costs pennies —
    // far less than that serialized hot path. So we accept bounded slack and
    // keep the scalable optimistic path. Reach for a hard cap only when an
    // overshoot is genuinely expensive (physical inventory, regulatory limits,
    // financial caps).
    public async Task<RedeemResult> RedeemAsync(string code, Guid customerId, decimal orderTotal, CancellationToken ct = default)
    {
        var couponTag = new CouponCode(code);
        var customerTag = new CustomerId(customerId);

        // Bounded retry loop — concurrency conflicts re-read, re-decide, re-write.
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
                // Small cooldown before retry — without it the next read
                // races the just-committed event becoming visible to the
                // DCB EXISTS check.
                await Task.Delay(50, ct);
            }
        }

        return new RedeemResult(RedeemOutcome.Rejected, "Too many concurrent attempts");
    }
}
