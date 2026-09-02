---
name: fullstack-app
description: Use when the user wants to build a complete application end-to-end, combining frontend, backend, and database. Covers project structure, API contracts, auth flow, environment config, and running the whole stack locally.
---

# Fullstack App

Guide the user to build a complete, working fullstack application from idea to running app.

## When to use
- User wants to build an entire product, MVP, SaaS, internal tool, or “an app that does X”.
- User asks for frontend + backend + database together, or “build me a ... app”.

## Workflow
1. Understand the goal in 1-2 sentences; if unclear, ask the most important questions only (what it does, who uses it, must-have features).
2. Propose a stack and structure. Sensible default: Next.js (React + API routes) + TypeScript + Tailwind + a database (SQLite/Postgres via Prisma) for quick start; or separate frontend/backend if the user prefers.
3. Scaffold both sides in one repository (monorepo) or clearly separated folders.
4. Define the data model and API contract first (endpoints, request/response shapes).
5. Build backend (models, auth, CRUD) then frontend (pages, components, API calls).
6. Wire authentication and shared types between front and back.
7. Add an `.env.example`, a README with run instructions, and seed data.
8. Run the full stack locally and verify a core happy-path flow works.
9. Summarize the architecture, how to run, and suggested next steps.

## Conventions
- Prefer one repository unless the user asks for separate repos.
- Share types/schemas between frontend and backend when possible.
- Keep setup reproducible: package scripts, env example, README.
- Commit logically only when the user asks.
