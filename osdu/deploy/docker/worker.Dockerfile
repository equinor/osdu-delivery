# An OSDU Delivery compute node as a container: the pull-based drain loop that polls the control plane's dispatcher
# for work, with the OSDU Delivery module installed so it can execute every OSDU flow kind (delivery, retrieval,
# cache, assertion, dimension and inventory) as well as SQLFlow's own.
#
# The image publishes osdu/hosts/SqlFlow.Delivery.Worker.Host. The run's definition, its YAML and its trace travel
# over the node protocol. It needs OUTBOUND HTTPS to the control plane, OUTBOUND SQL to the catalog database (for the
# `osdu` schema alone) and to the ingestion database its flows read, outbound git to the repository remotes (for
# SHA-pinned runs without a snapshot), and outbound HTTPS to the OSDU endpoint; it exposes nothing, so it never sits
# behind an ingress. Scale it on the control plane's replica target (osdu/deploy/k8s for the KEDA setup), not on
# traffic.
#
# Configuration is environment-only, matching the node's "credentials live on the node" model:
#   SQLFLOW_URL                   the control plane's URL (required)
#   SQLFLOW_TOKEN                 the node credential: a token minted with the 'node' scope, or a ${env:...} /
#                                 ${keyvault:...} reference to one (required)
#   SQLFLOW_CATALOG_DB            the catalog database's connection, which the ledger in the `osdu` schema is read
#                                 and written through (required: without it the node refuses to start)
#   SQLFLOW_WORKER_POOL           comma-separated pools this node serves (optional; empty = untargeted runs only)
#   SQLFLOW_WORKER_POLL_SECONDS   queue poll cadence (optional)
#   SQLFLOW_WORKER_DRAIN_SECONDS  how long a stopping node finishes what it holds (optional)
#   SQLFLOW_GIT_TOKEN             token for private git remotes (optional)
#   plus every ${env:...} reference the flows themselves use (the ingestion database connection, the OSDU
#   credentials).
#
# Build from the REPOSITORY ROOT (the context must span osdu/ and sqlflow/):
#
#   docker build -f osdu/deploy/docker/worker.Dockerfile -t osdu-delivery-worker:latest .

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
# Framework-dependent publish; building inside the linux SDK image restores the linux-x64 native assets the engine
# carries (LibGit2Sharp for materialization), so they ship under runtimes/linux-x64.
RUN dotnet publish osdu/hosts/SqlFlow.Delivery.Worker.Host/SqlFlow.Delivery.Worker.Host.csproj \
    -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS runtime
# LibGit2Sharp's bundled native git needs OpenSSL present on the Debian runtime image for HTTPS remotes.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libssl3 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
COPY osdu/deploy/docker/worker-entrypoint.sh /worker-entrypoint.sh
# The CR strip keeps the shebang valid when the build context comes from a Windows checkout (CRLF would make the
# kernel look for /bin/sh\r and fail the container with "no such file or directory").
RUN sed -i 's/\r$//' /worker-entrypoint.sh && chmod +x /worker-entrypoint.sh
ENV DOTNET_RUNNING_IN_CONTAINER=true
USER $APP_UID
ENTRYPOINT ["/worker-entrypoint.sh"]
