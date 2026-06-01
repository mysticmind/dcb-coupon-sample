namespace DCB.CouponSample.Wolverine;

// POST body for /coupons, consumed by DefineCouponEndpoint; no DCB is needed
// for coupon definition since it is a single-stream append.
public record DefineCoupon(string Code, int MaxTotalUses, int MaxPerCustomer);
