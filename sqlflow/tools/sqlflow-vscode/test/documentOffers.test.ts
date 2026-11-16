import assert from 'node:assert/strict';
import { test } from 'node:test';
import { DocumentOffers, listedName, type OfferedDocument } from '../src/gui/documentOffers.ts';

// What a module adds to the side bar's rows, checked with a module of the suite's own: a product that keeps `report`
// documents and offers a preview of each.

const flow: OfferedDocument = { kind: 'flow', flowType: 'ing', name: 'orders', relativePath: 'flows/orders.yaml' };
const report: OfferedDocument = { kind: 'document', documentType: 'report', name: 'Sales', relativePath: 'reports/Sales@1.2.0.yaml' };
const nothing = async (): Promise<void> => undefined;

function registry(): DocumentOffers<OfferedDocument, { id: string }> {
    const offers = new DocumentOffers<OfferedDocument, { id: string }>();
    offers.register((document, pipeline) => (document.kind === 'flow'
        ? [{ id: 'run', label: 'Run', primary: true, run: nothing }, ...(pipeline ? [{ id: 'page', label: 'Page', run: nothing }] : [])]
        : []));
    return offers;
}

test('a document is offered what every provider offers it, a module\'s after the host\'s own', () => {
    const offers = registry();
    offers.register((document) => (document.documentType === 'report' ? [{ id: 'preview', label: 'Preview', run: nothing }] : []));

    assert.deepEqual(offers.of(flow, undefined).map((offer) => offer.id), ['run']);
    assert.deepEqual(offers.of(flow, { id: 'p1' }).map((offer) => offer.id), ['run', 'page']);
    assert.deepEqual(offers.of(report, undefined).map((offer) => offer.id), ['preview']);
});

test('two offers of one id on a document are refused, naming the document', () => {
    const offers = registry();
    offers.register((document) => (document.kind === 'flow' ? [{ id: 'run', label: 'Run again', run: nothing }] : []));

    assert.throws(() => offers.of(flow, undefined), /flows\/orders\.yaml.*'run'/);
});

test('a module names the group its documents are listed under, once', () => {
    const offers = registry();
    assert.equal(offers.groupOf(report), 'report documents');
    offers.nameGroup('report', 'Reports');
    offers.nameGroup('report', 'Reports');

    assert.equal(offers.groupOf(report), 'Reports');
    assert.throws(() => offers.nameGroup('report', 'Papers'), /already listed under 'Reports'/);
});

test('SQLFlow\'s own documents are listed under their own headings', () => {
    const offers = registry();
    assert.equal(offers.groupOf(flow), 'ing flows');
    assert.equal(offers.groupOf({ kind: 'flow' }), 'file flows');
    assert.equal(offers.groupOf({ kind: 'schedules' }), 'Schedules');
    assert.equal(offers.groupOf({ kind: 'subscribers' }), 'Subscribers');
    assert.equal(offers.groupOf({ kind: 'ai-context' }), 'AI contexts');
});

test('a document is listed by its own name, by its file\'s when the file carries its version, and by its file when it has none', () => {
    assert.equal(listedName(flow), 'orders');
    assert.equal(listedName(report), 'Sales@1.2.0');
    assert.equal(listedName({ kind: 'schedules', relativePath: 'library/schedules.yaml' }), 'schedules.yaml');
    assert.equal(listedName({ kind: 'document', documentType: 'report', name: 'Sales', relativePath: 'reports/q3.yml' }), 'Sales');
});
