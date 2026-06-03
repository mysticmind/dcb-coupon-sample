# dcb-coupon-sample

A runnable companion sample for the blog series **[Dynamic Consistency Boundary in Marten](https://mysticmind.dev/dcb-in-marten-part-1-the-aggregate-trap)** (4 parts). The posts reference the projects, files, and tests in this repo as they walk through the pattern.

It demonstrates how a Dynamic Consistency Boundary (DCB) lets a single command defend an invariant that spans two entities - in this case, a coupon redemption that must respect both a per-customer cap and a system-wide cap. As of **Marten 9.4.0** the DCB check is a **hard cap** (an exact ceiling) by default - concurrent same-tag appends serialize on a row-level constraint, so the cap holds exactly. See [Part 4](https://mysticmind.dev/dcb-in-marten-part-4-production-considerations) of the series for the mechanism, and what it costs on a hot tag.

## What's inside

```
sample/
├── docker-compose.yml                  # Postgres for local dev (not needed for tests)
├── src/DCB.CouponSample/               # Plain Marten + minimal API
│   ├── Domain/                         # Tags, events, boundary aggregate, redeemer (explicit DCB cycle)
│   ├── Api/                            # POST /coupons, POST /coupons/{code}/redeem, GET /coupons/{code}
│   └── Program.cs
├── src/DCB.CouponSample.Wolverine/     # Same rule via Wolverine.HTTP (every route is a [WolverinePost])
│   ├── Domain/                         # Own copy of tags, events, boundary aggregate (self-contained, no project ref)
│   ├── DefineCouponEndpoint.cs         # [WolverinePost] plain single-stream append (no DCB boundary)
│   ├── RedeemCouponEndpoint.cs         # Load(...) + [WolverinePost] Post(..., [BoundaryModel] boundary)
│   ├── Commands.cs                     # DefineCoupon / request + response records
│   └── Program.cs                      # IntegrateWithWolverine(), UseWolverine(), MapWolverineEndpoints()
├── tests/DCB.CouponSample.Tests/
│   └── ConcurrentRedemptionTests.cs    # 50 parallel redemptions, exactly the cap lands (Testcontainers)
└── tests/DCB.CouponSample.Wolverine.Tests/
    └── RedemptionHttpTests.cs          # Same race through the HTTP endpoint, via Alba + Testcontainers
```

## Run it

```bash
# 1. Start Postgres
docker compose up -d

# 2. Run the API
dotnet run --project src/DCB.CouponSample

# 3. In another terminal
curl -X POST http://localhost:5080/coupons \
  -H 'Content-Type: application/json' \
  -d '{"code":"SUMMER25","maxTotalUses":1000,"maxPerCustomer":2}'

# Redeem twice as the same customer - both should succeed
curl -X POST http://localhost:5080/coupons/SUMMER25/redeem \
  -H 'Content-Type: application/json' \
  -d '{"customerId":"00000000-0000-0000-0000-000000000001","orderTotal":49.99}'

curl -X POST http://localhost:5080/coupons/SUMMER25/redeem \
  -H 'Content-Type: application/json' \
  -d '{"customerId":"00000000-0000-0000-0000-000000000001","orderTotal":12.50}'

# Third attempt - rejected by the per-customer cap
curl -X POST http://localhost:5080/coupons/SUMMER25/redeem \
  -H 'Content-Type: application/json' \
  -d '{"customerId":"00000000-0000-0000-0000-000000000001","orderTotal":7.00}'
```

## Run the race test

The most interesting bit. Fires 50 concurrent redemption attempts for the same `(coupon, customer)` pair against a per-customer cap of 2 and asserts that **exactly the cap** lands - the assertion is `<= 2` (never over), and at least one succeeds:

```bash
dotnet test
```

Why does the cap hold exactly? On Marten 9.4.0+ the DCB consistency check is backed by a real serializing constraint: at `SaveChangesAsync` concurrent same-tag appends contend on a row-level lock, so two genuinely-simultaneous writers can no longer both pass - one wins, the other gets a `DcbConcurrencyException` and the retry loop converts it into a clean rejection. The cap is a **hard cap** (Part 2 explains the mechanism; Part 4 covers what it costs on a hot tag). The invariant the test proves is *exactly the cap, never over*.

Tests use **Testcontainers**, so they spin up their own throwaway Postgres - no need to `docker compose up` first. Docker just needs to be running.

If DCB is doing its job, the count stays exactly at the cap. If you swap `FetchForWritingByTags` for a plain `QueryByTagsAsync` (no consistency check at all), the same test lets through 20, 30, sometimes all 50 redemptions - the classic event-sourcing race. *That's* the contrast: bounded vs. unbounded.

## Plain Marten vs Wolverine

The repo ships the same business rule in two flavors. Each project is **self-contained** - the Wolverine project carries its own copy of the domain (tags, events, boundary aggregate) rather than referencing the plain one, so you can open either in isolation and see the whole picture:

- **`src/DCB.CouponSample`** - plain Marten. The DCB cycle (`FetchForWritingByTags` → decide → `SaveChangesAsync` → `catch ConcurrencyException` → retry) is hand-written in `CouponRedeemer.RedeemAsync`. (It catches the base `ConcurrencyException`, which covers both the DCB `DcbConcurrencyException` and the per-stream conflict.) Use this while learning - you can *see* DCB happening.
- **`src/DCB.CouponSample.Wolverine`** - same rule via Wolverine.HTTP. The whole API is `[WolverinePost]` endpoints: redemption pairs a `Load(...)` returning the `EventTagQuery` with a `Post(..., [BoundaryModel] boundary)` that holds the decision, and Wolverine codegens the fetch / save / retry. Defining a coupon is also a `[WolverinePost]`, but a plain single-stream append with no DCB boundary. Production-grade ceremony cut.

Run them on different ports against the same Postgres:

```bash
dotnet run --project src/DCB.CouponSample             # http://localhost:5080
dotnet run --project src/DCB.CouponSample.Wolverine   # http://localhost:5081
```

## Compatibility note

Both projects target **Marten 9.5.0** (and WolverineFx 6.4.x for the Wolverine project). The DCB API surface (`RegisterTagType` + `.ForAggregate<T>()`, `EventTagQuery`, `FetchForWritingByTags`, `DcbConcurrencyException`, `[BoundaryAggregate]`, `DcbStorageMode.HStore`) reflects that version.

A few behaviours are version-sensitive and worth knowing if a reviewer is on a different build:

- **Hard cap (9.4.0+).** Earlier 9.3.x builds ran the DCB check as an optimistic, non-locking `SELECT EXISTS(...)`, so concurrent same-tag appends could exceed the cap by a small margin (this was [Marten #4591](https://github.com/JasperFx/marten/issues/4591)). Marten 9.4.0 added a serializing constraint (a side table that turns the check into a row-level write conflict at READ COMMITTED - no `SERIALIZABLE`, no advisory lock), making the cap **hard by default**. That's why the race tests assert exactly the cap. On 9.3.x the same tests would intermittently see cap+1. Upgrading is a schema change, so run `db-apply` / `db-patch` if you deploy with `AutoCreate.None`.
- **`UseLightweightSessions()`.** Earlier builds needed `.UseLightweightSessions()` on the `AddMarten(...)` chain or every DCB endpoint returned a null aggregate (404). Lightweight sessions are the default now, so the explicit call is redundant and the line is not present in the Wolverine `Program.cs`.
- **`StreamIdentity`.** This sample uses the default `AsGuid` stream identity. Earlier 9.x builds needed `opts.Events.StreamIdentity = StreamIdentity.AsString` as a workaround for a `[BoundaryAggregate]` dispatcher bug; that is fixed, so the line is not present here.

If you pin to a different Marten version and a signature has drifted, please open an issue.
