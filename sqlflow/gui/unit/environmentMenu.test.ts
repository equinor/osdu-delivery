// The title bar's environment control: it names the environment this deployment is, offers the others as links in their
// configured order, shows nothing when none is configured, and never links an address that is not http(s).

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { EstateEnvironments } from "../src/api/types.ts";
import { environmentMenu, readableAddress } from "../src/layout/workbench/environmentMenu.ts";

const estate = {
  current: "test",
  environments: [
    { name: "dev", description: "Development", url: "https://sqlflow-dev.example.com", current: false },
    { name: "test", description: null, url: "https://sqlflow-test.example.com/", current: true },
    { name: "prod", description: "Production", url: "https://sqlflow.example.com/workbench/", current: false },
  ],
};

describe("environmentMenu", () => {
  it("shows nothing before the list loads, and nothing when no environment is configured", () => {
    assert.equal(environmentMenu(undefined), null);
    assert.equal(environmentMenu({ current: null, environments: [] }), null);
    assert.equal(environmentMenu({ current: "  ", environments: [] }), null);
  });

  it("names the current environment and offers the others in their configured order", () => {
    const menu = environmentMenu(estate);

    assert.ok(menu);
    assert.equal(menu.current.name, "test");
    assert.equal(menu.canSwitch, true);
    assert.deepEqual(menu.entries.map((entry) => [entry.name, entry.current]), [["dev", false], ["test", true], ["prod", false]]);
    assert.deepEqual(menu.entries.map((entry) => entry.url), [
      "https://sqlflow-dev.example.com",
      "https://sqlflow-test.example.com/",
      "https://sqlflow.example.com/workbench/",
    ]);
    assert.deepEqual(menu.entries.map((entry) => entry.address), [
      "sqlflow-dev.example.com",
      "sqlflow-test.example.com",
      "sqlflow.example.com/workbench",
    ]);
  });

  it("names the current environment alone, with nothing to open, when only it is configured", () => {
    const menu = environmentMenu({ current: "prod", environments: [] });

    assert.ok(menu);
    assert.equal(menu.current.name, "prod");
    assert.equal(menu.current.url, null);
    assert.equal(menu.canSwitch, false);
    assert.deepEqual(menu.entries, []);
  });

  it("never links an address that is not http(s)", () => {
    const menu = environmentMenu({
      current: "test",
      environments: [
        { name: "dev", description: null, url: "javascript:alert(1)", current: false },
        { name: "test", description: null, url: "not an address", current: true },
        { name: "prod", description: null, url: "https://sqlflow.example.com", current: false },
      ],
    });

    assert.ok(menu);
    assert.deepEqual(menu.entries.map((entry) => entry.name), ["test", "prod"]);
    assert.equal(menu.current.url, null);
    assert.equal(menu.canSwitch, true);
  });

  it("offers no menu when every other environment's address is unusable", () => {
    const menu = environmentMenu({
      current: "test",
      environments: [
        { name: "test", description: null, url: "https://sqlflow-test.example.com", current: true },
        { name: "prod", description: null, url: "ftp://sqlflow.example.com", current: false },
      ],
    });

    assert.ok(menu);
    assert.equal(menu.canSwitch, false);
  });

  it("shows nothing for an answer it cannot read, so the title bar never breaks on one", () => {
    const unreadable: unknown[] = [null, "", [], "<html>", 42, {}, { current: 7 }, { current: " " }, { environments: [] }];
    for (const answer of unreadable) {
      assert.equal(environmentMenu(answer as EstateEnvironments), null, JSON.stringify(answer));
    }
  });

  it("names the current environment alone when the list is missing or not a list", () => {
    for (const environments of [undefined, null, "dev", {}]) {
      const menu = environmentMenu({ current: "test", environments } as unknown as EstateEnvironments);

      assert.ok(menu, JSON.stringify(environments));
      assert.equal(menu.current.name, "test");
      assert.equal(menu.canSwitch, false);
    }
  });

  it("leaves out an entry it cannot read and keeps the rest", () => {
    const menu = environmentMenu({
      current: "test",
      environments: [
        null,
        { name: 3, url: "https://sqlflow-dev.example.com", current: false },
        { name: "dev", url: 5, current: false },
        { name: " ", url: "https://sqlflow-dev.example.com", current: false },
        { name: "test", url: "https://sqlflow-test.example.com", current: "yes", description: 12 },
        { name: "prod", url: "https://sqlflow.example.com", current: false, description: "Production" },
      ],
    } as unknown as EstateEnvironments);

    assert.ok(menu);
    assert.deepEqual(menu.entries.map((entry) => [entry.name, entry.current, entry.description]), [
      ["test", false, null],
      ["prod", false, "Production"],
    ]);
    assert.equal(menu.current.name, "test");
    assert.equal(menu.current.url, null);
  });
});

describe("readableAddress", () => {
  it("reads an address as its host and path", () => {
    assert.equal(readableAddress("https://sqlflow.example.com"), "sqlflow.example.com");
    assert.equal(readableAddress("https://sqlflow.example.com/"), "sqlflow.example.com");
    assert.equal(readableAddress("http://localhost:5173/workbench//"), "localhost:5173/workbench");
  });

  it("reads nothing that is not an http(s) address", () => {
    for (const url of ["", "sqlflow.example.com", "javascript:alert(1)", "data:text/html,x", "ftp://sqlflow.example.com"]) {
      assert.equal(readableAddress(url), null, url);
    }
  });
});
