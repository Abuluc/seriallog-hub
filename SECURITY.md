# Security Policy

## Reporting a Vulnerability

If you discover a security vulnerability in **SerialLog Hub**, please do not open a public issue containing exploit details. Instead, report it privately to the repository maintainer via GitHub Security Advisories.

## Implemented Security Controls

- **Zero Direct Client Database Access:** Both `SerialLogHub.Web` and `SerialLogHub.Desktop` communicate exclusively through `SerialLogHub.Api`.
- **Salted PBKDF2 Password Storage:** Passwords are stored using ASP.NET Core `PasswordHasher<string>` with unique per-user salts.
- **API Key & Bridge Key Isolation:** Backend endpoints require `X-API-KEY`; Desktop-to-Web SignalR webhooks (`/Home/AppCheck`, `/Home/waitresponse`) require `X-BRIDGE-KEY`.
- **Signed Cookie Claims & Authorized SignalR Hub:** User identity is resolved exclusively from signed ASP.NET Core authentication claims (`User.Identity.Name`), never from client-modifiable cookies.
- **Rate Limiting:** Fixed-window per-IP rate limiting (180 requests/min) is enabled on both `SerialLogHub.Api` and `SerialLogHub.Web`.
- **Demo Mode Isolation:** In public demo deployments, the shared demo account cannot be modified (`updatename` blocked), and transient command/history tables automatically reset on a schedule.
- **Secret Hygiene:** No real credentials, IP addresses, certificates, or `.db` files are committed to source control. Use environment variables or secret managers in production.
