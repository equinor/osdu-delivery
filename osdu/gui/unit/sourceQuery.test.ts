// Which records a query of an attribute's source narrows to (osdu/docs/reference/concepts/gui.md, Mappings).

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { failingKeys } from "../src/features/delivery/sourceQuery.ts";

const key = (id: string) => JSON.stringify(["PROJECT_A", id]);

describe("the failing rows a query narrows to", () => {
  it("are the records the findings name, each once, in the order they name them", () => {
    const found = failingKeys([
      { samples: [{ keyJson: key("LOG-2") }, { keyJson: key("LOG-1") }] },
      { samples: [{ keyJson: key("LOG-1") }, { keyJson: key("LOG-3") }] },
    ]);
    assert.deepEqual(found.keys, [key("LOG-2"), key("LOG-1"), key("LOG-3")]);
    assert.equal(found.named, 3);
  });

  it("leave out a sample with no key, from an older node or a row with an empty key part", () => {
    assert.deepEqual(failingKeys([{ samples: [{ keyJson: null }, {}, { keyJson: key("LOG-1") }] }]).keys, [key("LOG-1")]);
  });

  it("are none when nothing was found", () => {
    assert.deepEqual(failingKeys([]), { keys: [], named: 0 });
  });

  it("stop at what a request can carry, and still say how many were named", () => {
    const many = Array.from({ length: 400 }, (_, i) => ({ keyJson: key(`LOG-${String(i).padStart(6, "0")}`) }));
    const found = failingKeys([{ samples: many }]);
    assert.equal(found.named, 400);
    assert.ok(found.keys.length > 0 && found.keys.length < 400);
    assert.ok(`[${found.keys.join(",")}]`.length <= 4000);
  });
});
