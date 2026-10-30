import { DocumentSkeleton } from '../../../../sqlflow/tools/sqlflow-vscode/src/editor/documentActions';

/** The connection every OSDU flow declares: references alone, which a run resolves. */
const OSDU_AUTH = `  endpoint: \${env:OSDU_URL}
  auth:
    type: oauth2ClientCredentials
    secondarySecretRef: \${env:OSDU_CLIENT_ID}
    secretRef: \${env:OSDU_CLIENT_SECRET}
    token:
      url: \${env:OSDU_TOKEN_URL}
      body:
        scope: \${env:OSDU_SCOPE}
`;

/** Every document kind a repository holds, with the keys it cannot do without. Each is checked by the suite against the
 * language engine, so `New Document` never starts a person on a document the editor underlines. */
export const SKELETONS: DocumentSkeleton[] = [
    {
        label: 'Delivery flow',
        detail: 'flowType: delivery, ingestion rows into OSDU records through a mapping',
        content: `flowType: delivery
name: newsystem-thing-03-delivery
batch: newsystem

partitions:
  - name: dev

source:
  connection: \${env:OSDU_DATA_DB}
  record:
    object: OsduData.silver.Thing
    key: [thing_id]
    primaryKey: RecId
  lastModified: update_date
  work: ../.work/thing

render:
  mapping: Thing@1.0.0

change:
  detect: renderedHash
  onUnchanged: skip

target:
${OSDU_AUTH}  protocol: storage
`,
    },
    {
        label: 'Mapping',
        detail: 'documentType: mapping, how a row becomes a record of a template',
        content: `documentType: mapping
name: Thing
version: 1.0.0
template:
  kind: osdu:wks:master-data--Thing:1.0.0

dataset:
  system: newsystem
  key: [thing_id]

parameters:
  aclOwner:
    required: true
    description: The entitlements group that owns every record this mapping renders (acl.owners).
  aclViewer:
    required: true
    description: The entitlements group that may read every record this mapping renders (acl.viewers).
  legalTag:
    required: true
    description: The legal tag every record this mapping renders carries (legal.legaltags).

record:
  acl:
    owners: ["{$param.aclOwner}"]
    viewers: ["{$param.aclViewer}"]
  legal:
    legaltags: ["{$param.legalTag}"]
    otherRelevantDataCountries: [US]
  data:
    Name:
      $from: thing_name
`,
    },
    {
        label: 'Cache flow',
        detail: 'flowType: cache, the lookup and reference values mappings read',
        content: `flowType: cache
name: newsystem-lookups-00-cache
batch: newsystem

partitions: [dev]

source:
  connection: \${env:OSDU_DATA_DB}

types:
  - table: OsduData.silver.Lookup
    name: Lookup
    key: source_value
    fields: [osdu_value]
`,
    },
    {
        label: 'Assertion flow',
        detail: 'flowType: assertion, what must hold of the records OSDU has',
        content: `flowType: assertion
name: newsystem-thing-04-assertion
batch: newsystem
partitions: [dev]

source:
${OSDU_AUTH}
tests:
  - name: thing-rules
    description: Each delivered record meets the rules its mapping states.
    kind: osdu:wks:master-data--Thing:1.0.0
    query: 'tags.DeliveredBy:"osdu-delivery"'
    assert:
      - mapping: Thing@1.0.0
`,
    },
    {
        label: 'Dictionary',
        detail: 'documentType: dictionary, a lookup table kept in the repository',
        content: `documentType: dictionary
name: Aliases
key: source_value
fields: [osdu_value]
entries:
  a-source-value:
    osdu_value: an-osdu-value
`,
    },
    {
        label: 'Repository document',
        detail: 'documentType: repository, the code this repository\'s runs carry in their correlation id',
        content: `documentType: repository
code: newsystem
description: The flows of the new system.
`,
    },
    {
        label: 'Ingestion flow',
        detail: 'flowType: ing, a table keyed into the ingestion tables a delivery reads',
        content: `flowType: ing
name: newsystem-thing-02-ing
batch: newsystem
source:
  server: src
  table: dbo.source
target:
  server: dwh
  table: silver.Thing
load:
  mode: append
`,
    },
    {
        label: 'Schedule library',
        detail: 'schedules.yaml, named schedules flows join',
        content: `schedules:
  newsystem-nightly:
    cron: "0 1 * * *"
    timezone: UTC
`,
    },
];
