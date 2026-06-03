using JasperFx.Events.Aggregation;

namespace DCB.CouponSample.Domain;

// A "boundary aggregate" is a transient decision model.
// It is NOT tied to a single stream - Marten builds it on the fly
// from whichever events match the tag query passed to FetchForWritingByTags.
[BoundaryAggregate]
public class CouponRedemptionGuard
{
    public string Code { get; private set; } = "";
    public int MaxTotalUses { get; private set; }
    public int MaxPerCustomer { get; private set; }
    public int TotalUses { get; private set; }
    public Dictionary<Guid, int> UsesByCustomer { get; } = new();

    public void Apply(CouponDefined e)
    {
        Code = e.Code;
        MaxTotalUses = e.MaxTotalUses;
        MaxPerCustomer = e.MaxPerCustomer;
    }

    public void Apply(CouponRedeemed e)
    {
        TotalUses++;
        UsesByCustomer.TryGetValue(e.CustomerId, out var count);
        UsesByCustomer[e.CustomerId] = count + 1;
    }

    public RedeemDecision CanRedeem(Guid customerId)
    {
        if (MaxTotalUses == 0) return RedeemDecision.Reject("Coupon does not exist");
        if (TotalUses >= MaxTotalUses) return RedeemDecision.Reject("Total-use cap reached");

        UsesByCustomer.TryGetValue(customerId, out var count);
        if (count >= MaxPerCustomer) return RedeemDecision.Reject("Per-customer cap reached");

        return RedeemDecision.Accept();
    }
}

public readonly record struct RedeemDecision(bool Allowed, string? Reason)
{
    public static RedeemDecision Accept() => new(true, null);
    public static RedeemDecision Reject(string reason) => new(false, reason);
}
