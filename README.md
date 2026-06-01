# dcb-coupon-sample

A runnable companion sample for the blog series **[Dynamic Consistency Boundary in Marten](https://mysticmind.dev/dcb-in-marten-part-1-the-aggregate-trap)** (4 parts). The posts reference the projects, files, and tests in this repo as they walk through the pattern.

It demonstrates how a Dynamic Consistency Boundary (DCB) lets a single command defend an invariant that spans two entities — in this case, a coupon redemption that must respect both a per-customer cap and a system-wide cap. Note the cap is a **soft cap** (at-most-N with bounded slack under concurrency), not an exact ceiling — see [Part 4](https://mysticmind.dev/dcb-in-marten-part-4-production-considerations) of the series for why, and when you'd make it exact.

## What's inside

```
sample/
├── docker-compose.yml                  # Postgres for local dev (not needed for tests)
├── src/DCB.CouponSample/               # Plain Marten + minimal API
│   ├── Domain/                         # Tags, events, boundary aggregate, redeemer (explicit DCB cycle)
│   ├── Api/                            # POST /coupons, POST /coupons/{code}/redeem, GET /coupons/{code}
│   └── Program.cs
├── src/DCB.CouponSample.Wolverine/     # Same rule via Wolverine.HTTP (every route is a [WolverinePost])
│   ├── DefineCouponEndpoint.cs         # [WolverinePost] plain single-stream append (no DCB boundary)
│   ├── RedeemCouponEndpoint.cs         # Load(...) + [WolverinePost] Post(..., [BoundaryModel] boundary)
│   ├── Commands.cs                     # DefineCoupon / request + response records
│   └── Program.cs                      # IntegrateWithWolverine(), UseWolverine(), MapWolverineEndpoints()
├── tests/DCB.CouponSample.Tests/
│   └── ConcurrentRedemptionTests.cs    # 50 parallel redemptions, at most cap+slack land (Testcontainers)
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

# Redeem twice as the same customer — both should succeed
curl -X POST http://localhost:5080/coupons/SUMMER25/redeem \
  -H 'Content-Type: application/json' \
  -d '{"customerId":"00000000-0000-0000-0000-000000000001","orderTotal":49.99}'

curl -X POST http://localhost:5080/coupons/SUMMER25/redeem \
  -H 'Content-Type: application/json' \
  -d '{"customerId":"00000000-0000-0000-0000-000000000001","orderTotal":12.50}'

# Third attempt — rejected by the per-customer cap
curl -X POST http://localhost:5080/coupons/SUMMER25/redeem \
  -H 'Content-Type: application/json' \
  -d '{"customerId":"00000000-0000-0000-0000-000000000001","orderTotal":7.00}'
```

## Run the race test

The most interesting bit. Fires 50 concurrent redemption attempts for the same `(coupon, customer)` pair against a per-customer cap of 2 and asserts that **at most a small handful** land — the assertion is `<= 3` (cap plus bounded slack), not `== 2`:

```bash
dotnet test
```

Why `<= 3` and not `== 2`? On current Marten the DCB check is *optimistic and non-locking*: it stops runaway over-redemption but two genuinely-simultaneous writers can each pass the check before either commits, so the count can briefly sit a little over the cap. That's the **soft cap** the series discusses (Part 2 explains the mechanism; Part 4 covers making it exact, and the upcoming Marten fix that makes it hard by default). The invariant the test proves is *bounded, never wildly over*.

Tests use **Testcontainers**, so they spin up their own throwaway Postgres — no need to `docker compose up` first. Docker just needs to be running.

If DCB is doing its job, the count stays at the cap plus small slack. If you swap `FetchForWritingByTags` for a plain `QueryByTagsAsync` (no consistency check at all), the same test lets through 20, 30, sometimes all 50 redemptions — the classic event-sourcing race. *That's* the contrast: bounded vs. unbounded.

## Plain Marten vs Wolverine

The repo ships the same business rule in two flavors:

- **`src/DCB.CouponSample`** — plain Marten. The DCB cycle (`FetchForWritingByTags` → decide → `SaveChangesAsync` → `catch ConcurrencyException` → retry) is hand-written in `CouponRedeemer.RedeemAsync`. (It catches the base `ConcurrencyException`, which covers both the DCB `DcbConcurrencyException` and the per-stream conflict.) Use this while learning — you can *see* DCB happening.
- **`src/DCB.CouponSample.Wolverine`** — same rule via Wolverine.HTTP. The whole API is `[WolverinePost]` endpoints: redemption pairs a `Load(...)` returning the `EventTagQuery` with a `Post(..., [BoundaryModel] boundary)` that holds the decision, and Wolverine codegens the fetch / save / retry. Defining a coupon is also a `[WolverinePost]`, but a plain single-stream append with no DCB boundary. Production-grade ceremony cut.

Run them on different ports against the same Postgres:

```bash
dotnet run --project src/DCB.CouponSample             # http://localhost:5080
dotnet run --project src/DCB.CouponSample.Wolverine   # http://localhost:5081
```

## Compatibility note

Code targets **Marten 9.3.4** (and WolverineFx 6.x for the Wolverine project). The DCB API surface (`RegisterTagType` + `.ForAggregate<T>()`, `EventTagQuery`, `FetchForWritingByTags`, `DcbConcurrencyException`, `[BoundaryAggregate]`, `DcbStorageMode.HStore`) reflects that version.

Two behaviours are version-sensitive and worth knowing if a reviewer is on a different build:

- **Soft cap.** On 9.3.x the DCB consistency check is optimistic and non-locking, so concurrent same-tag appends can exceed the cap by a small margin (see the race test above and [Marten #4591](https://github.com/JasperFx/marten/issues/4591)). An upcoming release (expected 9.4.0) adds a serializing constraint that makes the cap hard by default — at which point the same code becomes an exact cap and the `<= 3` assertion could tighten to `== 2`.
- **`StreamIdentity`.** This sample uses the default `AsGuid` stream identity. Earlier 9.x builds needed `opts.Events.StreamIdentity = StreamIdentity.AsString` as a workaround for a `[BoundaryAggregate]` dispatcher bug; that is fixed in 9.3.4, so the line is not present here.

If you pin to a different Marten version and a signature has drifted, please open an issue.
