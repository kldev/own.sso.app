--liquibase formatted sql

--changeset sso:20261103-05-ef-attempts-default-zero splitStatements:false
--comment: ef-migration 20261103164416_AttemptsDefaultZero
--comment: Exported by db/scripts/export-ef-migration.sh from libs/Sso.Data/Migrations - do not edit by hand.
ALTER TABLE marketing.recipients ALTER COLUMN attempts SET DEFAULT 0;

ALTER TABLE messaging.outbox_messages ALTER COLUMN attempts SET DEFAULT 0;
--rollback ALTER TABLE marketing.recipients ALTER COLUMN attempts DROP DEFAULT;
--rollback ALTER TABLE messaging.outbox_messages ALTER COLUMN attempts DROP DEFAULT;
