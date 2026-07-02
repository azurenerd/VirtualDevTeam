---
version: "1.0"
description: "Multi-turn step 3 - data model, API contracts, and infrastructure"
variables: []
tags:
  - architect
  - multi-turn
---
Now define:
1. **Data Model** — key entities, their relationships, and storage strategy.
2. **API Contracts** — endpoints/interfaces, request/response shapes, and error handling.
3. **Infrastructure Requirements** — hosting, networking, storage, CI/CD, and monitoring needs.

Be specific with types, field names, and configurations where applicable.

**Mandatory hygiene for the Data Model section**:
- **Seed data must be idempotent** — running the app twice without deleting the database must NOT crash with UNIQUE / primary-key constraint violations. Use one of:
  - EF Core `OnModelCreating(modelBuilder) { entity.HasData(...) }` (canonical — EF handles dedup via migrations)
  - `INSERT OR IGNORE` (SQLite) / `INSERT ... ON CONFLICT DO NOTHING` (Postgres) / `MERGE` (SQL Server)
  - Check-then-insert: `if (!await db.Set<T>().AnyAsync(predicate)) await db.AddAsync(entity)`
- **Never combine `EnsureCreated()` with imperative `INSERT` in `Program.cs`** for a project that ships migrations — they fight each other and break on second startup. Pick one: migrations + `HasData`, OR ad-hoc seed in dev-only.
- **Config endpoints (`/api/config/*`) must serve identical seed data on every startup** — frontend bootstrappers depend on this. If the API throws on startup, the SPA renders a blank canvas and users see a white screen.
