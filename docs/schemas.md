# Schemas

A schema is one telemetry type: the shape of a record the client sends and the shape of the
row that reaches storage. Schemas are the collector's declaration of what data it accepts and
what it keeps.

Schemas are attributed POCOs under `src/Telemetee.Telemetry/TelemetryData/`. One file per
schema holds two types:

- The **input POCO** inherits `TelemetryRecord`. Its `[Telemetry("name")]` attribute names the
  schema on the wire. Each property carries one treatment attribute.
- The nested **`Sink` class** inherits `TelemetrySinkRecord` and implements `IRetained`. It
  declares one `required` property per input field at the post-treatment type, plus a
  `RetentionTime` constant.

Available treatments on an input property:

| Attribute          | Effect                                                                                            |
| ------------------ | ------------------------------------------------------------------------------------------------- |
| `[Keep]`           | Passes the value through verbatim. The sink property matches the input CLR type.                  |
| `[Bucket(nameof)]` | Replaces the value with the label the bucketer returns. Records off the end drop. Sink is string. |
| `[Pseudonymize]`   | HMAC-SHA256s the value under the pseudonym salt. Sink is a hex string.                            |

Input property types are `string`, `bool`, `int`, `long`, `float`, `double`. Nested objects
and arrays are not supported today, so a schema that needs structure flattens it client-side.
Input properties map PascalCase → snake_case on the wire.

The attested image guarantees that records reach storage only after they pass through one of
these declared sink shapes, and that each schema's retention is what this build declares.

## Flow correlation

Cross-event correlation within one flow is a property each schema declares explicitly. The
intended primitive is a client-generated `flow_id` plus a `flow_kind` that names the product
intent the id spans.
The client holds the id for the life of the intent.

- `flow_kind` is `[Keep]`. It is a closed set of named intents known to the collector.
  Reviewers reason about a whole flow kind as a unit. Analyzing which events it emits, which
  fields are allowed, how much the sequence through its steps could fingerprint a user.
- `flow_id` is `[Pseudonymize]`. Server-side HMAC makes the correlation token one-way at the
  collector such that a client-state leak alone cannot be joined back to collected data.
  Within-flow correlation is intact because the same raw id always pseudonymizes to the same
  output under one salt.

Pseudonymize on anything other than `flow_id` is a red flag — see the discussion below.

## Adding a telemetry type

Add a POCO plus its nested `Sink` into `TelemetryData/`. The input type appears in the OpenAPI
document and the generated client bindings on the next build. The code under `Schemas/` is
machinery and routine schema work does not change it.

After adding or changing a POCO, regenerate the Rust bindings. See
[client-bindings.md](client-bindings.md) for the script and when to run it.

At startup, `SchemaBuilder` scans the assembly for `[Telemetry]`-marked subtypes of
`TelemetryRecord` and compiles a per-schema `ProcessFunc` for each. The compiled body is a
single `new Sink { ... }` member-init with a `Treatments.X(root, name, ...)` call per field. A
missing nested `Sink`, a sink property whose type does not match the treatment's output type,
a `[Bucket]` whose named bucketer is wrong, or any other shape drift throws at service
registration time.

If a record carries a field the schema does not declare, the collector drops that field
silently and keeps the rest, so older collectors stay forward-compatible with newer clients.
It drops and counts whole records with an unknown schema, malformed JSON, or over the record
cap. The reason tag is on `telemetee.batch.records_dropped`.

## Reviewing a new schema

Every new schema should pass two checks before merge: whether batch_time anonymizes it, and
how much each retained field could reveal about an individual. Schemas that carry a flow get a
third check on sequence fingerprinting across the flow.

### Does batch_time anonymize this schema?

Batch_time coarsens timing on every row. It only prevents re-identification if the flush
window holds enough records that a researcher cross-referencing the sink against production
state still faces many plausible candidates per row.

Estimate the schema's global events per second and multiply by `FlushInterval`. If the product
is below 10, batching does no anonymizing work for this schema. The fields themselves have to
carry the defense. The usual low-rate shape is an org-level lifecycle event such as SSO setup
or a billing-plan change. Even a few events per day globally, multiplied by any usable flush
window, still stays well below the threshold. For a schema in that band, every retained field
is a potential join key against production state. Audit each one, and omit anything that would
narrow candidates.

### Judging a field's discriminating power

For each retained field, ask three questions.

- **Value distribution.** How many distinct values can the field take, and how uneven is the
  distribution? A boolean split roughly 50/50 adds one bit of narrowing. A categorical with a
  dominant mode (90% English locale, say) adds less than one bit on average, and the long
  tail is where re-identification lands. A numeric or free-text field with per-entity
  variance adds many bits; use wide buckets or omit.
- **Join to production state.** Is the same field also present in the production DB for the
  same entity? Plan tier, release channel, locale, version string, feature state, and
  geography are all visible to a researcher with DB access. Any such field becomes a join
  key, and the candidate set at the sink shrinks from "entities in the window" to "entities
  with matching value in prod." If the DB has fewer than ~100 rows matching the value, the
  join and the time resolution together are strong enough to serve as an identifier.
- **Repetition across rows.** Does the same entity emit the field many times? Stable
  per-entity values (version, locale, device shape) multiply across the entity's events, so
  an adversary intersects values across rows to narrow further. A one-shot lifecycle event is
  safer per-row but is still subject to the batch_time check above.

Multiply the number of plausible values each retained field can take. That product is the
number of distinct value-combinations a record can land in. If the product exceeds the number
of entities emitting records in the flush window, most entities get a value-combination of
their own, and any single row pins its entity down — even if no field in isolation looked
dangerous. For scale: three fields with 100 plausible values each give one million
combinations against perhaps 10,000 entities per flush window, so almost every row is unique.
Sweeney's result that ZIP + date-of-birth + sex pins down ~87% of the US population comes out
of the same math. To deal with these kind of fields that have many possible values, prefer
bucket to keep and omit to bucket.

### Flows: review as a unit, not per row

When a schema carries `flow_id`, the three field questions apply across the whole flow, not
just one row. All events sharing a `flow_id` form one sequence; the attacker's view is the
full ordered trace of that intent, not a single record. Evaluate the schema against the
flow's expected shape:

- **Sequence fingerprinting.** For a complex `flow_kind` (SSO setup is the usual example),
  the path through the knobs — provider choice, cert type, SCIM on/off, test outcomes, retry
  pattern — is a fingerprint even when no individual field looks dangerous. Count the
  value-combinations across the flow, not within one event. If the product can run into the
  thousands, the sequence re-identifies the user regardless of how opaque the id is.
- **Length of the intent.** A long-lived flow (days or weeks) accumulates more sequence
  entropy than a short one. If an intent is naturally long but the correlation window should
  be shorter, split it into multiple shorter flows at product boundaries with no linkage
  between them.
- **Flow-id handling.** `flow_id` is `[Pseudonymize]`. Treating it any other way defeats the
  one-way property that lets client-state leaks and sink leaks remain uncorrelatable without
  the salt.

### Pseudonymize outside of flow_id

`[Pseudonymize]` on any field other than `flow_id` needs a justification during review.
Omitting an identifier field from the POCO is strictly better than pseudonymizing it — an
undeclared field never crosses the trust boundary at all, while a pseudonymized one enables
within-salt correlation that the collector's design otherwise forbids. The join-to-production
check becomes a hard no for any pseudonymized field: one joinable field anywhere in the graph
names the entity and unlocks all events sharing the pseudonym.

Pseudonymization is HMAC-SHA256 under a 32-byte salt. The salt lives in collector memory and
rotates on process restart.
