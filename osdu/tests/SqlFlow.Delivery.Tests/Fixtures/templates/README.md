# Test templates

The template folder of the fixture documents (`Fixtures/documents`, below it), holding the templates of the kinds the
sample estate's own template folder (`osdu/samples/welldb/templates`) does not carry. The fixture documents read both:
the build links the sample's templates into `Fixtures/documents/templates`, the folder nearest them, and this one is
above it ([templates](../../../../docs/reference/concepts/templates.md#template-folders)).

| File | Kind | Template version | Origin |
| --- | --- | --- | --- |
| `osdu_wks_work-product-component--WellLog_1.5.0.json` | `osdu:wks:work-product-component--WellLog:1.5.0` | `2f8a99cb38d32480` | OSDU data definitions v0.30.0 (99f8fc88d8ad), `Generated/work-product-component/WellLog.1.5.0.json` |

Each file is the schema as `sqlflow template capture --kind <kind> --release <release>` saves it: fetched from the Open
Group's data definitions and bundled with every shared schema it refers to, then written out with two-space
indentation. The template version is computed from the schema's content, so the indentation does not change it. The
schemas are published under the Apache License 2.0, which each file carries in `x-osdu-license`.

`SchemaVersionPipelinesTests` uses WellLog 1.5.0 to deliver the sample well log rows through a second pipeline on the
next version of the kind the sample flow delivers (WellLog 1.4.0).
