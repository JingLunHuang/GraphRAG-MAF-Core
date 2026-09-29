FROM ubuntu:24.04 AS plugin
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl unzip && rm -rf /var/lib/apt/lists/*
ARG GDS_VERSION=2.13.12
ARG GDS_SHA256
RUN test -n "$GDS_SHA256" && curl --fail --location "https://graphdatascience.ninja/neo4j-graph-data-science-${GDS_VERSION}.zip" -o /tmp/gds.zip \
    && echo "${GDS_SHA256}  /tmp/gds.zip" | sha256sum --check \
    && unzip /tmp/gds.zip -d /gds
FROM neo4j:5.26.31-community
COPY --from=plugin /gds/neo4j-graph-data-science-*.jar /plugins/
