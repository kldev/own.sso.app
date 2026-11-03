# 0013. MCP for Claude Code, opened by an admin's personal token

Status: accepted (2026-11-03)

## Context

An admin should be able to configure Great SSO from Claude Code as fully as from the panel ("MCP everywhere").

## Decision

- **`/mcp`** with `ModelContextProtocol.AspNetCore` (streamable HTTP, stateless).
- **Every operation of the panel is a tool** (`apps/Sso.Api/Mcp/SsoTools.cs`, `MarketingTools.cs`): people, applications,
  the log and sessions, templates and mailings. A tool is a thin call of **the same handler** as the API endpoint
  (`McpToolRunner`), as the token's person - every rule, role check and log entry is the same; a refusal reaches the
  agent as a tool error with the rule's sentence and code. A new panel feature gets its tool.
- **A personal MCP token** (`identity.mcp_tokens`): an admin makes it on the panel's Claude Code page (`mcp_` + 256
  random bits, shown once with the ready `claude mcp add --transport http great-sso <issuer>/mcp ...` command),
  1-365 days, revocable; only its SHA-256 is stored. It carries no role: every request checks that its person is still
  an active admin.
- The `McpToken` scheme is bound to `/mcp` alone: the panel never accepts an MCP token and `/mcp` never accepts a
  session. Issuing and revoking tokens is not a tool.
- Every tool call is an `mcp_tool` event (tool, token name, outcome, code of a refusal - never the arguments).

## Consequences

- A leaked token can do what the panel can, as that admin, until revoked or expired - including creating client
  secrets, which return into the agent's conversation by design.

## Revisit when

OAuth 2.1 for MCP clients (Great SSO is an OpenID Connect provider already) or clients other than Claude Code.
