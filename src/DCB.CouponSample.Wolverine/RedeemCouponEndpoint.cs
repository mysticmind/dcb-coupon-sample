using DCB.CouponSample.Domain;
using JasperFx;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Tags;
using Marten;
using Microsoft.AspNetCore.Mvc;
using Wolverine.ErrorHandling;
using Wolverine.Http;
using Wolverine.Marten;
using Wolverine.Runtime.Handlers;

namespace DCB.CouponSample.Wolverine;

// POST body for /coupons/{code}/redeem. The `code` arrives as a route parameter.
public record RedeemCouponBody(Guid CustomerId, decimal OrderTotal);

// Successful response shape — typed so it shows up in generated OpenAPI metadata.
public record RedeemResponse(string Status);

// The HTTP route, the consistency boundary, the cap check, and the write all
// live in this one method. That isn't a workaround — it's how DCB endpoints
// want to look. The "validation" here reads from the same boundary the
// endpoint writes to, so it isn't really validation; it's the business
// decision. Wolverine.HTTP's Validate / Before middleware is for input-shape
// checks ("is the body well-formed", "is the date in the future") that don't
// need the aggregate.
//
// At startup Wolverine generates the wrapper:
//   1. calls Load() to build the tag query
//   2. runs FetchForWritingByTags<CouponRedemptionGuard>(query)
//   3. invokes Redeem(...) with the boundary
//   4. SaveChangesAsync, catch ConcurrencyException, retry per Configure()
public static class RedeemCouponEndpoint
{
    public static EventTagQuery Load(string code, RedeemCouponBody body)
        => new EventTagQuery()
            .Or<CouponCode>(new CouponCode(code))
            .Or<CustomerId>(new CustomerId(body.CustomerId));

    // Returning IResult lets us vary the status code per branch (200/404/409),
    // but Wolverine can't infer the response shape from IResult for OpenAPI.
    // The [ProducesResponseType] attributes restore that metadata so generated
    // Swagger/OpenAPI describes each response. (ASP.NET Core attributes, honored
    // by Wolverine.HTTP.)
    [ProducesResponseType(typeof(RedeemResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [WolverinePost("/coupons/{code}/redeem")]
    public static IResult Post(
        string code,
        RedeemCouponBody body,
        [BoundaryModel] IEventBoundary<CouponRedemptionGuard> boundary,
        IDocumentSession session)
    {
        var guard = boundary.Aggregate;
        if (guard is null || guard.MaxTotalUses == 0)
            return Results.NotFound(new { error = $"Coupon {code} not defined" });

        var decision = guard.CanRedeem(body.CustomerId);
        if (!decision.Allowed)
            return Results.Conflict(new { error = decision.Reason });

        // Build + tag explicitly. boundary.AppendOne(raw event) would otherwise
        // try to infer tags from event properties whose .NET TYPES match
        // registered tag wrappers; our event carries string/Guid primitives,
        // not CouponCode/CustomerId records, so inference fails and the append
        // throws "Cannot route event ... no explicit tags set".
        var redeemed = session.Events.BuildEvent(
            new CouponRedeemed(code, body.CustomerId, body.OrderTotal));
        redeemed.WithTag(new CouponCode(code), new CustomerId(body.CustomerId));
        boundary.AppendOne(redeemed);

        return Results.Ok(new RedeemResponse("accepted"));
    }

    // Wolverine does NOT auto-retry on transient exceptions. Without this, a
    // DcbConcurrencyException from a concurrent redemption — the DCB EXISTS
    // check tripping because a newer matching event landed since we read —
    // would bubble up as a 500 instead of being retried into a clean 409.
    //
    // This is a SOFT cap (at-most-N with bounded slack), by design. Under
    // READ COMMITTED the optimistic DCB check stops wild overshoot but does
    // not make the cap a hard ceiling: under heavy contention you may see
    // cap+1. We accept that — for a marketing coupon an occasional extra
    // redemption is far cheaper than serializing every redemption of a hot
    // coupon. The retry just converts a lost optimistic race into a correct
    // rejection. See CouponRedeemer in the plain-Marten project for the same
    // reasoning written out long-hand.
    public static void Configure(HandlerChain chain)
    {
        // RetryWithCooldown takes one delay per attempt; the count of delays =
        // retry budget. A few small steps is the right shape for a real deploy;
        // tests with tens of contending requests need a little more headroom.
        chain.OnException<ConcurrencyException>()
            .RetryWithCooldown(
                50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds());
    }
}
