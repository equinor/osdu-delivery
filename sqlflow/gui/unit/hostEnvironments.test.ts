// The environments a host works against (src/embed/environments.ts): what a saved one is, how a typed one is checked,
// the stage a name says, and how the saved list is read from settings a person may have edited by hand.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  addressProblem,
  checkDraft,
  controlPlaneKey,
  environmentNameProblem,
  environmentOf,
  guessStage,
  MAX_ENVIRONMENTS,
  readSavedEnvironments,
  sameControlPlane,
  type SavedEnvironment,
} from "../src/embed/environments.ts";

const dev: SavedEnvironment = { name: "dev", url: "https://sqlflow-dev.example.com", stage: "development", guiUrl: null };
const prod: SavedEnvironment = {
  name: "prod",
  url: "https://sqlflow-prod.example.com/control",
  stage: "production",
  guiUrl: "https://sqlflow-prod.example.com",
};

describe("the stage a name or an address says", () => {
  it("is the one stage the name says, before the address", () => {
    assert.equal(guessStage("prod", "https://cp.example.com"), "production");
    assert.equal(guessStage("dev", "https://prod-cluster.example.com"), "development");
    assert.equal(guessStage("UAT", "https://cp.example.com"), "test");
    assert.equal(guessStage("staging-west", "https://cp.example.com"), "test");
    assert.equal(guessStage("prd", ""), "production");
  });

  it("reads an instance number after the word as the same word", () => {
    assert.equal(guessStage("dev2", ""), "development");
    assert.equal(guessStage("prod-01", ""), "production");
  });

  it("reads pre-production as test, however it is written", () => {
    assert.equal(guessStage("preprod", ""), "test");
    assert.equal(guessStage("pre-prod", ""), "test");
    assert.equal(guessStage("pre_prod", ""), "test");
  });

  it("is the address's when the name says nothing", () => {
    assert.equal(guessStage("west", "https://sqlflow-prod.example.com"), "production");
    assert.equal(guessStage("", "https://cp.test.example.com"), "test");
    assert.equal(guessStage("mine", "http://localhost:5000"), "development");
    assert.equal(guessStage("mine", "http://127.0.0.1:5000"), "development");
  });

  it("is none when nothing says one, or more than one is said, so production is never green by default", () => {
    assert.equal(guessStage("west", "https://cp.example.com"), null);
    assert.equal(guessStage("test-prod", "https://cp.example.com"), null);
    assert.equal(guessStage("", "https://dev-prod.example.com"), null);
    assert.equal(guessStage("", "not an address"), null);
    // A word that only holds a stage's letters is not that word.
    assert.equal(guessStage("product", "https://cp.example.com"), null);
    assert.equal(guessStage("devtest", "https://cp.example.com"), null);
  });
});

