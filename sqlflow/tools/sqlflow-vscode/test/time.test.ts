import assert from 'node:assert/strict';
import { test } from 'node:test';
import { ago, parseUtc } from '../src/time.ts';

test('a timestamp with no zone is read as UTC, whatever zone this machine is in', () => {
    assert.equal(parseUtc('2026-11-16T00:12:30'), Date.UTC(2026, 10, 16, 0, 12, 30));
    assert.equal(parseUtc('2026-11-16T00:12:30.5'), Date.UTC(2026, 10, 16, 0, 12, 30, 500));
    assert.equal(parseUtc('2026-11-16T00:12:30Z'), Date.UTC(2026, 10, 16, 0, 12, 30));
    assert.equal(parseUtc('2026-11-16T01:12:30+01:00'), Date.UTC(2026, 10, 16, 0, 12, 30));
});

test('how long ago is said the way a person says it', () => {
    const now = Date.UTC(2026, 10, 16, 12, 0, 0);
    assert.equal(ago('2026-11-16T11:59:30', now), 'just now');
    assert.equal(ago('2026-11-16T11:30:00', now), '30m ago');
    assert.equal(ago('2026-11-16T07:00:00Z', now), '5h ago');
    assert.equal(ago('2026-11-10T12:00:00Z', now), '6d ago');
    assert.equal(ago(null, now), '');
    assert.equal(ago('not a time', now), '');
});
