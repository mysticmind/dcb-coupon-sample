using DCB.CouponSample.Domain;
using JasperFx.Events.Tags;
using Marten;

namespace DCB.CouponSample.Api;

public static class RedeemEndpoints
{
    public record DefineCouponRequest(string Code, int MaxTotalUses, int MaxPerCustomer);
    public record RedeemRequest(Guid CustomerId, decimal OrderTotal);

    public static void MapCouponEndpoints(this WebApplication app)
    {
        app.MapPost("/coupons", async (DefineCouponRequest req, CouponRedeemer redeemer) =>
        {
            await redeemer.DefineCouponAsync(req.Code, req.MaxTotalUses, req.MaxPerCustomer);
            return Results.Created($"/coupons/{req.Code}", req);
        });

        app.MapPost("/coupons/{code}/redeem", async (string code, RedeemRequest req, CouponRedeemer redeemer) =>
        {
            var result = await redeemer.RedeemAsync(code, req.CustomerId, req.OrderTotal);
            return result.Outcome switch
            {
                RedeemOutcome.Accepted => Results.Ok(new { status = "accepted" }),
                RedeemOutcome.NotFound => Results.NotFound(new { error = result.Reason }),
                RedeemOutcome.Rejected => Results.Conflict(new { error = result.Reason }),
                _ => Results.StatusCode(500)
            };
        });

        app.MapGet("/coupons/{code}", async (string code, IDocumentStore store) =>
        {
            await using var session = store.LightweightSession();
            var query = new EventTagQuery().Or<CouponCode>(new CouponCode(code));
            var guard = await session.Events.AggregateByTagsAsync<CouponRedemptionGuard>(query);
            return guard is null
                ? Results.NotFound()
                : Results.Ok(new
                {
                    code = guard.Code,
                    totalUses = guard.TotalUses,
                    maxTotalUses = guard.MaxTotalUses,
                    maxPerCustomer = guard.MaxPerCustomer,
                    usesByCustomer = guard.UsesByCustomer
                });
        });
    }
}
