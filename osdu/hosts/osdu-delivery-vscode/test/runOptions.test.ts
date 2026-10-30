import assert from 'node:assert/strict';
import { test } from 'node:test';
import { declaredPartitions, objectParts, runOptions, runPayload, templateFileName } from '../src/runOptions.ts';

test('an operation takes what the GUI\'s run dialog offers it', () => {
    assert.deepEqual(runOptions('delivery', 'deliver'), ['force', 'submissionId', 'recordKeys', 'sendAgain', 'interfaces']);
    assert.deepEqual(runOptions('delivery', undefined), runOptions('delivery', 'deliver'));
    assert.deepEqual(runOptions('delivery', 'verify'), ['force', 'recordKeys', 'interfaces']);
    assert.deepEqual(runOptions('delivery', 'sync'), ['recordKeys', 'interfaces']);
    assert.deepEqual(runOptions('delivery', 'drain'), ['submissionId', 'interfaces']);
    assert.deepEqual(runOptions('delivery', 'plan'), ['force', 'interfaces']);
    assert.deepEqual(runOptions('cache', 'refresh'), ['types']);
    assert.deepEqual(runOptions('retrieval', undefined), ['force']);
});

test('a kind with no options takes none', () => {
    assert.deepEqual(runOptions('ing', undefined), []);
    assert.deepEqual(runOptions('assertion', undefined), []);
});

test('nothing chosen is no payload at all', () => {
    assert.equal(runPayload(runOptions('delivery', 'deliver'), {}), undefined);
});

test('what was chosen reads as the keys the kind takes', () => {
    assert.deepEqual(
        runPayload(runOptions('delivery', 'deliver'), { force: true, submissionId: 's-1', interfaces: ['wellbores'], sendAgain: 'metadata' }),
        { force: true, submissionId: 's-1', redeliver: 'metadata', interfaces: ['wellbores'] }
    );
    assert.deepEqual(runPayload(runOptions('delivery', 'deliver'), { sendAgain: 'uptodate' }), { rerender: true });
    assert.deepEqual(runPayload(runOptions('cache', 'refresh'), { types: ['b', 'a'] }), { types: ['a', 'b'] });
});

test('records named by key are always sent again', () => {
    assert.deepEqual(runPayload(runOptions('delivery', 'deliver'), { recordKeys: ['k'] }), { recordKeys: ['k'], redeliver: 'all' });
    assert.deepEqual(runPayload(runOptions('delivery', 'deliver'), { recordKeys: ['k'], sendAgain: 'uptodate' }), { recordKeys: ['k'], rerender: true });
});

test('an option the operation does not take is dropped', () => {
    assert.equal(runPayload(runOptions('delivery', 'sync'), { force: true, sendAgain: 'all' }), undefined);
});

test('a flow\'s partitions are read however it writes them', () => {
    assert.deepEqual(declaredPartitions(['name: x', 'partitions: [dev, "test"]']), ['dev', 'test']);
    assert.deepEqual(declaredPartitions(['partitions:', '  - name: dev', '  - name: test  # later', 'source:', '  - name: no']), ['dev', 'test']);
    assert.deepEqual(declaredPartitions(['partitions:', '  - dev', '', '  - test', 'source: x']), ['dev', 'test']);
    assert.deepEqual(declaredPartitions(['name: x']), []);
});

test('a template file is named after its kind', () => {
    assert.equal(templateFileName('osdu:wks:master-data--Well:1.2.0'), 'osdu_wks_master-data--Well_1.2.0.json');
});

test('an object a flow names reads as its parts, bracketed or not', () => {
    assert.deepEqual(objectParts('OsduData.silver.Well'), { name: 'Well', schema: 'silver', database: 'OsduData' });
    assert.deepEqual(objectParts('[OsduData].[silver].[Well]'), { name: 'Well', schema: 'silver', database: 'OsduData' });
    assert.deepEqual(objectParts('Well'), { name: 'Well', schema: undefined, database: undefined });
});
