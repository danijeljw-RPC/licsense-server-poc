# Test suites

The test project deliberately separates the pre-roadmap regression baseline from
the Phase 0 executable specification.

Run the established baseline against an isolated PostgreSQL container:

```powershell
./scripts/Test-DatabaseAndAuth.ps1 -TestFilter 'Suite=Baseline'
```

Run the intentional-red roadmap suite:

```powershell
./scripts/Test-DatabaseAndAuth.ps1 -TestFilter 'Suite=Phase0Roadmap'
```

`Phase0Roadmap` tests are not skipped. Each test has an `ExpectedGreenStage`
trait naming the first roadmap stage expected to satisfy it:

- Stage 02: lifecycle, cancellation, expiry rules, signed expiry, and the
  online/offline invalidation boundary.
- Stage 03: reliable confirmation posting and visible offline limitations.
- Stage 04: database-allocated immutable IDs.
- Stage 05: generated activation-code format and one-time/negative-secret rules.
- Stage 06: mandatory normalized customer email and authoritative
  `metadata.contactEmail` persisted as `jsonb`.
- Stage 07: product/edition controls and exactly one entitlement per
  portal/API issuance.
- Stage 08: action-level authorization and direct-HTTP denial.

The HTTP request/response records in `RoadmapTestSupport.cs` are test contracts,
not substitute production implementations. They keep tests compile-safe while
later stages introduce the administrative issuance endpoints and domain types.
