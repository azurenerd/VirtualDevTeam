---
version: "1.0"
description: "Guidance for generating developer-experience configuration so projects work out of the box after clone"
tags:
  - engineer-base
  - scaffolding
  - dev-experience
---
## Developer-Experience Configuration for T1 Scaffolding

Every project MUST work out of the box after a fresh `git clone` followed by the standard build-and-run commands for its stack. Agents set environment variables programmatically at runtime, but end users run `dotnet run`, `npm start`, `python manage.py runserver`, etc. from a cold terminal. If the project requires invisible environment setup to function, it is broken for every human who clones it.

### The Principle

Create the **development-mode configuration files** that the framework expects so that the default developer workflow just works:

```
git clone <repo>
cd <repo>
<install dependencies>
<run>
→ App starts in development mode on a known port with working defaults
```

### What to Create (by Stack)

Analyze the `{{tech_stack}}` and create ALL applicable configuration files:

1. **Development environment activation** — The file or setting that tells the framework to run in development mode. Without it, frameworks default to production and disable debug tooling, static asset serving, developer exception pages, and hot reload.
   - ASP.NET Core: `Properties/launchSettings.json` with `ASPNETCORE_ENVIRONMENT=Development`
   - Node.js/Express: `NODE_ENV=development` default in `package.json` scripts or `.env`
   - Python/Django: `DEBUG=True` in `settings/development.py` or `.env`
   - Python/Flask: `.flaskenv` with `FLASK_ENV=development`
   - Java/Spring: `application-dev.properties` or `application-dev.yml` with `spring.profiles.active=dev`
   - Ruby/Rails: development is the default — ensure `database.yml` has a working dev config
   - Go: `Makefile` with `dev` target or `.env` with `APP_ENV=development`

2. **Default ports and URLs** — Hard-code sensible development ports so the app starts without port conflicts or "address in use" errors. Document them if non-standard.
   - Web backends: pick a port in the 5000-5199 range (or the framework's conventional default)
   - Frontend dev servers: pick the framework's default (3000 for React, 5173 for Vite, 4200 for Angular, 8080 for Vue CLI)
   - If both frontend and backend exist: they must use DIFFERENT ports, and the frontend's proxy config must point to the backend's port

3. **Dev-mode feature toggles** — Enable features needed for local development:
   - Swagger/OpenAPI UI for API projects
   - Hot reload / watch mode in dev scripts
   - Detailed error pages (developer exception page, stack traces)
   - CORS permissive policy for local frontend-to-backend calls
   - HTTPS redirect disabled for local development (use HTTP)

4. **Database and data defaults** — If the app uses a database:
   - Default to a local/file-based database in dev (SQLite, H2, embedded Postgres) unless architecture says otherwise
   - Connection string must work without external setup (no "configure your SQL Server first")
   - Seed data runs idempotently on startup (see startup idempotency rule in implementation prompts)

5. **README setup instructions** — Include a `## Getting Started` section (or equivalent) documenting:
   - Prerequisites (runtime versions, required tools)
   - How to install dependencies
   - How to run the app
   - What URL to open in the browser
   - Any required first-time setup (database migration, API keys for external services)

### Multi-Component Projects

For projects with separate frontend and backend:
- Each component gets its OWN dev config (e.g., `launchSettings.json` for the API AND `.env.development` for the React frontend)
- A root-level `README.md` documents how to start BOTH components
- Consider a root `package.json` with workspace scripts or a `Makefile` that starts everything with one command

### Do NOT

- **Commit real secrets** — Use placeholder values (`your-api-key-here`, `change-me`) and document where to get real ones
- **Hardcode absolute paths** — All paths must be relative to the project root
- **Require external services to start** — Dev mode should work offline with mocks or embedded alternatives
- **Rely on environment variables being set externally** — Dev config files exist precisely so developers don't need to `export FOO=bar` before running
- **Create `.env` files with production values** — Development configs are for development; production deployment is a separate concern
- **Ignore dev config files in `.gitignore`** — These files MUST be committed so they work for every developer who clones. The ONLY exception is `.env.local` / `.env.*.local` which contain per-developer overrides (API keys, custom ports)

### Why This Matters

Without these files:
- Frameworks default to production mode → static assets from component libraries (CSS, JS) may not be served → the app looks broken
- No default port → startup fails or conflicts with other services
- No connection string → database errors on first run
- No README → every new developer wastes 30 minutes figuring out how to start the project
