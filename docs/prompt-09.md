# Stage 09 of 16 — Build operator and service-account administration

Read `docs/README.md` in full. This is stage 09 after permission-based RBAC. Inspect Identity entities/endpoints, role seeding, security-stamp behavior, MFA/passkeys, current registration flow, tests, and the working tree. Preserve unrelated changes. Implement secure user administration rather than a cosmetic page.

Build `/settings/users` protected by `users.read`/`users.manage` as appropriate. Provide list/search, invite/create, disable/enable, role assignment, permission inspection, MFA status, forced password reset/setup, and API-credential ownership placeholders for stage 10. Distinguish human operators from service accounts so production automation does not require a person's permanent identity.

Prefer invitation links and forced password setup; an administrator must not choose another user's reusable password. Tokens must be short-lived, single-use through Identity's protected token mechanisms, and never logged. If the configured mail sender is still unavailable, provide a development-only one-time reveal/test capture that cannot activate in production and will be replaced in stage 12.

Only `users.manage` may mutate users or roles. Prevent disabling, deleting, or demoting the final enabled System Administrator, including concurrent requests. Disabling a user must update the security stamp, invalidate sessions promptly, and expose a shared revocation hook that stage 10 will use for owned API keys. Audit invitations, enable/disable, role changes, password-reset initiation/completion, and relevant security events without secrets. Do not expose password hashes, authenticator secrets, recovery codes, passkey material, security stamps, or raw tokens.

Keep existing MFA, recovery-code, passkey, forced-seed-password, and lockout flows working. Add tests for authorization, final-admin protection under concurrency, session invalidation, role changes, service-account restrictions, token expiry/single-use, audit redaction, and safe projections.

Run targeted Identity/UI/HTTP tests, the full suite, and a browser smoke test for common user-admin flows. Finish with exact commands/results and note the temporary email-development mechanism, if any, that stage 12 must remove.
