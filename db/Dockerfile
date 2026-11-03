# Liquibase with Great SSO's changelog inside - for a deployment that applies the schema from an image (ADR 0003).
# Built with db/ as the context (.github/workflows/image-db.yml): the repository's .dockerignore leaves db/ out of the other images. The same Liquibase as locally (db/docker/liquibase: 5.0.4 + the PostgreSQL driver).
FROM liquibase/liquibase:5.0.4
RUN lpm add postgresql --global
WORKDIR /liquibase/workspace
COPY --chown=1001:1001 changelog.yaml ./changelog.yaml
COPY --chown=1001:1001 changelog/ ./changelog/
