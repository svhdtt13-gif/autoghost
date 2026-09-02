---
name: web-frontend
description: Use when building or scaffolding the user interface of a web application with React, Next.js, Vue, Svelte, or Angular. Covers component structure, styling, routing, state management, and connecting to APIs.
---

# Web Frontend

Guide the user to build modern, responsive web frontends.

## When to use
- User wants to build a website, dashboard, landing page, SPA, or UI.
- User mentions React, Next.js, Vue, Svelte, Angular, Tailwind, component, page, form.

## Workflow
1. Ask clarifying questions only if the stack is unknown: framework preference, TypeScript or JS, styling approach (Tailwind / CSS Modules / styled-components), and whether it needs a backend.
2. Scaffold the project: prefer official CLIs (`create-next-app`, `npm create vite@latest`, `npm create vue@latest`).
3. Establish a clean folder structure: `components/`, `pages/` or `app/`, `hooks/`, `lib/`, `styles/`.
4. Build reusable, accessible components. Keep components small and focused.
5. Implement routing and global state (Context, Zustand, Redux, Pinia) as needed.
6. Connect to APIs via fetch/axios; handle loading, error, and empty states.
7. Make it responsive and mobile-friendly. Verify with a local dev server (`npm run dev`).
8. Summarize what was built and how to run it.

## Conventions
- Prefer TypeScript when the user doesn't specify.
- Prefer Tailwind CSS for fast, consistent styling unless told otherwise.
- Always handle edge cases (loading, errors, empty data).
