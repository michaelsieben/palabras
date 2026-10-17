# Authentication

Accounts come only from the SQLite account CLI, never configuration or environment seeding.
See [account commands](../docs/04-accounts.md) and [cookie authentication](../docs/05-auth.md).
Development uses `Backend/Palabras/data`; create accounts with the same `DATA_DIR` as the server.
Production persists both SQLite and Data Protection keys under `/data`.
