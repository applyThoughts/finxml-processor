# Mapping profiles

A mapping profile is a JSON document that tells the processor where records live in the XML, which values become
which columns, how to parse and validate them, and which fields form the record duplicate key. Every profile is
validated against the embedded JSON Schema (`finxml profile schema` prints it) and a set of semantic rules before it
can be activated. No XPath, scripting or expressions are supported by design; complex logic belongs in an
`IRecordMapperFactory` selected by `mapperType`.

Installed profiles live in `<data folder>/profiles/*.json`. The active profile is `Processing:ActiveProfileId`.

## Top level

| Property | Required | Meaning |
| --- | --- | --- |
| `schemaVersion` | yes | Always `1` |
| `id` | yes | `^[a-z0-9][a-z0-9-]{1,63}$`; used in output file names |
| `displayName`, `version` (semver), `description` | yes / yes / no | Shown in the UI and recorded on every job |
| `isSynthetic` | no | `true` flags demo rules in the UI, reports and Summary sheet |
| `mapperType` | no | `profile` (default) selects the declarative mapper |
| `namespaces` | no | prefix → URI; key `""` declares the default namespace for unprefixed element names |
| `recordPath` | yes | Absolute element path from the document root to the repeating record, one qualified name per entry, e.g. `["t:TransactionBatch","t:Transactions","t:Transaction"]`. The first entry must match the document root. |
| `xsdPath` | no | Optional XSD (absolute or relative to the profile file). Validation runs in the same streaming pass. |
| `safeIdentifierField` | no | Field whose value may identify a record in reports and rejection output; must map to a column classified `none` |
| `duplicateKeyFields` | no | Fields forming the composite duplicate key; empty disables record duplicate detection |
| `tables` | yes | Output sheets |
| `fields` | yes | Source → column bindings |

## Tables and columns

```json
{
  "id": "transactions",
  "sheetName": "Transactions",
  "columns": [
    { "id": "amount", "heading": "Amount", "cellType": "decimal", "width": 16, "numberFormat": "#,##0.00", "sensitivity": "restricted" }
  ]
}
```

- `cellType`: `text` (default for identifiers; never coerced), `integer`, `decimal` (System.Decimal, exact),
  `date`, `dateTime` (normalised to UTC), `boolean`.
- `width`: Excel column width (1–255); omitted widths use a small per-type default. Rows are never retained to auto-size.
- `numberFormat`: Excel format code; dates default to `yyyy-mm-dd`, date-times to `yyyy-mm-dd hh:mm:ss`.
- `sensitivity`: `none` (may appear in logs/reports/rejections), `sensitive` (masked to the last four characters; in
  rejection output only if `allowInRejectionOutput` is true), `restricted` (never outside the workbook).
- `Summary` and `Rejected Records` are reserved sheet names. Sheet names are sanitised to Excel rules and split into
  `Name (2)`, `Name (3)` … when a table exceeds 1,048,576 rows including the header.

## Fields

```json
{
  "id": "currency",
  "source": "t:Amount/@currency",
  "table": "transactions",
  "column": "currency",
  "required": true,
  "default": null,
  "trim": true,
  "transforms": [ { "type": "upper" } ],
  "parse": { "dateFormats": [], "culture": null, "trueValues": [], "falseValues": [], "allowThousands": false },
  "validation": { "pattern": "[A-Z]{3}" }
}
```

- `source` is relative to the record element: `t:Amount` (child text), `t:Header/t:Id` (nested), `t:Amount/@currency`
  (attribute of a child), `@sequence` (attribute of the record) or `.` (record text). Omit it for constant fields.
- Processing order: evaluate source → transforms → trim → default if empty → required check → parse to the column
  type → field validation. A missing required value or a failed parse rejects the record (`MAP-*` codes); a failed
  validation rule rejects it (`VAL-*` codes). Parse errors describe the value's *shape*, never its content.
- `transforms`: `upper`, `lower`, `trim`, `normalizeWhitespace`, `constant` (`value`), `concat` (`sources`
  relative paths appended to the primary value with `separator`).
- `parse.dateFormats` (required for date/dateTime columns) are exact .NET custom formats tried in order, e.g.
  `yyyy-MM-dd'T'HH:mm:ssK`. `parse.culture` sets decimal separators (`""`/null = invariant). Booleans accept
  `trueValues`/`falseValues` or the defaults `true/1/y/yes` and `false/0/n/no`.
- `validation`: `minLength`, `maxLength`, `pattern` (anchored, timed-out regex), `allowedValues`, `caseInsensitive`
  for text; `min`/`max` for integer/decimal; `minDate`/`maxDate` (`yyyy-MM-dd`) for date/dateTime. Type-incompatible
  rules are rejected when the profile is validated.
- A column may be bound by at most one field; unbound columns stay blank.

## Issue codes

`FILE-001…009` file checks · `XML-001…006` XML/schema · `MAP-001…008` mapping/conversion ·
`VAL-001…008` validation and record duplicates · `OUT-001…004` workbook · `JOB-001…006` job. The codes are stable;
new ones are appended, never renumbered (`Domain/Issues/IssueCodes.cs`).

## The synthetic demo profile

`samples/profiles/demo-fintech-v1.json` maps a fictitious `TransactionBatch/Transactions/Transaction` document in
namespace `urn:example:fintech:demo:v1` to one `Transactions` sheet with transaction id, account reference
(sensitive), posted timestamp, value date, amount (restricted), currency, direction, status, counterparty
(sensitive, excluded from rejections), description, reversal flag, sequence, batch id and a constant source system.
The duplicate key is the transaction id. **All rules are placeholders.** Replace the profile with the real one once
the XML schema and business rules are supplied; nothing else in the application needs to change for a profile-shaped
mapping.

Generate matching synthetic input with `tools/FinXmlProcessor.TestDataGenerator` (see its `--help`); the generator
can inject missing fields, invalid dates/decimals, duplicates, special characters and Unicode, long values,
a default-namespace serialisation, a DOCTYPE, or a truncated document.
