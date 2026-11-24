# The stages of a test run (tools/test.sh; CONTRIBUTING.md, Tests), sourced, never run on its own.
#
# Each stage runs in a subshell of its own under set -euo pipefail: its first failing command ends it, and the run goes
# on to the next stage whatever this one did, so no failure hides another. A stage that needs one that did not pass
# (the build a suite tests, the install a GUI's checks read) is not run, and the summary says which one it waited for.
# An interrupt (Ctrl+C) ends the stage it reaches and the run with it: the stages after it are reported as not run. The
# run ends with one line per stage, and fails when a stage failed.
#
#   stages_begin
#   stage_run "Build" -- dotnet build App.sln
#   stage_run "Unit tests" --needs "Build" -- dotnet test App.sln --no-build
#   stage_skip "Docs" "unchanged on this branch"
#   status=0; stages_end "The unit tier" || status=$?; exit "$status"
#
# Written for bash 3.2, as tools/test.sh is: no associative arrays, and no array is expanded, so set -u never trips on an
# empty one. The report keeps a line per stage, its fields apart by the ASCII unit separator, which no name holds.

stages_separator=$'\037'
stages_report=""
stages_interrupted=false
stages_started=$SECONDS

# Starts a run: forgets any stage recorded before, starts its clock, and notes an interrupt rather than dying of it, so
# the summary still says how far the run got.
stages_begin() {
  stages_report=""
  stages_interrupted=false
  stages_started=$SECONDS
  trap 'stages_interrupted=true' INT TERM
}

# Records a stage: its result (passed, failed, interrupted, skipped or not run), the seconds it took (empty when it did
# not run), its name and a note.
stage_record() {
  stages_report="$stages_report$1$stages_separator$2$stages_separator$3$stages_separator$4
"
}

# Prints the result recorded for the stage named $1, or nothing when no stage of that name was recorded.
stage_result() {
  local result seconds name note found=""
  while IFS="$stages_separator" read -r result seconds name note; do
    if [ "$name" = "$1" ]; then
      found="$result"
    fi
  done <<EOF
$stages_report
EOF
  printf '%s' "$found"
}

# stage_run <name> [--needs <stage>]... -- <command> [<argument>]...
# Runs <command> as the stage <name> in a subshell under set -euo pipefail, after every stage it needs has passed, and
# records how it went. It returns 0 whatever the stage did, so a caller under set -e goes on to the next stage; it
# returns 2, naming the mistake, only when it is called wrongly.
stage_run() {
  if [ "$#" -lt 1 ] || [ -z "$1" ]; then
    echo "stage_run: a stage needs a name, as in: stage_run \"Unit tests\" -- dotnet test" >&2
    return 2
  fi
  local name="$1"
  shift
  local needs="" need result began status errexit=false
  while [ "$#" -ge 2 ] && [ "$1" = --needs ]; do
    needs="$needs$2$stages_separator"
    shift 2
  done
  if [ "$#" -lt 2 ] || [ "$1" != -- ]; then
    echo "stage_run: stage '$name' names its command after --, as in: stage_run \"$name\" -- dotnet test" >&2
    return 2
  fi
  shift

  if [ "$stages_interrupted" = true ]; then
    stage_record "not run" "" "$name" "the run was interrupted"
    return 0
  fi

  while [ -n "$needs" ]; do
    need="${needs%%"$stages_separator"*}"
    needs="${needs#*"$stages_separator"}"
    result="$(stage_result "$need")"
    if [ "$result" != passed ]; then
      case "$result" in
        failed) result="failed" ;;
        interrupted) result="was interrupted" ;;
        skipped) result="was skipped" ;;
        *) result="did not run" ;;
      esac
      echo
      echo "== $name: not run, since it needs $need, which $result"
      stage_record "not run" "" "$name" "needs $need, which $result"
      return 0
    fi
  done

  echo
  echo "== $name"
  began=$SECONDS
  case $- in
    *e*) errexit=true ;;
  esac
  set +e
  (
    set -euo pipefail
    "$@"
  )
  status=$?
  if [ "$errexit" = true ]; then
    set -e
  fi

  if [ "$stages_interrupted" = true ]; then
    stage_record interrupted "$((SECONDS - began))" "$name" "exit $status"
  elif [ "$status" -eq 0 ]; then
    stage_record passed "$((SECONDS - began))" "$name" ""
  else
    stage_record failed "$((SECONDS - began))" "$name" "exit $status"
    echo "== $name failed (exit $status); the run goes on to the next stage."
  fi
  return 0
}

# stage_skip <name> <why>: records a stage this run has no reason to run, such as a tree the branch leaves unchanged.
stage_skip() {
  echo
  echo "== $1: skipped, $2"
  stage_record skipped "" "$1" "$2"
}

# Prints <seconds> as minutes and seconds: 75 as "1m 15s".
stages_duration() {
  printf '%dm %02ds' "$(($1 / 60))" "$(($1 % 60))"
}

# stages_end <run>: prints every stage the run recorded, one line each, and what became of the run (<run> names it, as
# in "The unit tier"). Returns 0 when no stage failed, 130 when the run was interrupted, and 1 when a stage failed.
stages_end() {
  local run="$1" result seconds name note shown took failed=0 total=0 failures="" elapsed
  elapsed=$((SECONDS - stages_started))
  echo
  echo "== Summary"
  while IFS="$stages_separator" read -r result seconds name note; do
    if [ -z "$name" ]; then
      continue
    fi
    total=$((total + 1))
    shown="$result"
    case "$result" in
      failed | interrupted)
        failed=$((failed + 1))
        failures="$failures; $name"
        shown="$(printf '%s' "$result" | tr '[:lower:]' '[:upper:]')"
        ;;
    esac
    took=""
    if [ -n "$seconds" ]; then
      took="$(stages_duration "$seconds")"
    fi
    if [ -n "$note" ]; then
      printf '  %-12s %9s  %s (%s)\n' "$shown" "$took" "$name" "$note"
    else
      printf '  %-12s %9s  %s\n' "$shown" "$took" "$name"
    fi
  done <<EOF
$stages_report
EOF
  echo
  if [ "$stages_interrupted" = true ]; then
    echo "$run was interrupted after $(stages_duration "$elapsed"); the stages after the one it reached did not run."
    return 130
  fi
  if [ "$failed" -eq 0 ]; then
    echo "$run passed in $(stages_duration "$elapsed")."
    return 0
  fi
  echo "$run failed in $(stages_duration "$elapsed"): $failed of $total stages failed (${failures#; })."
  return 1
}
