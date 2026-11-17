import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { after, test } from 'node:test';
import { shippedPrograms } from '../programs.mts';

// Which builds a package carries (programs.mts): release builds only, where a checkout also runs a debug one.

const root = mkdtempSync(join(tmpdir(), 'osdu-programs-'));
after(() => rmSync(root, { recursive: true, force: true }));

function touch(path: string): void {
    mkdirSync(join(path, '..'), { recursive: true });
    writeFileSync(path, '');
}

touch(join(root, 'sqlflow', 'tools', 'target', 'debug', 'sqlflow-lsp.exe'));
touch(join(root, 'osdu', 'hosts', 'osdu-delivery-mcp', 'target', 'release', 'osdu-delivery-mcp.exe'));
touch(join(root, 'osdu', 'hosts', 'osdu-delivery-mcp', 'target', 'debug', 'osdu-delivery-mcp.exe'));
touch(join(root, 'osdu', 'hosts', 'osdu-delivery-vscode', 'publish', 'cli', 'SqlFlow.Delivery.Cli.Host.exe'));

test('a package takes release builds only, so a debug build left behind is never shipped', () => {
    const programs = shippedPrograms({ production: true, platform: 'win32', root, cargoTargetDir: '' });
    assert.deepEqual(programs.map((program) => [program.name, program.file]), [
        ['sqlflow-lsp', 'sqlflow-lsp.exe'],
        ['osdu-delivery-mcp', 'osdu-delivery-mcp.exe'],
        ['sqlflow', 'sqlflow.exe'],
    ]);
    assert.equal(programs[0].built, undefined);
    assert.equal(programs[1].built, join(root, 'osdu', 'hosts', 'osdu-delivery-mcp', 'target', 'release', 'osdu-delivery-mcp.exe'));
    assert.equal(programs[2].built, join(root, 'osdu', 'hosts', 'osdu-delivery-vscode', 'publish', 'cli', 'SqlFlow.Delivery.Cli.Host.exe'));
});

test('a checkout runs a debug build when it has no release one', () => {
    const programs = shippedPrograms({ production: false, platform: 'win32', root, cargoTargetDir: '' });
    assert.equal(programs[0].built, join(root, 'sqlflow', 'tools', 'target', 'debug', 'sqlflow-lsp.exe'));
    assert.equal(programs[1].built, join(root, 'osdu', 'hosts', 'osdu-delivery-mcp', 'target', 'release', 'osdu-delivery-mcp.exe'));
});

test('a shared cargo target folder is looked in first, and a program on Linux or macOS has no extension', () => {
    const shared = join(root, 'cargo-target');
    touch(join(shared, 'release', 'sqlflow-lsp'));
    const programs = shippedPrograms({ production: true, platform: 'linux', root, cargoTargetDir: shared });
    assert.deepEqual(programs.map((program) => program.file), ['sqlflow-lsp', 'osdu-delivery-mcp', 'sqlflow']);
    assert.equal(programs[0].built, join(shared, 'release', 'sqlflow-lsp'));
    assert.equal(programs[0].candidates[0], join(shared, 'release', 'sqlflow-lsp'));
});
