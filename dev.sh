#!/usr/bin/env bash
# =====================================================================================================================
#  OSDU Delivery local dev, on macOS and Linux (dev.bat on Windows). Run this instead of deploying to try something.
#
#      ./dev.sh          start the GUI, the control plane and its managed workers: worker processes the control plane
#                        starts and stops itself (the 'process' launcher), as a deployment's workers run, so the Nodes
#                        page's scaling acts here too
#      ./dev.sh api      control plane and workers only (no GUI, so no Node.js needed)
#      ./dev.sh single   one process: the control plane runs runs on its own node and starts no worker processes (what
#                        F5 runs). Combine with api: ./dev.sh single api
#      ./dev.sh managed  the default, accepted as before
#      ./dev.sh setup    ask the setup questions again (the SQL Server, OSDU, a repository), then start
#      ./dev.sh check    say what this machine has and what it is missing, test the OSDU sign-in, and start nothing
#      ./dev.sh demo     a local environment with nothing to ask: the sample estates, synthetic data and a stand-in for
#                        OSDU on this machine; it starts everything, then runs the tour, which says whether it all held.
#                        For a clean machine: it refuses an environment it did not make, and it reaches no real OSDU
#      ./dev.sh demo reset  the same from nothing: first removes the databases and records an earlier demo made, which
#                        only a demo's own file allows
#
#  First start: with no .sqlflow/env yet, the setup asks for what it cannot know, offers to create the databases and to
#  start SQL Server 2022 in Docker (which is how SQL Server runs on a Mac), writes .sqlflow/env from dev.env.example, and
#  registers the partition. Every start then checks the machine and the file and prints what is set and what is missing,
#  with the fix for each, and stops only when a start would fail. Both are osdu-delivery-setup
#  (osdu/docs/reference/cli/dev.md), built here from this checkout in seconds; on a machine without .NET, which every
#  start needs, this script offers to install it first. It only finds them, and starts what the check cleared.
#
#  What comes up: the GUI on http://localhost:5173 (Vite, hot-reloads on save), the control plane on
#  http://localhost:5000 (API + scheduler + dispatcher; with 'single', its own node too), and the worker processes it
#  starts, named worker-<pool>-<id>, at least one and at most 4 unless .sqlflow/env sets
#  ControlPlane__ManagedWorkers__MaxWorkers, all in this terminal: Ctrl+C stops them.
#  Everything runs here, against the databases and the OSDU partition .sqlflow/env names; flows read and WRITE them
#  exactly as a deployed estate does. Pending migrations from THIS checkout are applied before startup.
#
#  Written for the bash macOS ships (3.2): no ${var,,}, no associative arrays, no empty-array expansion under set -u.
# =====================================================================================================================
set -euo pipefail

cd "$(dirname "$0")"
root="$PWD"

# Managed workers are the default; 'single' is the one-process mode, and 'managed' names the default.
api=""
managed=1
single=""
named_managed=""
asksetup=""
check=""
demo=""
reset=""
for argument in "$@"; do
    case "$(printf '%s' "$argument" | tr '[:upper:]' '[:lower:]')" in
        api) api=1 ;;
        single) single=1 ;;
        managed) named_managed=1 ;;
        setup) asksetup=1 ;;
        check) check=1 ;;
        demo) demo=1 ;;
        reset) reset=1 ;;
        *)
            echo "[X] '$argument' is none of api, single, managed, setup, check, demo and reset." >&2
            exit 1
            ;;
    esac
done
if [ -n "$reset" ] && [ -z "$demo" ]; then
    echo "[X] 'reset' goes with 'demo': ./dev.sh demo reset." >&2
    exit 1
fi
if [ -n "$single" ] && [ -n "$named_managed" ]; then
    echo "[X] 'single' runs no worker processes and 'managed' starts them: name one of the two." >&2
    exit 1
fi
if [ -n "$single" ]; then managed=""; fi
api_flag=""
if [ -n "$api" ]; then api_flag="--api"; fi

