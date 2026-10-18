# Palabras

Private Peninsular Spanish vocabulary PWA. Shipped: M0–M2, M5, M10; M7 backend. M6 in progress. Status: `docs/00-handoff.md` §7.

- React 19, strict TypeScript, Vite; .NET 10 minimal API; one Linux container.
- SQLite + EF Core (migrations `InitialAccounts`, `ContentSchema`, `SrsCards`, `DayStatusAndSkips`) and cookie auth exist. Council CLI lands in M3, reviewer UI in M4, push in M8.
- Run backend: `dotnet run --project Backend/Palabras --urls http://localhost:5080`.
- Run frontend: `cd Frontend && npm ci && npm run dev` (API proxy to :5080).
- Verify from root: `dotnet build Backend/Palabras.slnx`, `dotnet test Backend/Palabras.slnx`, `npm --prefix Frontend ci`, `npm --prefix Frontend run lint`, `npm --prefix Frontend run build`, `node tools/content-lint/index.js`, `docker build -t palabras:local .`.
- Persist future SQLite, keys and backups under `/data`. Never commit `.env` or data.
- User authorized Unraid setup and main-branch poll deployment for T-20261017-1516 step 18. Outside that explicit scope, deployment remains user-run only. Existing nginx proxy owns DNS/TLS; no proxy container here.
- Base Compose publishes `127.0.0.1:18480` for host checks. Unraid uses a standalone compose copied from `deploy/unraid/docker-compose.yml` to `/mnt/user/docker/palabras/docker-compose.yml`, building `/mnt/user/repos/palabras`. App joins external bridge `sieben` with alias `palabras`; existing `nginx-proxy-manager` forwards `http://palabras:18480`. No Unraid override, registry or CI image push.
- Future layers and browser features are added only in their milestones. Android Chrome only.
