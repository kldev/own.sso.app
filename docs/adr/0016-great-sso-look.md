# 0016. Great SSO's look

Status: accepted (2026-11-03)

## Context

The panel and the sign-in pages are what people see of Great SSO. They should look calm and trustworthy, read well in
a light and a dark theme, and need no front-end build in the image.

## Decision

- **Tailwind CSS v4** with shadcn-style tokens (`--primary`, `--sidebar`, ...) in `apps/Sso.Api/Styles/app.css`,
  built by the Tailwind CLI into `wwwroot/css/app.css` - committed, so images and CI need no Node (`npm install` once
  in `apps/Sso.Api` to rebuild it with the build).
- **The palette**: a teal accent (`#0f766e`) on neutral zinc greys, a white sidebar in the light theme and a deep one
  in the dark theme; destructive red, success green, warning amber. Every text and button pair passes **WCAG AA**
  (4.5:1) in both themes - the pairs and their ratios are written next to the tokens.
- **The logo**: a white "G" whose bar reaches into its centre - signing in - on a teal rounded square, with a small
  amber dot; `wwwroot/img/logo.svg` (also the favicon) and PNG icons without metadata.
- Light by default; the theme toggle is remembered per browser. Icons are Lucide, generated into C#
  (`tools/generate-icons.py`).

## Consequences

- A new colour is a token in both themes, checked for contrast.

## Revisit when

The operator wants their own brand (a theme per deployment).
