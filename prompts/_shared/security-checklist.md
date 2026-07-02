---
description: Web feature security checklist — apply when the change touches user input, auth, or external IO
tags:
  - shared
  - security
---

## Security checklist

This checklist applies whenever a change touches user input, authentication, sessions, file uploads, external service calls, or anything that crosses a network boundary. Skip it for purely static content, internal tooling, or test infrastructure.

### Defaults to follow

- **Trust nothing crossing a boundary.** Every API route, form handler, query string, header, file upload, and webhook payload gets validated at the entry point.
- **Parameterize every database query.** No string concatenation of user input into SQL, NoSQL queries, or shell commands.
- **Encode output for the rendering medium.** Use the framework's built-in escaping (Razor's `@`, JSX's brace expressions). Avoid bypasses like `MarkupString`, `dangerouslySetInnerHTML`, and direct `innerHTML` assignment with user data.
- **HTTPS for every external call.** Reject anything that downgrades silently to HTTP.
- **Hash credentials with vetted KDFs.** bcrypt, scrypt, or argon2 — never plaintext, base64, MD5, or homegrown hashes.
- **Set the standard security headers.** CSP, HSTS, X-Frame-Options, X-Content-Type-Options, Referrer-Policy.
- **Cookies must be httpOnly + Secure + SameSite** for session and identity tokens.
- **Treat partner/integration data as untrusted** even when it comes from a "trusted" upstream.

### Decisions that require a checkpoint before shipping

These categories should NOT slip through silently — at minimum, call them out in the PR description:

- New auth flow or change to existing auth logic.
- New category of sensitive data (PII, PHI, payment, biometric).
- A new external service integration of any kind.
- CORS configuration changes.
- File upload endpoints.
- Rate-limit or throttling adjustments.
- Code that grants elevated permissions.

### Hard "do not" list

- **Never commit secrets to version control.** Use a secret manager or per-user store. (`appsettings.json` is tracked in this repo — secrets placed there will leak.)
- Never log passwords, tokens, full payment numbers, or PII as plain text.
- Never trust client-side validation as a security boundary; re-validate server-side.
- Never disable security headers "for a test" — use a per-environment override instead.
- Never call `eval` (or equivalent) on user-provided strings, and never set `innerHTML` from one.
- Never store auth tokens in `localStorage`; use httpOnly cookies for sessions.
- Never expose stack traces in HTTP responses; log them server-side and return a generic error code.
- Never perform side effects (create user, change password, send email) from a GET handler.

### Quick OWASP Top 10 sweep before publishing

- [ ] **Injection** — every DB call parameterized; no shell-out string concatenation.
- [ ] **Broken Authentication** — sessions short-lived; password reset flows verified.
- [ ] **Sensitive Data Exposure** — no PII in logs; HTTPS-only.
- [ ] **XML External Entities** — XML parsing disables external entities (or use JSON instead).
- [ ] **Broken Access Control** — every protected endpoint checks AUTHORIZATION, not just authentication.
- [ ] **Security Misconfiguration** — default credentials removed; debug-mode off in production.
- [ ] **Cross-Site Scripting** — output encoded; CSP set.
- [ ] **Insecure Deserialization** — no `BinaryFormatter`, no `pickle.loads(untrusted)`, no Java serialization of untrusted streams.
- [ ] **Components with Known Vulnerabilities** — dependency manifests audited.
- [ ] **Insufficient Logging & Monitoring** — auth failures and admin actions logged.

### Static-site / local-only escape hatch

If your task ships a purely static site or a local-only HTML page (no backend, no auth, no sensitive data, no external IO), skip the OWASP sweep above and apply only the "Hard do not" list — those rules still apply (no `eval`, no `innerHTML` from untrusted strings, no `localStorage` for any token).
