# Live Demo & Cloud Deployment Guide (`docs/demo/DEMO_GUIDE.md`)

## Hosting Options for Live Demo

Both `SerialLogHub.Api` and `SerialLogHub.Web` are containerized **.NET 8** applications ready for HTTPS cloud deployment using `deployment/Dockerfile.api` and `deployment/Dockerfile.web`.

| Hosting Provider | Free / Low-Cost Tier | HTTPS | Persistent SQLite | Notes |
| :--- | :--- | :---: | :---: | :--- |
| **Render.com** *(Recommended for Portfolio)* | Free Web Services (spins down on idle) or \$7/mo Starter | Automatic | Ephemeral on Free (auto-reseeded by `DbSeeder`); Persistent Disk on Starter | Ready-to-use blueprint in `deployment/render.yaml` |
| **Fly.io** | Pay-as-you-go (small VMs ~\$2–\$5/mo) | Automatic | Persistent Volumes supported | Fast cold-starts, Docker native |
| **Railway** | \$5/mo Hobby plan | Automatic | Persistent Volumes supported | Zero-config `docker-compose` / Dockerfile deploys |
| **VPS + Docker Compose** | \$4–\$6/mo (Hetzner / DigitalOcean) + Caddy/Nginx | Automatic (Let's Encrypt) | Local Docker volume (`serialloghub_data`) | Run `docker compose -f deployment/docker-compose.yml up -d` |

## Cloud Demo Mode Behavior

When deployed to the cloud (`DemoMode__Enabled=true` and `DemoMode__CloudVirtualDeviceResponder=true`):
1. Visitors sign in using the dedicated non-privileged demo operator account (`demo@serialloghub.example`, password configured via `DemoMode__DemoPassword` environment variable).
2. A clear **Simulation / Demo Mode** badge is displayed in the header so visitors know they are interacting with a simulated DataLogger (`COM99 (Virtual DataLogger - Demo)`), not physical hardware.
3. `CloudDemoResponderService` answers queued `COM99` commands (`TEST`, `WLB`, `WLJ`, `WLW`, `WLG`, `WLH0`, `WLH1`, `WLS0`, `WLE1`, `WLA`, `WLV1`, `WLM`) via the real API command queue so every dashboard and configuration screen works end-to-end in the browser.
4. Rate limiting (180 req/min/IP), read-only demo account protection (`updatename` blocked), and automatic 60-minute state resets protect the demo from abuse.

## Local Docker Compose Verification

```bash
cp .env.example .env
# Edit .env to set strong SERIALLOGHUB_API_KEY, SERIALLOGHUB_BRIDGE_KEY, and SERIALLOGHUB_DEMO_PASSWORD
docker compose -f deployment/docker-compose.yml up --build
```
