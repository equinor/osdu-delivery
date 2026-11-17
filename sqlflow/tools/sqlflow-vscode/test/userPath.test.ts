import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import {
    addToShellProfiles,
    expandWindows,
    PROFILE_LINE,
    profileFiles,
    readWindowsPath,
    removeFromShellProfiles,
    windowsPathHas,
    windowsPathWith,
    windowsPathWithout,
    withoutProfileLine,
    withProfileLine,
    writeWindowsPath,
} from '../src/userPath.ts';

const env = { USERPROFILE: 'C:\\Users\\Ada', SystemRoot: 'C:\\Windows' };
const folder = 'C:\\Users\\Ada\\.sqlflow\\bin';

test('a Windows Path names the folder however it is written', () => {
    assert.equal(expandWindows('%userprofile%\\.sqlflow\\bin;%NOPE%', env), 'C:\\Users\\Ada\\.sqlflow\\bin;%NOPE%');
    for (const written of ['%USERPROFILE%\\.sqlflow\\bin', 'c:\\users\\ada\\.sqlflow\\bin\\', ' C:\\Users\\Ada\\.sqlflow\\bin ']) {
        assert.ok(windowsPathHas(`C:\\Tools;${written};D:\\bin`, folder, env), written);
    }
    assert.ok(!windowsPathHas('C:\\Tools;C:\\Users\\Ada\\.sqlflow\\binaries', folder, env));
    assert.ok(!windowsPathHas('', folder, env));
});

test('the folder goes at the end of a Windows Path once, and every other entry stays as written', () => {
    assert.equal(windowsPathWith('%SystemRoot%\\system32;C:\\Tools;', folder, env), `%SystemRoot%\\system32;C:\\Tools;${folder}`);
    assert.equal(windowsPathWith('', folder, env), folder);
    const already = `C:\\Tools;%USERPROFILE%\\.sqlflow\\bin`;
    assert.equal(windowsPathWith(already, folder, env), already);
    assert.equal(windowsPathWithout(`%SystemRoot%\\system32;${folder};C:\\Tools`, folder, env), '%SystemRoot%\\system32;C:\\Tools');
    assert.equal(windowsPathWithout(already, folder, env), 'C:\\Tools');
    assert.equal(windowsPathWithout('C:\\Tools', folder, env), 'C:\\Tools');
});

test('a start-up file gets the line once, at its end, and loses only that line', () => {
    assert.equal(withProfileLine(''), `${PROFILE_LINE}\n`);
    assert.equal(withProfileLine('export EDITOR=vi'), `export EDITOR=vi\n${PROFILE_LINE}\n`);
    assert.equal(withProfileLine('a\r\nb\r\n'), `a\r\nb\r\n${PROFILE_LINE}\r\n`);
    const once = withProfileLine('export EDITOR=vi\n');
    assert.equal(withProfileLine(once), once);
    assert.equal(withoutProfileLine(once), 'export EDITOR=vi\n');
    assert.equal(withoutProfileLine('a\n  ' + PROFILE_LINE + '  \nb\n'), 'a\nb\n');
});

test('the line goes into .profile always, bash files that exist, and .zshenv for zsh or a Mac', () => {
    const exists = (have: string[]) => (file: string) => have.some((name) => file.endsWith(name));
    assert.deepEqual(profileFiles('/h', '/bin/bash', 'linux', exists([])), [join('/h', '.profile')]);
    assert.deepEqual(profileFiles('/h', '/bin/bash', 'linux', exists(['.bashrc'])), [join('/h', '.profile'), join('/h', '.bashrc')]);
    assert.deepEqual(profileFiles('/h', '/usr/bin/zsh', 'linux', exists([])), [join('/h', '.profile'), join('/h', '.zshenv')]);
    assert.deepEqual(profileFiles('/h', '/bin/bash', 'darwin', exists(['.bash_profile'])), [join('/h', '.profile'), join('/h', '.bash_profile'), join('/h', '.zshenv')]);
});

test('the shell profiles are written once, undone exactly, and a person\'s own lines are kept', () => {
    const home = mkdtempSync(join(tmpdir(), 'sqlflow-profiles-'));
    try {
        writeFileSync(join(home, '.bashrc'), 'alias ll="ls -l"\n');
        mkdirSync(join(home, '.config', 'fish'), { recursive: true });

        const first = addToShellProfiles('Product', home, '/bin/zsh', 'linux');
        assert.ok(first.changed.includes(join(home, '.sqlflow', 'path.sh')));
        for (const name of ['.profile', '.bashrc', '.zshenv']) {
            assert.ok(readFileSync(join(home, name), 'utf8').includes(PROFILE_LINE), name);
        }
        assert.match(readFileSync(join(home, '.sqlflow', 'path.sh'), 'utf8'), /\$HOME\/\.sqlflow\/bin/);
        assert.ok(existsSync(join(home, '.config', 'fish', 'conf.d', 'sqlflow.fish')));
        assert.deepEqual(addToShellProfiles('Product', home, '/bin/zsh', 'linux').changed, [], 'a second time changes nothing');

        const undone = removeFromShellProfiles(home);
        assert.ok(undone.changed.length >= 5);
        assert.equal(readFileSync(join(home, '.bashrc'), 'utf8'), 'alias ll="ls -l"\n');
        assert.ok(!existsSync(join(home, '.sqlflow', 'path.sh')));
        assert.ok(!existsSync(join(home, '.config', 'fish', 'conf.d', 'sqlflow.fish')));
        assert.deepEqual(removeFromShellProfiles(home).changed, []);
    } finally {
        rmSync(home, { recursive: true, force: true });
    }
});

test('the path script puts the folder on a POSIX shell\'s PATH once, however often it is read', { skip: process.platform === 'win32' && 'needs a POSIX shell' }, () => {
    const home = mkdtempSync(join(tmpdir(), 'sqlflow-script-'));
    try {
        addToShellProfiles('Product', home, '/bin/sh', 'linux');
        const script = join(home, '.sqlflow', 'path.sh');
        const out = execFileSync('/bin/sh', ['-c', `. "${script}"; . "${script}"; printf %s "$PATH"`], { env: { HOME: home, PATH: '/usr/bin:/bin' } }).toString();
        assert.equal(out, `/usr/bin:/bin:${home}/.sqlflow/bin`);
    } finally {
        rmSync(home, { recursive: true, force: true });
    }
});

// The user environment's own key is never touched by a test: a key of its own under HKEY_CURRENT_USER\Software stands in.
test('a Windows Path is read and written as written, its kind kept, and removed when emptied', { skip: process.platform !== 'win32' && 'the user environment is in the Windows registry' }, async () => {
    const key = `Software\\SqlFlowTests\\Environment-${process.pid}-${Date.now()}`;
    try {
        assert.deepEqual(await readWindowsPath(key), { value: '', kind: '' });
        await writeWindowsPath('%USERPROFILE%\\tools;C:\\Tools', '', key);
        assert.deepEqual(await readWindowsPath(key), { value: '%USERPROFILE%\\tools;C:\\Tools', kind: 'ExpandString' });
        await writeWindowsPath('C:\\Tools', 'String', key);
        assert.deepEqual(await readWindowsPath(key), { value: 'C:\\Tools', kind: 'String' });
        await writeWindowsPath('', 'String', key);
        assert.deepEqual(await readWindowsPath(key), { value: '', kind: '' });
    } finally {
        execFileSync('reg.exe', ['delete', `HKCU\\Software\\SqlFlowTests`, '/f'], { stdio: 'ignore' });
    }
});
