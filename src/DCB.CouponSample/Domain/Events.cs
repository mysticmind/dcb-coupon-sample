namespace DCB.CouponSample.Domain;

public record CouponDefined(string Code, int MaxTotalUses, int MaxPerCustomer);

public record CouponRedeemed(string Code, Guid CustomerId, decimal OrderTotal);
