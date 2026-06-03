using DCB.CouponSample.Domain;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace DCB.CouponSample.Wolverine;

// Successful response shape - typed so it shows up in generated OpenAPI metadata.
public record DefineResponse(string Code);

// Defining a coupon is a single-stream append with no cross-entity invariant:
// there is no prior state to read and defend, so there is no DCB boundary, no
// FetchForWritingByTags, and no concurrency retry. This is a [WolverinePost]
// purely so the whole API speaks one style; unlike RedeemCouponEndpoint, none
// of Wolverine's DCB machinery is engaged here.
//
// Inject IDocumentSession (the scoped session Wolverine manages) rather than
// opening a LightweightSession from IDocumentStore - a separate session can
// race / not be visible to subsequent reads inside the same host.
public static class DefineCouponEndpoint
{
    // IResult keeps this symmetric with RedeemCouponEndpoint; the attribute
    // gives OpenAPI the response shape IResult can't convey on its own.
    [ProducesResponseType(typeof(DefineResponse), StatusCodes.Status200OK)]
    [WolverinePost("/coupons")]
    public static async Task<IResult> Post(DefineCoupon cmd, IDocumentSession session)
    {
        var defined = session.Events.BuildEvent(
            new CouponDefined(cmd.Code, cmd.MaxTotalUses, cmd.MaxPerCustomer));
        defined.WithTag(new CouponCode(cmd.Code));
        session.Events.Append(Guid.NewGuid(), defined);

        await session.SaveChangesAsync();
        return Results.Ok(new DefineResponse(cmd.Code));
    }
}
