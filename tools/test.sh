#!/usr/bin/env bash
# Runs the tests, in one of two tiers (CONTRIBUTING.md, Tests):
#
#   bash tools/test.sh                  the unit tier: the build with warnings as errors, every .NET test that needs no
#                                       SQL Server, host or other process, and the GUI's build, lint and unit tests. It
#                                       is what CI's Build and test runs on a pull request, and what the pre-push hook
#                                       runs. It takes minutes, and needs no database.
#   bash tools/test.sh --full           everything: the whole .NET suite against SQL Server (and a test GitHub App when
#                                       one is configured), the MCP server's crates and the GUI end-to-end suite. Run it
#                                       before opening a pull request; CI runs it on a release tag, and on a pull request
#                                       labelled full-tests.
#   bash tools/test.sh --install-hook   makes git run the unit tier before every push (git config core.hooksPath).
#   bash tools/test.sh --suite NAME     one on-demand suite alone, against SQL Server as the full tier runs it: the tests
#                                       that carry [Trait("Suite", "NAME")], which both tiers leave out. The suites are
#                                       named in on_demand_suites below: DimensionEdgeCases, every edge of the dimension
#                                       engine's attributes, elements, lookups, incremental loads and views; and
#                                       RunParameters, every rule of a flow's parameters and run settings for each kind, a
#                                       run's resolution checked against what a run form and a schedule's preview say.
#
# The unit tier points the suites at a server that does not exist, so a test that needs SQL Server without carrying
# [Trait("Category", "Integration")] fails here, as it would in CI, rather than passing on this machine's database.
# The full tier uses SQLFLOW_TEST_DB: from the environment, else from .sqlflow/env, else (on Windows) the local default
# instance's OsduDeliveryTests under Windows authentication. Never point it at a database holding real data: the suites
# migrate and seed it. It reads an optional test GitHub App the same way (SQLFLOW_TEST_GITHUB_APP_ID, its key in
# SQLFLOW_TEST_GITHUB_APP_PRIVATE_KEY_PATH or SQLFLOW_TEST_GITHUB_APP_PRIVATE_KEY, and SQLFLOW_TEST_GITHUB_REPOSITORY;
# see sqlflow/docs/reference/guides/github-app.md): a read-only App the live suite writes nothing with. Without one,
# as on an estate that reads its repositories with a token, that suite is skipped.
#
# Written for the bash macOS ships (3.2), and for Git Bash on Windows.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

# The suites a test opts into with [Trait("Suite", "<name>")]: run on demand alone, left out of both tiers.
on_demand_suites="DimensionEdgeCases RunParameters"

if [ "$#" -gt 1 ] && [ "${1:-}" != --suite ]; then
  echo "tools/test.sh: takes one argument at most, or --suite and a suite's name, and was given $#." >&2
  exit 2
fi

mode=unit
suite=""
case "${1:-}" in
  "") ;;
  --full) mode=full ;;
  --install-hook) mode=install-hook ;;
  --suite)
    mode=suite
    suite="${2:-}"
    if [ "$#" -ne 2 ] || ! printf '%s\n' $on_demand_suites | grep -qx -- "$suite"; then
      echo "tools/test.sh: --suite takes the name of one on-demand suite: $(printf '%s ' $on_demand_suites)(for example --suite DimensionEdgeCases)." >&2
      exit 2
    fi
    ;;
  -h | --help)
    sed -n '2,27p' "$0" | sed 's/^# \{0,1\}//'
    exit 0
    ;;
  *)
    echo "tools/test.sh: '$1' is none of --full, --suite and --install-hook (no argument runs the unit tier)." >&2
    exit 2
    ;;
esac

# Both tiers leave out every on-demand suite.
leave_out=""
for name in $on_demand_suites; do
  leave_out="$leave_out&Suite!=$name"
done
leave_out="${leave_out#&}"

if [ "$mode" = install-hook ]; then
  current="$(git config --get core.hooksPath || true)"
  if [ -n "$current" ] && [ "$current" != tools/git-hooks ]; then
    echo "tools/test.sh: core.hooksPath is '$current' already; it is left as it is. Run the unit tier from that hook:" >&2
    echo "  bash tools/test.sh" >&2
    exit 1
  fi
  git config core.hooksPath tools/git-hooks
  echo "Installed: git runs the unit tier (bash tools/test.sh) before every push that carries commits."
  echo "git push --no-verify skips it once; git config --unset core.hooksPath removes it."
  exit 0
