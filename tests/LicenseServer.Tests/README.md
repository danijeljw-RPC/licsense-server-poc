# Test suites

The test project contains the original regression baseline and the now-complete roadmap
executable specification.

Run the established baseline against an isolated PostgreSQL container:

```powershell
./scripts/Test-DatabaseAndAuth.ps1 -TestFilter 'Suite=Baseline'
```

Run the roadmap contract suite:

```powershell
./scripts/Test-DatabaseAndAuth.ps1 -TestFilter 'Suite=Phase0Roadmap'
```

`Phase0Roadmap` tests are not skipped. Each test has an `ExpectedGreenStage` trait
naming the first roadmap stage that satisfied it. Later suites cover:

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
- Stages 09-13: users, scoped bearer credentials, complete admin API, transactional
  email, and customer magic links.
- Stage 14: verified/deduplicated Stripe inbox and lease recovery.
- Stage 15: purchase, renewal, grace, cancellation, plan, refund/dispute, mapping, and
  billing-operations policy.
- Stage 16: the full suite, standalone signing/activation scripts, and container smoke.

The HTTP request/response records in `RoadmapTestSupport.cs` are test contracts, not
substitute production implementations.
