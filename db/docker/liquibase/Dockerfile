# Liquibase 5 ships without JDBC drivers - the PostgreSQL one is added at build time, not on every start.
FROM liquibase/liquibase:5.0.4
RUN lpm add postgresql --global