describe("a name and an address typed into the form", () => {
  it("takes the control plane's own rule for a name", () => {
    assert.equal(environmentNameProblem("prod"), null);
    assert.equal(environmentNameProblem("eu-west.prod_2"), null);
    assert.match(environmentNameProblem("") ?? "", /Name the environment/);
    assert.match(environmentNameProblem("-prod") ?? "", /starting with a letter or a digit/);
    assert.match(environmentNameProblem("my prod") ?? "", /letters, digits/);
    assert.match(environmentNameProblem("x".repeat(65)) ?? "", /at most 64/);
  });

  it("takes an absolute http or https address with nothing but the address in it", () => {
    assert.equal(addressProblem("https://cp.example.com"), null);
    assert.equal(addressProblem("http://localhost:5000/sqlflow/"), null);
    assert.match(addressProblem("") ?? "", /Enter the control plane's address/);
    assert.match(addressProblem("", "GUI") ?? "", /GUI's address/);
    assert.match(addressProblem("cp.example.com") ?? "", /not an address/);
    assert.match(addressProblem("ftp://cp.example.com") ?? "", /https:\/\/ or http:\/\//);
    assert.match(addressProblem("https://me:secret@cp.example.com") ?? "", /user name or a password/);
    assert.match(addressProblem("https://cp.example.com/?token=x") ?? "", /query and the fragment/);
    assert.match(addressProblem("https://cp.example.com/#x") ?? "", /query and the fragment/);
  });

  it("knows a control plane by the key its sign-in is kept under", () => {
    assert.equal(controlPlaneKey("HTTPS://CP.Example.com:443/Control/"), "https://cp.example.com/control");
    assert.equal(sameControlPlane("https://cp.example.com/", "https://CP.example.com"), true);
    assert.equal(sameControlPlane("https://cp.example.com", "http://cp.example.com"), false);
    assert.equal(environmentOf([dev, prod], "https://SQLFLOW-PROD.example.com/control/")?.name, "prod");
    assert.equal(environmentOf([dev, prod], "https://other.example.com"), null);
    assert.equal(environmentOf([dev, prod], null), null);
  });
});

describe("an environment typed into the form, checked beside the saved ones", () => {
  it("saves trimmed, without a trailing slash, and without an empty GUI address", () => {
    const checked = checkDraft({ name: " test ", url: " https://cp-test.example.com/ ", stage: "test", guiUrl: " " }, [dev]);
    assert.deepEqual(checked, {
      environment: { name: "test", url: "https://cp-test.example.com", stage: "test", guiUrl: null },
      problems: null,
    });
  });

  it("keeps a GUI address it is given", () => {
    const checked = checkDraft({ name: "prod", url: prod.url, stage: "production", guiUrl: "https://gui.example.com/" }, []);
    assert.equal(checked.environment?.guiUrl, "https://gui.example.com");
  });

  it("asks for a stage rather than taking one", () => {
    const checked = checkDraft({ name: "west", url: "https://cp.example.com", stage: null, guiUrl: "" }, []);
    assert.equal(checked.environment, null);
    assert.match(checked.problems?.stage ?? "", /Pick the stage/);
  });

  it("refuses a name or a control plane another environment already has", () => {
    const named = checkDraft({ name: "DEV", url: "https://elsewhere.example.com", stage: "development", guiUrl: "" }, [dev]);
    assert.match(named.problems?.name ?? "", /Another environment is called dev/);
    const same = checkDraft({ name: "dev2", url: "https://SQLFLOW-DEV.example.com/", stage: "development", guiUrl: "" }, [dev]);
    assert.match(same.problems?.url ?? "", /dev is already saved with this address/);
  });

  it("says every problem at once, each by its field", () => {
    const checked = checkDraft({ name: "", url: "nope", stage: null, guiUrl: "ftp://x" }, []);
    assert.deepEqual(Object.keys(checked.problems ?? {}).sort(), ["guiUrl", "name", "stage", "url"]);
  });
});

describe("the saved list, as a person's settings hold it", () => {
  it("is empty and says nothing when nothing is set", () => {
    assert.deepEqual(readSavedEnvironments(undefined, "sqlflow.environments"), { environments: [], problems: [] });
    assert.deepEqual(readSavedEnvironments(null, "sqlflow.environments"), { environments: [], problems: [] });
  });

  it("reads each environment in its order", () => {
    const read = readSavedEnvironments([
      { name: "dev", url: "https://sqlflow-dev.example.com/", stage: "development" },
      { name: "prod", url: prod.url, stage: "production", guiUrl: prod.guiUrl },
    ], "sqlflow.environments");
    assert.deepEqual(read, { environments: [dev, prod], problems: [] });
  });

  it("leaves out what it cannot read, naming the entry, and shows the rest", () => {
    const read = readSavedEnvironments([
      { name: "dev", url: dev.url, stage: "development" },
      "prod",
      { name: "qa", url: "https://cp-qa.example.com", stage: "staging" },
      { name: "dev", url: "https://other.example.com", stage: "development" },
      { name: "again", url: "https://SQLFLOW-DEV.example.com", stage: "development" },
      { name: "secret", url: "https://me:pw@cp.example.com", stage: "test" },
      { name: "test", url: "https://cp-test.example.com", stage: "test" },
    ], "sqlflow.environments");
    assert.deepEqual(read.environments.map((environment) => environment.name), ["dev", "test"]);
    assert.equal(read.problems.length, 5);
    assert.match(read.problems[0], /^sqlflow\.environments\[1\] is not an environment/);
    assert.match(read.problems[1], /^sqlflow\.environments\[2\] is left out: its stage/);
    assert.match(read.problems[2], /\[3\] is left out: Another environment is called dev/);
    assert.match(read.problems[3], /\[4\] is left out: dev is already saved with this address/);
    // An address is never repeated: a mistyped one could carry a credential.
    assert.doesNotMatch(read.problems.join("\n"), /pw@|me:pw/);
  });

  it("says a setting that is not a list is not one", () => {
    const read = readSavedEnvironments({ dev: dev.url }, "sqlflow.environments");
    assert.deepEqual(read.environments, []);
    assert.match(read.problems[0], /is not a list of environments/);
  });

  it("shows at most the first twenty", () => {
    const many = Array.from({ length: MAX_ENVIRONMENTS + 3 }, (_, index) => ({
      name: `env${index}`,
      url: `https://cp-${index}.example.com`,
      stage: "development",
    }));
    const read = readSavedEnvironments(many, "sqlflow.environments");
    assert.equal(read.environments.length, MAX_ENVIRONMENTS);
    assert.deepEqual(read.problems, [`sqlflow.environments lists ${MAX_ENVIRONMENTS + 3} environments; the first ${MAX_ENVIRONMENTS} are shown.`]);
  });
});
