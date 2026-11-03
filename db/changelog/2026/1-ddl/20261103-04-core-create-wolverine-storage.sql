--liquibase formatted sql

--changeset sso:20261103-04-core-create-wolverine-storage splitStatements:false
--comment: Wolverine's message storage for the runner (ADR 0010): the durable inbox and outbox, dead letters and the
--comment: node registry. Not part of the EF model: the DDL is Wolverine's own (WolverineFx 6.40.0), without the schema -
--comment: the runner checks these tables at start and creates nothing. An upgrade of Wolverine that changes them is a
--comment: new changeset from a new dump. Tables and sequences only. wolverine_node_records.timestamp is quoted.
CREATE SCHEMA wolverine;
REVOKE ALL ON SCHEMA wolverine FROM PUBLIC;
GRANT USAGE ON SCHEMA wolverine TO sso_runner;
ALTER DEFAULT PRIVILEGES IN SCHEMA wolverine GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO sso_runner;
ALTER DEFAULT PRIVILEGES IN SCHEMA wolverine GRANT USAGE, SELECT ON SEQUENCES TO sso_runner;

CREATE TABLE wolverine.wolverine_outgoing_envelopes (
    id              uuid                        NOT NULL,
    owner_id        integer                     NOT NULL,
    destination     varchar                     NOT NULL,
    deliver_by      timestamp with time zone    NULL,
    body            bytea                       NOT NULL,
    attempts        integer                     NULL DEFAULT 0,
    message_type    varchar                     NOT NULL,
CONSTRAINT pkey_wolverine_outgoing_envelopes_id PRIMARY KEY (id)
);

CREATE INDEX idx_wolverine_outgoing_envelopes_owner ON wolverine.wolverine_outgoing_envelopes USING btree (owner_id) WHERE (owner_id <> 0);

CREATE INDEX idx_wolverine_outgoing_envelopes_recover ON wolverine.wolverine_outgoing_envelopes USING btree (destination) WHERE (owner_id = 0);
CREATE TABLE wolverine.wolverine_incoming_envelopes (
    id                uuid                        NOT NULL,
    status            varchar                     NOT NULL,
    owner_id          integer                     NOT NULL,
    execution_time    timestamp with time zone    NULL DEFAULT NULL,
    attempts          integer                     NULL DEFAULT 0,
    body              bytea                       NOT NULL,
    message_type      varchar                     NOT NULL,
    received_at       varchar                     NULL,
    keep_until        timestamp with time zone    NULL,
CONSTRAINT pkey_wolverine_incoming_envelopes_id PRIMARY KEY (id)
);

CREATE INDEX idx_wolverine_incoming_envelopes_owner ON wolverine.wolverine_incoming_envelopes USING btree (owner_id) WHERE (owner_id <> 0);

CREATE INDEX idx_wolverine_incoming_envelopes_recover ON wolverine.wolverine_incoming_envelopes USING btree (received_at) WHERE (status = 'Incoming' AND owner_id = 0);

CREATE INDEX idx_wolverine_incoming_envelopes_keep_until ON wolverine.wolverine_incoming_envelopes USING btree (keep_until) WHERE (status = 'Handled');
CREATE TABLE wolverine.wolverine_dead_letters (
    id                   uuid                        NOT NULL,
    execution_time       timestamp with time zone    NULL DEFAULT NULL,
    body                 bytea                       NOT NULL,
    message_type         varchar                     NOT NULL,
    received_at          varchar                     NULL,
    source               varchar                     NULL,
    exception_type       varchar                     NULL,
    exception_message    varchar                     NULL,
    sent_at              timestamp with time zone    NULL,
    replayable           boolean                     NULL,
CONSTRAINT pkey_wolverine_dead_letters_id PRIMARY KEY (id)
);

CREATE INDEX idx_wolverine_dead_letters_replayable ON wolverine.wolverine_dead_letters USING btree (replayable) WHERE (replayable = true);
CREATE TABLE wolverine.wolverine_nodes (
    id              uuid                        NOT NULL,
    node_number     serial                      NOT NULL,
    description     varchar                     NOT NULL,
    uri             varchar                     NOT NULL,
    started         timestamp with time zone    NOT NULL DEFAULT now(),
    health_check    timestamp with time zone    NOT NULL DEFAULT now(),
    version         varchar                     NULL,
    capabilities    text[]                      NULL,
CONSTRAINT pkey_wolverine_nodes_id PRIMARY KEY (id)
);
CREATE TABLE wolverine.wolverine_node_assignments (
    id         varchar                     NOT NULL,
    node_id    uuid                        NULL,
    started    timestamp with time zone    NOT NULL DEFAULT now(),
CONSTRAINT pkey_wolverine_node_assignments_id PRIMARY KEY (id)
);

ALTER TABLE wolverine.wolverine_node_assignments
ADD CONSTRAINT fkey_wolverine_node_assignments_node_id FOREIGN KEY(node_id)
REFERENCES wolverine.wolverine_nodes(id) ON DELETE CASCADE;

CREATE TABLE wolverine.wolverine_control_queue (
    id              uuid                        NOT NULL,
    message_type    varchar                     NOT NULL,
    node_id         uuid                        NOT NULL,
    body            bytea                       NOT NULL,
    posted          timestamp with time zone    NOT NULL DEFAULT NOW(),
    expires         timestamp with time zone    NULL,
CONSTRAINT pkey_wolverine_control_queue_id PRIMARY KEY (id)
);
CREATE TABLE wolverine.wolverine_node_records (
    id             serial                      NOT NULL,
    node_number    integer                     NOT NULL,
    event_name     varchar                     NOT NULL,
    "timestamp"    timestamp with time zone    NOT NULL DEFAULT now(),
    description    varchar                     NULL,
CONSTRAINT pkey_wolverine_node_records_id PRIMARY KEY (id)
);
CREATE TABLE wolverine.wolverine_agent_restrictions (
    id      uuid       NOT NULL,
    uri     varchar    NOT NULL,
    type    varchar    NOT NULL,
    node    integer    NOT NULL DEFAULT 0,
CONSTRAINT pkey_wolverine_agent_restrictions_id PRIMARY KEY (id)
);
--rollback DROP TABLE wolverine.wolverine_agent_restrictions;
--rollback DROP TABLE wolverine.wolverine_node_records;
--rollback DROP TABLE wolverine.wolverine_control_queue;
--rollback DROP TABLE wolverine.wolverine_node_assignments;
--rollback DROP TABLE wolverine.wolverine_nodes;
--rollback DROP TABLE wolverine.wolverine_dead_letters;
--rollback DROP TABLE wolverine.wolverine_incoming_envelopes;
--rollback DROP TABLE wolverine.wolverine_outgoing_envelopes;
--rollback DROP SCHEMA wolverine;
