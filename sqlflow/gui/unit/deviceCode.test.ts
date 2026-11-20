// How the device page reads a sign-in code from a person (src/auth/deviceCode.ts): what they type or paste becomes the
// code the control plane minted, in the shape it shows it.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { deviceCodeBoxAt, deviceCodeCharacters, formatDeviceCode, isWholeDeviceCode } from "../src/auth/deviceCode.ts";

describe("a sign-in code as a person types it", () => {
  it("keeps a code the control plane minted as it is", () => {
    assert.equal(formatDeviceCode("WDJB-MJHT"), "WDJB-MJHT");
  });

  it("is upper case, whatever case was typed", () => {
    assert.equal(formatDeviceCode("wdjb-mjht"), "WDJB-MJHT");
  });

  it("puts the dash back once the second group begins, and not before", () => {
    assert.equal(formatDeviceCode("WDJ"), "WDJ");
    assert.equal(formatDeviceCode("WDJB"), "WDJB");
    assert.equal(formatDeviceCode("WDJBM"), "WDJB-M");
  });

  it("drops a dash typed after the first group, so typing one changes nothing", () => {
    assert.equal(formatDeviceCode("WDJB-"), "WDJB");
  });

  it("takes a pasted code with spaces, other separators or the line it was copied from around it", () => {
    assert.equal(formatDeviceCode(" wdjb mjht "), "WDJB-MJHT");
    assert.equal(formatDeviceCode("WDJB_MJHT\n"), "WDJB-MJHT");
  });

  it("stops at the eight characters a code holds", () => {
    assert.equal(formatDeviceCode("WDJBMJHTXY"), "WDJB-MJHT");
    assert.equal(deviceCodeCharacters("WDJB-MJHT-XY"), "WDJBMJHT");
  });

  it("is whole only with all eight characters", () => {
    assert.equal(isWholeDeviceCode("WDJB-MJHT"), true);
    assert.equal(isWholeDeviceCode("wdjbmjht"), true);
    assert.equal(isWholeDeviceCode("WDJB-MJH"), false);
    assert.equal(isWholeDeviceCode(""), false);
  });
});

describe("the box a caret types into", () => {
  it("is the caret's position in the first group", () => {
    assert.equal(deviceCodeBoxAt(0), 0);
    assert.equal(deviceCodeBoxAt(3), 3);
  });

  it("is the second group's first box on either side of the dash", () => {
    assert.equal(deviceCodeBoxAt(4), 4);
    assert.equal(deviceCodeBoxAt(5), 4);
  });

  it("counts past the dash in the second group", () => {
    assert.equal(deviceCodeBoxAt(6), 5);
    assert.equal(deviceCodeBoxAt(9), 8);
  });
});
