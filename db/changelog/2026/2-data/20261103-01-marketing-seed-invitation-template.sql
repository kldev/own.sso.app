--liquibase formatted sql

--changeset sso:20261103-01-marketing-seed-invitation-template splitStatements:false
--comment: The first template of module marketing (ADR 0014): the invitation to Great SSO in English. Plain text; an
--comment: SSO admin edits it in the panel, and every mailing keeps its own copy. A fixed id - the same row in every
--comment: environment; ON CONFLICT keeps a re-run harmless.
INSERT INTO marketing.templates (id, code, product, language, name, subject, body) VALUES
(
    '019a4a00-0000-7000-8000-000000000001', 'great-sso-en', 'great-sso', 'en', 'Great SSO - invitation',
    'Your team now signs in with Great SSO',
    $body$Hello,

your team now signs in to its applications with Great SSO - one sign-in for all of them.

There is no new password to remember: you sign in with the GitHub, Google or work Microsoft account your admin has
entered for you, on that provider's own page. Great SSO never asks for a password.

To start, open https://sso.example.com and choose the account you use at work. The applications of your team will
then let you in without signing in again.

If you cannot sign in, or you want to use another account, reply to this e-mail - your SSO admin will change it.

Kind regards
Your Great SSO admin
https://sso.example.com

--
You receive this e-mail because your SSO admin entered your address. If you do not want to hear from us again, reply
"unsubscribe".$body$
)
ON CONFLICT (id) DO NOTHING;
--rollback DELETE FROM marketing.templates WHERE id = '019a4a00-0000-7000-8000-000000000001';
