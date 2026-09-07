# Security Hotfix — DEC-034

Date: 2026-09-06

## Incident scope

The P3/P3.1 evidence package contained an OIDC/JWT-looking `id_token` inside
captured title URL strings. The affected tracked files were:

- `artifacts/p3-canonical-preflight-2026-09-06.json`
- `artifacts/p3.1-ui-evidence-2026-09-06.json`

The token value and complete auth URL are not reproduced in this document.

## Remediation

- Replaced every affected auth URL occurrence with `[REDACTED_AUTH_URL]`.
- Re-scanned all 103 local JSON/log/text evidence files; 28 local files were
  redacted and 47 auth-URL occurrences were removed.
- Re-scanned all GitHub-tracked artifact evidence; no auth/JWT match remains.
- Extended `.github/workflows/ci.yml` with global high-confidence JWT/auth-URL
  checks and a dedicated P0-P3 evidence scan for id/access/refresh tokens,
  session/cookie credentials, bearer credentials, auth URLs, and JWTs.
- Kept the existing build-output/cache and secret scan.

## Credential invalidation

Removing a value from the current tree does not remove it from Git history.
The exposed value must be invalidated at the issuing identity/session provider
and a fresh session must be established. This repository has no provider-side
credential-revocation API or authority, so provider-side revoke/rotation is an
explicit operator action and is not claimed as completed by this commit.

Do not paste the exposed value into an issue, log, commit message, or command
output. After provider-side invalidation, verify that the old session/token is
rejected before any P4 authorization is considered.

## Status boundary

```text
P0 PASS
P1 PASS
P2 PASS
P3 Foundation PASS
P3 Canonical FINAL PASS
Security hotfix REQUIRED until provider-side invalidation is confirmed
P4+ UNAUTHORIZED
```
