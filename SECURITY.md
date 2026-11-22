# Security Policy (`SECURITY.md`)

## Reporting a Vulnerability

If you discover a security vulnerability in **SerialLog Hub**, please do not open a public issue containing exploit details. Instead, report it privately to the repository maintainer via GitHub Security Advisories.

## Implemented Security Controls (.NET 10 LTS)

1. **Fail-Closed Operator Authorization & BOLA/IDOR Prevention (`OperatorScopeGuard`):**
   - Null authentication contexts, missing operator identity headers, or bare unverified `X-OPERATOR-EMAIL` headers fail closed with `401 Unauthorized`.
   - Desktop Bridge instances authenticate via `POST /api/auth/login` to receive a 256-bit operator-bound session token (`X-OPERATOR-TOKEN`) stored as a `sha256:<hex>` hash in `ApiKeys` (`OwnerEmail = user.Email`, `ClientType = "OperatorSession"`).
   - Web Application backend proxy requests are cryptographically signed with `HMAC-SHA256(email|unixTimestamp)` (`X-OPERATOR-PROOF` + `X-OPERATOR-TIMESTAMP` with a $\pm 300\text{s}$ replay window).
   - Any cross-operator access attempt (`OwnerEmail != targetEmail`) is rejected with `403 Forbidden`.
2. **Production Configuration Validation (`ProductionSecurityValidator`):**
   - Fails startup safely (`InvalidOperationException`) in Production if `ApiSecurity:RequireApiKey` is `false`, `ApiSecurity:ApiKey` is shorter than 16 characters, or any development fallback secret (`dev-serialloghub-api-key`, `CHANGE_ME_IN_PRODUCTION`, `Demo1234!`) is configured.
3. **Salted PBKDF2 Password Storage (`PasswordSecurityService`):**
   - Passwords are stored using ASP.NET Core `PasswordHasher<string>` with unique per-user salts.
4. **Constant-Time Secret Verification:**
   - API keys, Bridge webhook keys (`X-BRIDGE-KEY`), HMAC proofs, and password reset tokens are verified with `CryptographicOperations.FixedTimeEquals`.
5. **Brute-Force Rate Limiting & HTTP Security Headers:**
   - Authentication endpoints enforce a strict `"auth-brute-force"` policy (`10 req/min` per IP), alongside a global `180 req/min` limiter, `5 MB` request body cap, and `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy`, and `X-Permitted-Cross-Domain-Policies: none`.
