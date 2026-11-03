# 0001. One operator, no tenants

Status: accepted (2026-11-03)

## Context

Great SSO is the sign-in service of one team or company: its admins enter the people who may sign in and register the
applications those people use. Nothing in it belongs to a customer of that team.

## Decision

- One operator per deployment, no organisation column, no tenant filter, no row-level security.
- Two roles of a person: `admin` runs the panel (people, applications, activity, marketing, MCP tokens) and `member`
  only signs in to applications. Every operation of the API reads the caller from the database and checks the role
  (`Actors`, `Actor.RequireAdmin`).
- A second team is a second deployment (its own database, its own issuer).

## Consequences

- Queries need no tenant condition; a missing row is a plain 404.
- The operator's name and website are configuration (`Operator:*`), shown on every public page.

## Revisit when

Two teams have to share one deployment and still be kept apart.