# --- .NET SDK on PATH ---
# A terminal opened before the install, or an install outside the PATH (the Microsoft script's ~/.dotnet, an SDK the
# setup installed since this terminal started), is found here rather than demanding a new terminal. 'dotnet --version'
# honours global.json.
find_dotnet() {
    if dotnet --version >/dev/null 2>&1; then
        return 0
    fi
    local candidate
    for candidate in "$HOME/.dotnet" /usr/local/share/dotnet /opt/homebrew/share/dotnet /usr/share/dotnet /usr/lib/dotnet; do
        if [ -x "$candidate/dotnet" ] && "$candidate/dotnet" --version >/dev/null 2>&1; then
            export DOTNET_ROOT="$candidate"
            export PATH="$candidate:$PATH"
            return 0
        fi
    done
    return 1
}

# --- .NET, offered when it is missing: Homebrew on a Mac, Microsoft's install script on Linux ---
# .NET on Linux reads its culture data from ICU (libicu), and nothing of OSDU Delivery runs without it.
has_icu() {
    local ldconfig lib
    for ldconfig in ldconfig /sbin/ldconfig /usr/sbin/ldconfig; do
        if command -v "$ldconfig" >/dev/null 2>&1 && "$ldconfig" -p 2>/dev/null | grep -q 'libicuuc\.so'; then
            return 0
        fi
    done
    for lib in /usr/lib/*/libicuuc.so* /usr/lib64/libicuuc.so* /usr/lib/libicuuc.so* /lib/*/libicuuc.so*; do
        [ -e "$lib" ] && return 0
    done
    return 1
}

# Installs .NET when the person says yes, and finds it again; false, with what to do, when it is still missing.
offer_dotnet() {
    local how command answer=""
    echo "The .NET SDK is not installed. The setup, the control plane and the workers all build and run on it." >&2
    case "$(uname -s)" in
        Darwin)
            if command -v brew >/dev/null 2>&1; then
                how="Homebrew (brew install --cask dotnet-sdk; it may ask for your password)"
                command="brew install --cask dotnet-sdk"
            else
                how=""
                command="the macOS installer from https://dotnet.microsoft.com/download"
            fi
            ;;
        Linux)
            if ! has_icu; then
                echo "[X] .NET on Linux needs ICU (libicu), which this machine does not have:" >&2
                echo "    apt install libicu-dev (Debian, Ubuntu), dnf install libicu (Fedora, RHEL), then start ./dev.sh again." >&2
                return 1
            fi
            if command -v curl >/dev/null 2>&1; then
                how="Microsoft's dotnet-install.sh, into ~/.dotnet"
                command="curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 9.0 --install-dir ~/.dotnet"
            else
                how=""
                command="https://learn.microsoft.com/dotnet/core/install/linux"
            fi
            ;;
        *)
            how=""
            command="https://dotnet.microsoft.com/download"
            ;;
    esac
    if [ -n "$how" ]; then
        read -r -p "Install the .NET 9 SDK now with $how? [Y/n] " answer || answer=n
        case "$(printf '%s' "$answer" | tr '[:upper:]' '[:lower:]')" in
            n | no) ;;
            *)
                if bash -o pipefail -c "$command" && find_dotnet; then
                    return 0
                fi
                echo "[X] The install did not leave a .NET SDK this terminal can run; its output above says why." >&2
                ;;
        esac
    fi
    echo "[X] Install the .NET 9 SDK: $command" >&2
    echo "    then start ./dev.sh again." >&2
    return 1
}

# --- The setup: osdu-delivery-setup ---
# It carries only the setup, so it builds in seconds and is always this checkout's. It needs .NET, as every start does,
# so a machine without it is offered the install first.
if ! find_dotnet; then
    offer_dotnet || exit 1
fi
echo "Building the setup ..."
if ! dotnet build osdu/hosts/SqlFlow.Delivery.Setup.Host -v q -nologo; then
    echo "[X] The setup did not build. Fix the build above and start ./dev.sh again." >&2
    exit 1
fi
setup="$root/osdu/hosts/SqlFlow.Delivery.Setup.Host/bin/Debug/net9.0/osdu-delivery-setup"

# --- 'check': the report alone, with an OSDU sign-in; nothing is stopped or started ---
if [ -n "$check" ]; then
    set +e
    "$setup" check --test-osdu $api_flag
    exit $?
fi

# --- The pre-push tests: git runs the unit tier before every push (CONTRIBUTING.md, Tests) ---
# Once per clone: only in a git checkout, and only where no hooks path is set, so one somebody chose is left as it is.
# 'git push --no-verify' skips the hook once; 'git config --unset core.hooksPath' removes it.
if command -v git > /dev/null 2>&1 && git rev-parse --is-inside-work-tree > /dev/null 2>&1 \
   && [ -z "$(git config --get core.hooksPath || true)" ]; then
    if git config core.hooksPath tools/git-hooks; then
        echo "Installed the pre-push tests: git runs the unit tier (bash tools/test.sh) before every push; git push --no-verify skips it once."
    fi