fi

started=$(date +%s)
step() {
  echo
  echo "== $1"
}

on_windows=false
case "$(uname -s)" in
  MINGW* | MSYS* | CYGWIN*) on_windows=true ;;
esac

# --- The tools each tier needs, named before anything runs ---
missing=""
command -v dotnet > /dev/null 2>&1 || missing="$missing .NET SDK (dotnet),"
if [ "$mode" != suite ]; then
  command -v npm > /dev/null 2>&1 || missing="$missing Node.js (npm),"
fi
if [ "$mode" = full ]; then
  command -v cargo > /dev/null 2>&1 || missing="$missing Rust (cargo, https://rustup.rs),"
fi
if [ -n "$missing" ]; then
  echo "tools/test.sh: the $mode tier needs${missing%,}, which this terminal cannot find." >&2
  exit 1
fi

# --- The test database ---
if [ "$mode" = unit ]; then
  # A server no test can reach: the unit tier proves it needs none. Port 1 refuses at once.
  test_db="Server=127.0.0.1,1;Database=UnitTierHasNoDatabase;Integrated Security=True;Connect Timeout=2;TrustServerCertificate=True"
else
  test_db="${SQLFLOW_TEST_DB:-}"
  if [ -z "$test_db" ] && [ -f .sqlflow/env ]; then
    # .sqlflow/env is NAME=value lines, not shell: read the one value, without its surrounding quotes.
    test_db="$(grep -m 1 '^SQLFLOW_TEST_DB=' .sqlflow/env | cut -d= -f2- | sed -e "s/^[\"']//" -e "s/[\"']\$//" || true)"
  fi
  if [ -z "$test_db" ]; then
    if [ "$on_windows" = true ]; then
      test_db="Server=localhost;Database=OsduDeliveryTests;Integrated Security=True;TrustServerCertificate=True"
    else
      echo "tools/test.sh: the $mode tier runs the SQL Server suites, and SQLFLOW_TEST_DB is not set (in the environment or" >&2
      echo "in .sqlflow/env). Point it at a disposable database, for example the SQL Server ./dev.sh starts in Docker:" >&2
      echo "  SQLFLOW_TEST_DB='Server=localhost,1433;Database=OsduDeliveryTests;User ID=sa;Password=...;TrustServerCertificate=True'" >&2
      exit 1
    fi
  fi
fi
export SQLFLOW_TEST_DB="$test_db"

# The GitHub App live suite's test App: from the environment, else from .sqlflow/env (one line per value, so the key is
# its .pem file's path, or the base64 of the PEM). With none of them the suite is skipped; with only some of them it
# fails, naming the rest.
if [ "$mode" = full ] && [ -f .sqlflow/env ]; then
  for name in SQLFLOW_TEST_GITHUB_APP_ID SQLFLOW_TEST_GITHUB_APP_PRIVATE_KEY SQLFLOW_TEST_GITHUB_APP_PRIVATE_KEY_PATH \
    SQLFLOW_TEST_GITHUB_REPOSITORY; do
    if [ -z "${!name:-}" ]; then
      value="$(grep -m 1 "^$name=" .sqlflow/env | cut -d= -f2- | sed -e "s/^[\"']//" -e "s/[\"']\$//" || true)"
      if [ -n "$value" ]; then
        export "$name=$value"
      fi
    fi
  done
fi

# The hosts the suites start never apply this checkout's .sqlflow/env: its credentials and its OSDU partition are not
# the suites' to use.
export SQLFLOW_LOCAL_ENV_FILE=false

# --- .NET ---
step "Build (Release, warnings as errors)"
dotnet build OsduDelivery.sln -c Release -nologo -v q -warnaserror -warnnotaserror:NU1903

if [ "$mode" = unit ]; then
  step "Unit tests (everything but Category=Integration and the on-demand suites)"
  dotnet test OsduDelivery.sln -c Release --no-build -nologo --filter "Category!=Integration&$leave_out"
