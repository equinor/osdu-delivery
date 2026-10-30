import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import {
    checkRecord,
    childrenOf,
    declaredParameters,
    describeVariable,
    parentRecordPath,
    recordEntries,
    recordPath,
    templateKind,
    templateVariables,
} from '../src/templates.ts';

const samples = join(dirname(fileURLToPath(import.meta.url)), '..', '..', '..', 'samples', 'welldb');
const wellLog = JSON.parse(readFileSync(join(samples, 'templates', 'osdu_wks_work-product-component--WellLog_1.4.0.json'), 'utf8'));
const variables = templateVariables(wellLog);
const named = (path: string) => variables.find((variable) => variable.path === path);

test('a published template lays out as the record a mapping fills', () => {
    assert.equal(named('acl.owners')?.type, 'array');
    assert.equal(named('legal.legaltags')?.type, 'array');
    assert.equal(named('data.Name')?.type, 'string');
    assert.equal(named('id'), undefined, 'the record id is not a mapping\'s');
});

test('a property reached through allOf and $ref keeps what the schema says of it', () => {
    const wellbore = named('data.WellboreID');
    assert.deepEqual(wellbore?.entityTypes, ['Wellbore']);
    assert.equal(named('data.TopMeasuredDepth')?.type, 'number');
    assert.ok(named('data.TopMeasuredDepth')?.unitContext !== undefined || named('data.TopMeasuredDepth')?.description !== undefined);
});

test('an array of objects steps into its items', () => {
    const unit = named('data.Curves[].CurveUnit');
    assert.equal(unit?.type, 'string');
    assert.deepEqual(unit?.entityTypes, ['UnitOfMeasure']);
    assert.ok(childrenOf(variables, 'data.Curves[]').some((variable) => variable.path === 'data.Curves[].CurveUnit'));
});

test('the required properties of the record are told', () => {
    assert.equal(named('acl')?.required, true);
    assert.equal(named('legal')?.required, true);
});

test('a schema that refers to itself ends', () => {
    const recursive = {
        properties: { data: { $ref: '#/definitions/Node' } },
        definitions: { Node: { type: 'object', properties: { child: { $ref: '#/definitions/Node' }, name: { type: 'string' } } } },
    };
    const paths = templateVariables(recursive).map((variable) => variable.path);
    assert.ok(paths.includes('data.name'));
    assert.ok(paths.length < 100);
});

test('a template file says its kind, or its name does', () => {
    assert.equal(templateKind(wellLog, 'x.json'), 'osdu:wks:work-product-component--WellLog:1.4.0');
    assert.equal(templateKind({}, 'osdu_wks_master-data--Well_1.2.0.json'), 'osdu:wks:master-data--Well:1.2.0');
    assert.equal(templateKind({}, 'notes.json'), undefined);
});

test('a mapping\'s key path reads as the template\'s', () => {
    assert.equal(recordPath('record.data.Name'), 'data.Name');
    assert.equal(recordPath('record.data.Curves.$item.CurveUnit'), 'data.Curves[].CurveUnit');
    assert.equal(recordPath('record.data.Name.$from'), null);
    assert.equal(recordPath('record.data.WellID.$findBy.value'), null);
    assert.equal(recordPath('dataset.key'), null);
});

const mapping = [
    'documentType: mapping',
    'record:',
    '  acl:',
    '    owners: ["{$param.aclOwner}"]',
    '  data:',
    '    Name:',
    '      $from: log_name',
    '    Curves:',
    '      $forEach: curves',
    '      $item:',
    '        CurveUnit: unit',
    '        NotAProperty: x',
    '    ',
];

test('the keys a record holds are found where they stand', () => {
    const entries = recordEntries(mapping).map((entry) => entry.path);
    assert.deepEqual(entries, ['acl', 'acl.owners', 'data', 'data.Name', 'data.Curves', 'data.Curves[].CurveUnit', 'data.Curves[].NotAProperty']);
});

test('a new key lands in the object above it', () => {
    assert.equal(parentRecordPath(mapping, 12), 'data');
    assert.equal(parentRecordPath([...mapping.slice(0, 11), '        '], 11), 'data.Curves[]');
    assert.equal(parentRecordPath(['record:', '  '], 1), '');
    assert.equal(parentRecordPath(['dataset:', '  '], 1), null);
});

test('a key the template lacks and a required property nothing fills are said', () => {
    const findings = checkRecord(mapping, variables, 'WellLog');
    assert.ok(findings.some((finding) => finding.message.includes("'data.Curves[].NotAProperty'")), JSON.stringify(findings));
    assert.ok(findings.some((finding) => finding.message.includes("requires 'legal'")));
    assert.ok(!findings.some((finding) => finding.message.includes("'data.Name'")));
    assert.equal(findings.find((finding) => finding.message.includes('NotAProperty'))?.line, 11);
});

test('the sample mappings stand against their templates with nothing to say of a key', () => {
    const text = readFileSync(join(samples, 'mappings', 'WellLog@1.4.0.yaml'), 'utf8').split(/\r?\n/);
    const unknown = checkRecord(text, variables, 'WellLog').filter((finding) => finding.message.includes('holds no property'));
    assert.deepEqual(unknown, []);
});

test('a property\'s hover says what the template says', () => {
    const text = describeVariable({ path: 'data.WellboreID', type: 'string', required: true, title: 'Wellbore', description: 'The bore.', entityTypes: ['Wellbore'], open: false });
    assert.equal(text, '**Wellbore** (`string`, required, points to Wellbore)\n\nThe bore.');
});

test('a document\'s parameters are read with their settings', () => {
    const parameters = declaredParameters([
        'parameters:',
        '  logSource:',
        '    required: true',
        '    description: "Which log source"',
        '  batchSize:',
        '    default: 100',
        'record:',
        '  data: {}',
    ]);
    assert.deepEqual(parameters, [
        { name: 'logSource', required: true, description: 'Which log source' },
        { name: 'batchSize', required: false, defaultValue: '100' },
    ]);
});
