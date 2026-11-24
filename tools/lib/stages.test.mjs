// The stages of a test run (tools/lib/stages.sh), driven through bash as tools/test.sh drives them: every stage runs
// whatever the ones before it did, a stage that needs one that did not pass is not run, and the summary names each.

import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { dirname, join } from "node:path";
import { describe, it } from "node:test";
import { fileURLToPath } from "node:url";

const repository = join(dirname(fileURLToPath(import.meta.url)), "..", "..");

/** Runs `script` in bash from the repository's root with the stages library sourced, under set -euo pipefail as tools/test.sh runs. */
function run(script) {
  const result = spawnSync("bash", ["-c", `set -euo pipefail\n. tools/lib/stages.sh\n${script}`], {
    cwd: repository,
    encoding: "utf8",
  });
  assert.equal(result.error, undefined, `bash could not be started: ${result.error}`);
  return { status: result.status, out: result.stdout, err: result.stderr };
}

/** The summary's line for the stage named `name`, its spaces collapsed: "FAILED 0m 00s b (exit 1)". */
function line(out, name) {
  const summary = out.slice(out.indexOf("== Summary"));
  const found = summary.split("\n").find((l) => l.includes(` ${name}`) && !l.startsWith("=="));
  assert.ok(found, `the summary has no line for ${name}:\n${summary}`);
  return found.trim().replace(/\s+/g, " ").replace(/\d+m \d{2}s/, "<time>");
}

describe("a run's stages", () => {
  it("passes when every stage passes, and says so with how long it took", () => {
    const { status, out } = run(`
      stages_begin
      stage_run "first" -- true
      stage_run "second" --needs "first" -- true
      stages_end "The probe run"`);

    assert.equal(status, 0);
    assert.equal(line(out, "first"), "passed <time> first");
    assert.equal(line(out, "second"), "passed <time> second");
    assert.match(out, /The probe run passed in \d+m \d{2}s\.\n$/);
  });

  it("runs every stage after a failed one, and fails naming each stage that failed", () => {
    const { status, out } = run(`
      stages_begin
      stage_run "a" -- true
      stage_run "b" -- false
      stage_run "c" -- true
      stage_run "d" -- sh -c "exit 3"
      stages_end "The probe run"`);

    assert.equal(status, 1);
    assert.match(out, /== c\n/);
    assert.equal(line(out, "a"), "passed <time> a");
    assert.equal(line(out, "b"), "FAILED <time> b (exit 1)");
    assert.equal(line(out, "c"), "passed <time> c");
    assert.equal(line(out, "d"), "FAILED <time> d (exit 3)");
    assert.match(out, /The probe run failed in \d+m \d{2}s: 2 of 4 stages failed \(b; d\)\.\n$/);
  });

  it("does not run a stage that needs one that did not pass, and says which and why", () => {
    const { status, out } = run(`
      stages_begin
      stage_run "build" -- false
      stage_run "tests" --needs "build" -- echo tests-ran
      stage_run "lint" -- true
      stage_run "e2e" --needs "lint" --needs "tests" -- echo e2e-ran
      stage_run "docs" --needs "never recorded" -- echo docs-ran
      stages_end "The probe run"`);

    assert.equal(status, 1);
    assert.doesNotMatch(out, /tests-ran|e2e-ran|docs-ran/);
    assert.equal(line(out, "tests"), "not run tests (needs build, which failed)");
    assert.equal(line(out, "e2e"), "not run e2e (needs tests, which did not run)");
    assert.equal(line(out, "docs"), "not run docs (needs never recorded, which did not run)");
    assert.match(out, /1 of 5 stages failed \(build\)/);
  });

  it("ends a stage at its first failing command, a failing pipe or an unset variable", () => {
    const { status, out } = run(`
      two_steps() { false; echo after-the-failure; }
      piped() { false | cat; echo after-the-pipe; }
      unset_read() { echo "$never_set"; echo after-the-unset; }
      stages_begin
      stage_run "two steps" -- two_steps
      stage_run "piped" -- piped
      stage_run "unset" -- unset_read
      stages_end "The probe run"`);

    assert.equal(status, 1);
    assert.doesNotMatch(out, /after-the-failure|after-the-pipe|after-the-unset/);
    assert.match(out, /3 of 3 stages failed \(two steps; piped; unset\)/);
  });

  it("runs a stage in a subshell, so a directory it enters or a variable it sets stays its own", () => {
    const { status, out } = run(`
      moves() { cd tools; mine=1; }
      stages_begin
      stage_run "moves" -- moves
      echo "after: $(basename "$PWD") \${mine:-unset}"
      stages_end "The probe run"`);

    assert.equal(status, 0);
    assert.match(out, /after: \S+ unset\n/);
    assert.doesNotMatch(out, /after: tools /);
  });

  it("records a skipped stage with its reason, which fails nothing", () => {
    const { status, out } = run(`
      stages_begin
      stage_skip "SQLFlow GUI" "unchanged on this branch"
      stage_run "after it" --needs "SQLFlow GUI" -- true
      stages_end "The probe run"`);

    assert.equal(status, 0);
    assert.equal(line(out, "SQLFlow GUI"), "skipped SQLFlow GUI (unchanged on this branch)");
    assert.equal(line(out, "after it"), "not run after it (needs SQLFlow GUI, which was skipped)");
  });

  it("stops at an interrupt: the stage it reached is marked, and the stages after it are not run", () => {
    const { status, out } = run(`
      stages_begin
      stage_run "before" -- true
      stage_run "reached" -- kill -INT $$
      stage_run "after" -- echo after-ran
      stages_end "The probe run"`);

    assert.equal(status, 130);
    assert.doesNotMatch(out, /after-ran/);
    assert.equal(line(out, "before"), "passed <time> before");
    assert.match(line(out, "reached"), /^INTERRUPTED <time> reached \(exit \d+\)$/);
    assert.equal(line(out, "after"), "not run after (the run was interrupted)");
    assert.match(out, /The probe run was interrupted after \d+m \d{2}s/);
  });

  it("refuses a stage with no name or no command, naming the mistake", () => {
    const unnamed = run(`stages_begin\nstage_run "" -- true`);
    assert.equal(unnamed.status, 2);
    assert.match(unnamed.err, /a stage needs a name/);

    const commandless = run(`stages_begin\nstage_run "lint" npm run lint`);
    assert.equal(commandless.status, 2);
    assert.match(commandless.err, /stage 'lint' names its command after --/);

    const empty = run(`stages_begin\nstage_run "lint" --`);
    assert.equal(empty.status, 2);
  });
});
