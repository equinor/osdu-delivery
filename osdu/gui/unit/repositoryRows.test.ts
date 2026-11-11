// The order and repository picks of the grids that list a repository's files: the mappings and the templates.
import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  ALL_REPOSITORIES, countOf, inRepositoryOrder, latestOf, pickedRepository, repositoriesOf,
} from "../src/features/delivery/repositoryRows.ts";

interface Row {
  repoId: string;
  path: string;
}

const names = new Map([["r-wl", "osdu-delivery-wl"], ["r-estate", "osdu-delivery-estate"], ["r-recall", "Osdu-recall-metadata"]]);
const rows: Row[] = [
  { repoId: "r-wl", path: "WellLog/mappings/WellLog@1.4.0.yaml" },
  { repoId: "r-recall", path: "Welllog/mappings/RecallWellLog@1.0.0.yaml" },
  { repoId: "r-estate", path: "welldb/mappings/LogType@1.0.0.yaml" },
  { repoId: "r-wl", path: "reference-data/LogType/mappings/LogType@1.0.0.yaml" },
  { repoId: "r-estate", path: "welldb/mappings/Well@1.2.0.yaml" },
];
const repoId = (row: Row) => row.repoId;

describe("the rows of a repository grid", () => {
  it("are listed by their repository's name, ignoring case, each repository's rows in the order they came", () => {
    assert.deepEqual(inRepositoryOrder(rows, repoId, names).map((row) => row.path), [
      "welldb/mappings/LogType@1.0.0.yaml",
      "welldb/mappings/Well@1.2.0.yaml",
      "WellLog/mappings/WellLog@1.4.0.yaml",
      "reference-data/LogType/mappings/LogType@1.0.0.yaml",
      "Welllog/mappings/RecallWellLog@1.0.0.yaml",
    ]);
  });

  it("place a repository with no name by its id", () => {
    const unnamed = [{ repoId: "0-unknown", path: "a.yaml" }, ...rows];
    assert.equal(inRepositoryOrder(unnamed, repoId, names)[0].repoId, "0-unknown");
    assert.deepEqual(repositoriesOf(unnamed, repoId, names)[0], { id: "0-unknown", name: "0-unknown" });
  });

  it("name each repository they come from once, by name", () => {
    assert.deepEqual(repositoriesOf(rows, repoId, names).map((repository) => repository.name), [
      "osdu-delivery-estate", "osdu-delivery-wl", "Osdu-recall-metadata",
    ]);
  });
});

describe("the repository a grid lists", () => {
  const repositories = repositoriesOf(rows, repoId, names);

  it("is the one picked while it is among the repositories", () => {
    assert.equal(pickedRepository("r-wl", repositories), "r-wl");
  });

  it("is every one for a pick of a repository that is gone", () => {
    assert.equal(pickedRepository("r-removed", repositories), ALL_REPOSITORIES);
  });

  it("is every one when there is one repository, which has no picker to take a pick back", () => {
    assert.equal(pickedRepository("r-wl", [{ id: "r-wl", name: "osdu-delivery-wl" }]), ALL_REPOSITORIES);
  });
});

describe("the counts and times a repository grid says", () => {
  it("count a noun, plural for any count but one", () => {
    assert.equal(countOf(1, "mapping"), "1 mapping");
    assert.equal(countOf(0, "mapping"), "0 mappings");
    assert.equal(countOf(1200, "repository", "repositories"), "1,200 repositories");
  });

  it("take the latest instant of the rows, or none for no rows", () => {
    assert.equal(latestOf(["2026-11-10T08:00:00Z", "2026-11-11T07:59:00Z", "2026-11-09T23:00:00Z"]), "2026-11-11T07:59:00Z");
    assert.equal(latestOf([]), null);
  });
});
