# Stage 03 of 16 — Repair lifecycle administration UI

Read `docs/README.md` in full. This is stage 03 after the lifecycle domain work. Inspect current pages, render modes, handlers/endpoints, services, CSS, tests, and the dirty working tree. Preserve unrelated work and use the domain services from stage 02; do not reimplement lifecycle rules in Razor components.

Make `/licenses/{licenseId}` a reliable, permission-ready lifecycle screen. Fix the currently non-responsive red actions caused by static rendering: confirmation controls must actually affect submit behavior, but confirmation must also be enforced on the server. Use the smallest appropriate interactive render scope or a robust form-post handler. Cookie mutations require authorization, antiforgery, explicit confirmation, a reason of the required length, and a concurrency/version check regardless of browser state.

Add separate panels for editing expiry, seats, and updates-until; cancelling a never-activated license; revoking a non-cancelled license; and existing activation/deactivation operations. Add edition editing only if the controlled stage-07 allowlist already exists; never add a free-text edit field. Cancel must disappear or explain why it is unavailable after any activation history. Show validation errors inline, refresh state immediately after success, prevent repeat mutation, and show a useful conflict/reload message on stale versions. Render the perpetual sentinel as `Never (perpetual)`, never as year 9999.

Show customer name/email placeholders compatible with the later email stage, immutable history with old/new values, permission-gated actions, and the offline limitation next to cancellation/revocation/expiry changes. Do not imply a downloaded offline license has been recalled. Keep secrets out of rendered markup after use and do not regress token/device handling.

Add component/HTTP/browser-level coverage proving confirmation posting, missing/short reason feedback, antiforgery, stale-version conflict, successful cancellation/revocation/expiry update, state refresh, and direct unauthorized-call rejection. Use an actual browser test only where DOM interactivity cannot be proven reliably below that level.

Run targeted UI/HTTP tests, the full suite, and a manual or automated smoke check against the disposable container stack. Finish with the exact tested flows and results; leave unrelated future issuance/catalog work for later prompts.
