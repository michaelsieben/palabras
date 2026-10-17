# Palabras

Private Peninsular Spanish vocabulary PWA. M0 contains only an API/SPA scaffold.

- React 19, strict TypeScript, Vite; .NET 10 minimal API; one Linux container.
- SQLite + EF Core are planned for M2+. No DB, auth, SRS or push code in M0.
- Run backend: `dotnet run --project Backend/Palabras --urls http://localhost:5080`.
- Run frontend: `cd Frontend && npm ci && npm run dev` (API proxy to :5080).
- Verify from root: `dotnet build Backend/Palabras.slnx`, `dotnet test Backend/Palabras.slnx`, `npm --prefix Frontend ci`, `npm --prefix Frontend run lint`, `npm --prefix Frontend run build`, `docker build -t palabras:local .`.
- Persist future SQLite, keys and backups under `/data`. Never commit `.env` or data.
- Unraid deployment is user-run only. Existing nginx proxy owns DNS/TLS; no proxy container here.
- Compose binds loopback :18480. A containerized proxy cannot reach host loopback directly: user must join a suitable shared Docker bridge network before public routing.
- Future layers and browser features are added only in their milestones. Android Chrome only.
