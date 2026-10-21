# The OSDU Delivery MCP server as a container: `osdu-delivery-mcp http`, the streamable HTTP endpoint the control
# plane's chat assistant and any remote MCP client connect to. It is SQLFlow's MCP server composed with the delivery
# module (osdu/hosts/osdu-delivery-mcp): a thin authenticated proxy over the control plane's /api/v1 and the
# documentation compiled into it. It holds NO credentials: it forwards each request's bearer token to the control
# plane, which enforces scopes, and it answers from metadata alone (osdu/docs/reference/guides/mcp.md).
#
# Configuration is environment-only:
#   SQLFLOW_CONTROL_PLANE_URL        base URL of the control plane the tools call (required)
#   SQLFLOW_GUI_URL                  public base URL of the GUI; results carry links into it (a record's page, a
#                                    submission's, a run's). Unset emits root-relative links, which resolve only for
#                                    a client rendering inside the GUI itself
#   SQLFLOW_MCP_HTTP_BIND            listen address (the image sets 0.0.0.0:8080)
#   SQLFLOW_MCP_HTTP_ALLOWED_HOSTS   optional Host-header allowlist; unset disables the check, which is safe here
#                                    because every /mcp request must carry a bearer token
#   SQLFLOW_MCP_LOG                  log filter (optional; default info)
#
# Build from the REPOSITORY ROOT:
#   docker build -f osdu/deploy/docker/mcp.Dockerfile -t osdu-delivery-mcp:latest .

# The build stage runs the Debian release the runtime stage runs (bookworm): a binary linked against a newer glibc than
# the runtime's does not start, and rust:<version>-slim names the newest Debian, not bookworm.
FROM rust:1.94-slim-bookworm AS build
WORKDIR /src
# The server compiles its documentation and the key census into the binary through repository-relative paths, so the
# build keeps the repository's shape: SQLFlow's server and language engine with the reference pages they embed, and
# the host crate with the product's reference pages, wiki and census (osdu/docs). The root .dockerignore
# re-includes these documentation trees for this.
COPY sqlflow/tools/ sqlflow/tools/
COPY sqlflow/docs/reference/ sqlflow/docs/reference/
COPY sqlflow/docs/wiki/ sqlflow/docs/wiki/
COPY osdu/hosts/osdu-delivery-mcp/ osdu/hosts/osdu-delivery-mcp/
COPY osdu/docs/ osdu/docs/
# --locked: the build is the one the committed lock file names, which is the one the tests ran against.
RUN cargo build --release --locked --manifest-path osdu/hosts/osdu-delivery-mcp/Cargo.toml

FROM debian:bookworm-slim AS runtime
# TLS trust roots for the outbound rustls connection to the control plane.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --system --uid 10001 --create-home delivery
COPY --from=build /src/osdu/hosts/osdu-delivery-mcp/target/release/osdu-delivery-mcp /usr/local/bin/osdu-delivery-mcp
ENV SQLFLOW_MCP_HTTP_BIND=0.0.0.0:8080
EXPOSE 8080
USER delivery
ENTRYPOINT ["/usr/local/bin/osdu-delivery-mcp", "http"]