fi

stop_port() {
    local pids
    pids="$(lsof -ti "tcp:$1" -sTCP:LISTEN 2>/dev/null || true)"
    if [ -n "$pids" ]; then
        echo "Stopping previous $2 (pid $(echo "$pids" | tr '\n' ' ')) on port $1 ..."
        echo "$pids" | xargs kill 2>/dev/null || true
    fi
}

# --- The setup when there is no env file (or it was asked for), else the check ---
# Exit codes: 0 ready, 2 something a start needs is missing (the report says what), 1 stopped.
if [ -n "$demo" ]; then
    reset_flag=""
    if [ -n "$reset" ]; then
        # What the last demo left running holds its databases open, and would write to them again.
        stop_port 5000 "control plane"
        stop_port 5301 "OSDU stand-in"
        reset_flag="--reset"
    fi
    "$setup" demo $reset_flag $api_flag || exit 1
elif [ -n "$asksetup" ] || [ ! -f .sqlflow/env ]; then
    if [ ! -f .sqlflow/env ]; then
        echo ".sqlflow/env is missing, so this is a first start: a few questions set this machine up."
    fi
    "$setup" setup $api_flag || exit 1
else
    "$setup" check --prepare $api_flag || exit 1
fi


# --- The command line: the migrations; the setup has usually built it already ---
echo "Building the OSDU Delivery command line ..."
if ! dotnet build osdu/hosts/SqlFlow.Delivery.Cli.Host -v q -nologo; then
    echo "[X] The command line did not build. Fix the build above and start ./dev.sh again." >&2
    exit 1
fi
cli="$root/osdu/hosts/SqlFlow.Delivery.Cli.Host/bin/Debug/net9.0/SqlFlow.Delivery.Cli.Host"

# --- Stop whatever the last run left behind ---
# A control plane from an earlier run holds its port and keeps taking runs; a managed worker of one keeps polling. This
# waits until the check passed, so a start that would fail never takes down one that works.
stop_port 5000 "control plane"
if [ -z "$api" ]; then stop_port 5173 "GUI"; fi
stop_port 5301 "OSDU stand-in"
pkill -f 'SqlFlow.Delivery.ControlPlane.Host' 2>/dev/null || true
pkill -f 'SqlFlow.Delivery.Worker.Host' 2>/dev/null || true

# --- Load .sqlflow/env into this process ---
# The control plane builds its configuration from the process environment before it reads .sqlflow/env itself, so its
# ControlPlane__* settings reach it only this way. 'env' prints the file's settings as the hosts read them; each is
# exported as it stands, never evaluated, so no value is ever run as shell.
settings="$("$setup" env)" || { echo "[X] .sqlflow/env could not be read; ./dev.sh check says why." >&2; exit 1; }
while IFS= read -r line; do
    # Under Git Bash the setup is a Windows program, whose lines end with a carriage return.
    line="${line%$'\r'}"
    [ -z "$line" ] && continue
    name="${line%%=*}"
    value="${line#*=}"
    if [ -z "$value" ]; then
        unset "$name"
    else
        export "$name=$value"
    fi
done <<< "$settings"

# --- Apply pending migrations, up front and visibly ---
# The local host runs with ApplyMigrations=false so an F5 or a stray local run never mutates the catalog implicitly;
# starting here is the explicit moment the checkout meets the databases. 'db migrate' upgrades an EXISTING database
# only and refuses to provision anything, so a wrong connection string can never create a stray database.
echo "Applying pending migrations (SQLFlow catalog, then the OSDU module) ..."
if ! "$cli" db migrate; then
    echo "[!] Migration failed. The control plane still starts, but any surface that needs the new schema will error" >&2
    echo "    until:  $cli db migrate" >&2
fi

