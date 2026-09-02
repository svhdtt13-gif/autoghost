---
name: web-backend
description: Use when building the server side of a web application: REST/GraphQL APIs, authentication, databases, and deployment with Node/Express, Fastify, Python (Django/Flask/FastAPI), or Go. Covers data models, CRUD, validation, and security.
---

# Web Backend

Guide the user to build robust, secure backend services and APIs.

## When to use
- User wants to create an API, server, microservice, admin panel, or database-backed service.
- User mentions Node, Express, Fastify, Python, Django, Flask, FastAPI, Go, Postgres, MySQL, MongoDB, auth, REST, GraphQL.

## Workflow
1. Clarify: language/runtime, database choice, auth needs (JWT / session / OAuth), and expected endpoints.
2. Scaffold the project and a clear structure: `routes/` or `api/`, `models/`, `services/`, `middleware/`, `config/`.
3. Define data models and migrations; use an ORM/ODM (Prisma, SQLAlchemy, GORM) when appropriate.
4. Implement CRUD endpoints with input validation and consistent error responses.
5. Add authentication and authorization; never store plaintext passwords (bcrypt/argon2).
6. Add basic security: CORS, rate limiting, input sanitization, environment-based secrets.
7. Provide a way to run locally and seed/test data.
8. Summarize endpoints, how to run, and how to connect from the frontend.

## Conventions
- Keep secrets in environment variables, never hardcode.
- Return structured JSON errors with proper HTTP status codes.
- Prefer typed schemas (Zod, Pydantic) for validation.
