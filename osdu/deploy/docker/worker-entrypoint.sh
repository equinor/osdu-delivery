#!/bin/sh
# Composes the node's invocation from the container's environment. The host reads the control plane's URL from
# SQLFLOW_URL, the node credential from SQLFLOW_TOKEN and the catalog database's connection, which holds the `osdu`
# schema, from SQLFLOW_CATALOG_DB itself, so none of them appears on the command line or in `ps` output.
set -e

# Start from an empty option list, so a `docker run` argument can never be mixed into the composed invocation.
set --
if [ -n "${SQLFLOW_WORKER_POOL:-}" ]; then
  set -- "$@" --pool "$SQLFLOW_WORKER_POOL"
fi
if [ -n "${SQLFLOW_WORKER_POLL_SECONDS:-}" ]; then
  set -- "$@" --poll-seconds "$SQLFLOW_WORKER_POLL_SECONDS"
fi
# How long a stopping node keeps finishing the runs it already claimed. Keep it below the orchestrator's
# termination grace period (Container Apps / Kubernetes terminationGracePeriodSeconds), or the platform's kill
# lands mid-drain and severs the work the drain exists to save.
if [ -n "${SQLFLOW_WORKER_DRAIN_SECONDS:-}" ]; then
  set -- "$@" --drain-seconds "$SQLFLOW_WORKER_DRAIN_SECONDS"
fi

exec dotnet /app/SqlFlow.Delivery.Worker.Host.dll "$@"
