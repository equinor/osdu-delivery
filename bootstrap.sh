#!/usr/bin/env bash
# OSDU Delivery on this laptop, from nothing: it fetches the code, builds it, starts everything, and runs both sample estates.
#
#   curl -fsSL https://raw.githubusercontent.com/TahirRiaz/osdu-delivery/main/bootstrap.sh | bash
#
# It needs git, Node.js 22 or later, and a SQL Server or Docker to run one (./dev.sh offers .NET). The environment uses the
# public sample estates and synthetic data, and a stand-in for OSDU on this machine: it reaches no real OSDU. In a folder that
# already holds an environment of another kind it stops and says so, and changes nothing.
#
#   OSDU_DELIVERY_FOLDER    where the code goes (default: ./osdu-delivery)
#   OSDU_DELIVERY_BRANCH    the branch to fetch (default: main)
#   OSDU_DELIVERY_RESET     1 starts over: an earlier demo's databases and records are removed first
#
# Written for the bash macOS ships (3.2).
set -euo pipefail

repository="https://github.com/TahirRiaz/osdu-delivery.git"
folder="${OSDU_DELIVERY_FOLDER:-osdu-delivery}"
branch="${OSDU_DELIVERY_BRANCH:-main}"

stop() {
    echo "[X] $1" >&2
    exit 1
}

need() {
    command -v "$1" >/dev/null 2>&1 || stop "$1 is not installed. $2"
}

echo
echo "OSDU Delivery, on this laptop"
case "$(uname -s)" in
    Darwin) install="brew install" ;;
    *) install="your package manager's install of" ;;
esac
need git "It fetches the code: $install git, then run this again."
need node "It runs the GUI and the stand-in for OSDU: $install node, then run this again."

if [ -f "$folder/dev.env.example" ]; then
    echo "Using the code already in $folder."
    git -C "$folder" pull --ff-only --quiet || echo "It could not be brought up to date, so it runs as it stands."
elif [ -e "$folder" ] && [ -n "$(ls -A "$folder" 2>/dev/null)" ]; then
    stop "$folder holds something else. Set OSDU_DELIVERY_FOLDER to a new folder, then run this again."
else
    echo "Fetching the code into $folder ..."
    git clone --depth 1 --branch "$branch" "$repository" "$folder" || stop "git could not fetch $repository; its output above says why."
fi

cd "$folder"
arguments="demo"
if [ "${OSDU_DELIVERY_RESET:-}" = "1" ]; then arguments="demo reset"; fi

# Piped from curl, this script's input is the script itself, so what dev.sh asks would read the rest of it: a terminal answers.
if [ ! -t 0 ] && (: </dev/tty) 2>/dev/null; then
    exec bash ./dev.sh $arguments </dev/tty
fi
exec bash ./dev.sh $arguments
