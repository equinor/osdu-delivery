import assert from 'node:assert/strict';
import { test } from 'node:test';
import { objectParts, templateFileName } from '../src/objectNames.ts';

test('a template file is named after its kind', () => {
    assert.equal(templateFileName('osdu:wks:master-data--Well:1.2.0'), 'osdu_wks_master-data--Well_1.2.0.json');
});

test('an object a flow names reads as its parts, bracketed or not', () => {
    assert.deepEqual(objectParts('OsduData.silver.Well'), { name: 'Well', schema: 'silver', database: 'OsduData' });
    assert.deepEqual(objectParts('[OsduData].[silver].[Well]'), { name: 'Well', schema: 'silver', database: 'OsduData' });
    assert.deepEqual(objectParts('Well'), { name: 'Well', schema: undefined, database: undefined });
});