elif [ "$mode" = suite ]; then
  # One project at a time: the SQL Server suites share the test database.
  step "The $suite suite, against $(printf '%s' "$test_db" | sed -e 's/;[Pp]assword=[^;]*/;Password=***/')"
  dotnet test OsduDelivery.sln -c Release --no-build -nologo -m:1 --filter "Suite=$suite"
  elapsed=$(( $(date +%s) - started ))
  echo
  echo "The $suite suite passed in $(( elapsed / 60 ))m $(( elapsed % 60 ))s."
  exit 0
else
  # One project at a time: the SQL Server suites share the test database.
  step "Every .NET test but the on-demand suites, against $(printf '%s' "$test_db" | sed -e 's/;[Pp]assword=[^;]*/;Password=***/')"
  dotnet test OsduDelivery.sln -c Release --no-build -nologo -m:1 --filter "$leave_out"
fi

# --- The GUI ---
# osdu/gui compiles the vendored sqlflow/gui sources in place, so its build covers both trees' TypeScript.
gui_install() {
  # Installs when there is no install, or the lock file moved on since the last one.
  if [ ! -f "$1/node_modules/.package-lock.json" ] || [ "$1/package-lock.json" -nt "$1/node_modules/.package-lock.json" ]; then
    (cd "$1" && npm ci --no-audit --no-fund)
  fi
}
step "OSDU GUI: build, lint and unit tests"
gui_install osdu/gui
(cd osdu/gui && npm run build && npm run lint && npm run test:unit)

# SQLFlow's GUI on its own, as CI builds it, when this branch changes it.
base="$(git merge-base HEAD origin/main 2> /dev/null || true)"
if [ "$mode" = full ] || [ -z "$base" ] || [ -n "$(git diff --name-only "$base" -- sqlflow/gui; git status --porcelain -- sqlflow/gui)" ]; then
  step "SQLFlow GUI: build and unit tests"
  gui_install sqlflow/gui
  (cd sqlflow/gui && npm run build && npm run test:unit)
else
  step "SQLFlow GUI: unchanged on this branch, not built"
fi

if [ "$mode" = full ]; then
  # --- The MCP server ---
  export CARGO_TARGET_DIR="$root/.sqlflow/test/cargo-target"
  step "MCP server: clippy and tests"
  cargo clippy --locked --manifest-path sqlflow/tools/Cargo.toml -p sqlflow-mcp --all-targets -- -D warnings
  cargo test --locked --manifest-path sqlflow/tools/Cargo.toml -p sqlflow-mcp
  cargo clippy --locked --manifest-path osdu/hosts/osdu-delivery-mcp/Cargo.toml --all-targets -- -D warnings
  cargo test --locked --manifest-path osdu/hosts/osdu-delivery-mcp/Cargo.toml

  # --- The GUI end-to-end suite ---
  # It starts the control plane on 5299 and the GUI on 5173 from the Release build above. A dev GUI on 5173 would answer
  # for it, so the suite takes 5174 then.
  step "OSDU GUI end-to-end suite"
  if [ -z "${SQLFLOW_E2E_GUI_PORT:-}" ] && { (echo > /dev/tcp/127.0.0.1/5173) > /dev/null 2>&1; }; then
    export SQLFLOW_E2E_GUI_PORT=5174
    echo "Port 5173 is taken (a dev GUI?), so the suite's GUI runs on 5174."
  fi
  if [ "$on_windows" = false ] && [ -z "${SQLFLOW_E2E_CATALOG_DB:-}" ]; then
    echo "tools/test.sh: the end-to-end suite keeps its estate in a database of its own, and SQLFLOW_E2E_CATALOG_DB is not set." >&2
    echo "Point it at a disposable database on the same server, for example OsduDeliveryE2E." >&2
    exit 1
  fi
  export SQLFLOW_E2E_DOTNET_CONFIGURATION=Release
  (cd osdu/gui && npx playwright install chromium && npm run e2e)
fi

elapsed=$(( $(date +%s) - started ))
echo
echo "The $mode tier passed in $(( elapsed / 60 ))m $(( elapsed % 60 ))s."
