using DCB.CouponSample.Domain;
using DCB.CouponSample.Wolverine;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Wolverine;
using Wolverine.Http;
using Wolverine.Marten;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMarten(opts =>
{
    opts.Connection(builder.Configuration.GetConnectionString("Postgres")
        ?? "Host=localhost;Port=5432;Database=dcb_sample;Username=dcb;Password=dcb");

    // .ForAggregate<>() links the tag to the boundary aggregate. Without
    // it, Marten falls back to a single-stream projection path and fails
    // at first projection with "No source-generated dispatcher found...".
    opts.Events.RegisterTagType<CouponCode>("coupon")
        .ForAggregate<CouponRedemptionGuard>();
    opts.Events.RegisterTagType<CustomerId>("customer")
        .ForAggregate<CouponRedemptionGuard>();

    // Explicitly register every event type. Under plain Marten the lazy
    // registration on first BuildEvent is enough; under IntegrateWithWolverine
    // the EventGraph that backs DCB query event-type filtering is built before
    // any BuildEvent runs, so unregistered types make ResolveAggregatorEventTypeNames<T>
    // return nothing and FetchForWritingByTags<T> finds zero matching events.
    opts.Events.AddEventType<CouponDefined>();
    opts.Events.AddEventType<CouponRedeemed>();
})
// Critical: Wolverine's OutboxedSessionFactory opens sessions via the
// registered ISessionFactory. Without UseLightweightSessions(), the default
// "heavy" session is used and its identity map / dirty-tracking interferes
// with FetchForWritingByTags<T> — every redemption sees a null aggregate
// and returns 404 even though the events are committed and queryable from
// a plain session.
.UseLightweightSessions()
.IntegrateWithWolverine();

builder.Host.UseWolverine();

// Required by Wolverine.HTTP; without it MapWolverineEndpoints throws
// "Required usage of IServiceCollection.AddWolverineHttp()".
builder.Services.AddWolverineHttp();

var app = builder.Build();

// Discovers every [WolverinePost] / [WolverineGet] / etc. attribute in the
// project and registers it as an ASP.NET route. Both DefineCouponEndpoint.Post
// (a plain single-stream append) and RedeemCouponEndpoint.Post (DCB-aware) get
// wired up here — the whole API speaks one style.
app.MapWolverineEndpoints();

app.Run("http://localhost:5081");

// Exposed so Alba can bootstrap the app via AlbaHost.For<Program>.
public partial class Program;
