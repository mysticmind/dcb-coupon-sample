using DCB.CouponSample.Api;
using DCB.CouponSample.Domain;
using JasperFx.Events;
using Marten;
using Marten.Events;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMarten(opts =>
{
    opts.Connection(builder.Configuration.GetConnectionString("Postgres")!);

    // Tell Marten which strong-typed tag wrappers we use, and which
    // boundary aggregate they project into. Without .ForAggregate<T>(),
    // Marten treats the type as a regular single-stream projection and
    // fails with "No source-generated dispatcher found for
    // SingleStreamProjection<T, Guid>" the first time DCB tries to project.
    // The short names (e.g. "coupon") are stored alongside each event
    // and are part of your event store schema, so keep them stable.
    opts.Events.RegisterTagType<CouponCode>("coupon")
        .ForAggregate<CouponRedemptionGuard>();
    opts.Events.RegisterTagType<CustomerId>("customer")
        .ForAggregate<CouponRedemptionGuard>();

    // HSTORE-backed tag storage gives faster reads/writes for DCB queries.
    // Plain TagTables mode works too; pick whichever your Postgres supports.
    opts.Events.DcbStorageMode = DcbStorageMode.HStore;
})
.UseLightweightSessions();

builder.Services.AddSingleton<CouponRedeemer>();

var app = builder.Build();

app.MapCouponEndpoints();
app.MapGet("/", () => "DCB.CouponSample is running. Try POST /coupons");

app.Run("http://localhost:5080");