# --- Managed workers (the default): the control plane starts its own worker processes ---
# The worker host is built here because the control plane starts it, it does not build it. The control plane then runs
# API-only and keeps at least one worker running per managed pool, starting more for queued runs and asking idle ones to
# leave, at most 4 unless .sqlflow/env sets ControlPlane__ManagedWorkers__MaxWorkers (any other
# ControlPlane__ManagedWorkers__* setting there applies too). 'single' skips all of this: the control plane runs runs on
# its own node, as .sqlflow/env configures it.
if [ -n "$managed" ]; then
    echo "Building the worker host for managed workers ..."
    if ! dotnet build osdu/hosts/SqlFlow.Delivery.Worker.Host -v q -nologo; then
        echo "[X] The worker host did not build; managed workers cannot start. Fix the build above and retry." >&2
        exit 1
    fi
    export ControlPlane__Worker__Enabled=false
    export ControlPlane__ManagedWorkers__Enabled=true
    export ControlPlane__ManagedWorkers__Launcher=process
    # Git Bash on Windows runs this script too, where the built host carries its extension.
    worker="$root/osdu/hosts/SqlFlow.Delivery.Worker.Host/bin/Debug/net9.0/SqlFlow.Delivery.Worker.Host"
    if [ -f "$worker.exe" ]; then worker="$worker.exe"; fi
    export ControlPlane__ManagedWorkers__Process__Command="$worker"
    export ControlPlane__ManagedWorkers__Process__ControlPlaneUrl=http://localhost:5000
    export ControlPlane__ManagedWorkers__MaxWorkers="${ControlPlane__ManagedWorkers__MaxWorkers:-4}"
fi

# --- The GUI and the stand-in for OSDU, in the background of this terminal: Ctrl+C (or the control plane ending) stops them ---
gui_pid=""
standin_pid=""
tour_pid=""
stop_gui() {
    if [ -n "$tour_pid" ]; then
        kill "$tour_pid" 2>/dev/null || true
    fi
    if [ -n "$gui_pid" ]; then
        kill "$gui_pid" 2>/dev/null || true
        pkill -P "$gui_pid" 2>/dev/null || true
    fi
    if [ -n "$standin_pid" ]; then
        kill "$standin_pid" 2>/dev/null || true
    fi
}
if [ -z "$api" ] || [ "${OSDU_STANDIN_WRITES:-}" = "true" ]; then
    # npm writes node_modules/.package-lock.json on every install; a package-lock.json newer than it means a pulled or
    # added dependency, and Vite would fail at the first missing import, so the tree is installed again.
    if [ ! -f osdu/gui/node_modules/.package-lock.json ] || [ osdu/gui/package-lock.json -nt osdu/gui/node_modules/.package-lock.json ]; then
        echo "Installing GUI dependencies ..."
        (cd osdu/gui && npm ci)
    fi
fi
if [ "${OSDU_STANDIN_WRITES:-}" = "true" ]; then
    echo "Starting the stand-in for OSDU on http://127.0.0.1:5301 ..."
    (cd osdu/gui && OSDU_STANDIN_STATE="$root/.sqlflow/osdu-standin.json" exec node standin/osdu-standin.mjs) &
    standin_pid=$!
    trap stop_gui EXIT INT TERM
fi
if [ -z "$api" ]; then
    echo "Starting the GUI on http://localhost:5173 ..."
    (cd osdu/gui && exec npm run dev -- --open) &
    gui_pid=$!
    trap stop_gui EXIT INT TERM
fi

echo
echo "  GUI:      http://localhost:5173"
echo "  API:      http://localhost:5000"
echo "  Catalog:  the one .sqlflow/env names (./dev.sh check says which, and what it holds)"
if [ -n "$managed" ]; then echo "  Workers:  managed: the control plane starts worker processes itself (see the Nodes page)."; fi
if [ -n "$single" ]; then echo "  Workers:  none: the control plane runs runs on its own node ('single')."; fi
echo "  Ctrl+C    stops the control plane and the GUI."
echo
# 'dotnet run' takes the project directory as its content root, where there is no appsettings.json, so every request
# and every EF Core command would log at Information without these two. Their names hold a '.', which a shell variable
# cannot, so env passes them. The tour waits for this control plane to come up, and writes to this terminal beside it.
if [ -n "$demo" ]; then
    "$setup" tour &
    tour_pid=$!
    trap stop_gui EXIT INT TERM
fi
env "Logging__LogLevel__Microsoft.AspNetCore=Warning" "Logging__LogLevel__Microsoft.EntityFrameworkCore=Warning" \
    dotnet run --project osdu/hosts/SqlFlow.Delivery.ControlPlane.Host --urls http://localhost:5000
