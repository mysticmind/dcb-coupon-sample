namespace DCB.CouponSample.Domain;

// Tags are tiny wrappers around primitives.
// Marten uses them as indexed labels on events so it can later
// answer "give me every event tagged with this coupon, or this customer".
public record CouponCode(string Value);

public record CustomerId(Guid Value);
