e# Deploying the collector to Confidential ACI

Steps for a milestone 2 deployment: the attested Noise channel from milestone 1 plus the batched
anonymized sink to Azure Blob Storage. `az`, `cargo`, and `dotnet` must be available.
Docker must be running, because `confcom` hashes image layers through the
local daemon.

The sink writes Parquet to one virtual directory per schema under the configured container.
`Azure.Storage.Blobs` addresses both plain Blob Storage and ADLS Gen2 (Blob Storage with
Hierarchical Namespace enabled) with the same connection string. ADLS Gen2 is the recommended
target because the <code>{schema}/{filename}</code> path becomes a real directory under HNS and
the data lake query tooling (Synapse, Databricks, Fabric) consumes that layout natively.

Region is East US 2, which has the Confidential ACI SKU and hosts the `sharedeus2` attestation
provider the template defaults to. Resources go in the existing `vat-playground1` group in
subscription `33f1558f-095b-4d93-a879-372001c8c505`. The group's own region does not matter; every
command below passes `eastus2` explicitly.

## 0. One-time prerequisites

Resource providers register at subscription scope, and Contributor on the resource group cannot
do it. A subscription Owner has to run this once:

```sh
az provider register -n Microsoft.ContainerInstance
az provider register -n Microsoft.ContainerRegistry
```

Check with `az provider show -n Microsoft.ContainerInstance --query registrationState`. It must
say `Registered` before step 1.

You also need an Azure Storage connection string for the destination account. Pass it as the
`blobStorageConnectionString` parameter in step 4, along with a `blobStorageContainer` name. Both
are unpinned in the policy so an operator can redirect output without a new build; the collector
enforces nothing about the destination because anonymization already ran upstream.

## 1. Subscription and a private registry

ACI can only pull from a registry. A Basic ACR in the playground group keeps the image inside the
subscription. Step 4 authenticates ACI with a short-lived AD token minted from your current `az`
login, so no admin password or managed identity is created. The name must be globally unique.

```sh
az login
az account set --subscription 33f1558f-095b-4d93-a879-372001c8c505
az provider register -n Microsoft.ContainerInstance
az acr create -g vat-playground1 -n <acrname> -l eastus2 --sku Basic
az acr login -n <acrname>
```

## 2. Build and push the image

```sh
DIGEST=$(deploy/build-image.sh telemetee-collector:0.1.0 --push <acrname>.azurecr.io)
IMAGE="<acrname>.azurecr.io/telemetee-collector@$DIGEST"
echo "$IMAGE"
```

`build-image.sh` is the reproducible build: digest-pinned base images, `SOURCE_DATE_EPOCH` from
the commit, and layer timestamps rewritten through a `docker-container` BuildKit builder that it
creates on first use. The digest reference is what goes into the policy and the deployment,
never the tag.

## 3. Generate the CCE policy

```sh
DISABLE_STDIO=0 deploy/generate-policy.sh "$IMAGE"
```

This pins `confcom`, pulls both images to hash their layers, injects the policy into
`deploy/aci-template.generated.json`, writes the decoded policy to `deploy/policy.rego`, and
writes the hostdata to `deploy/hostdata.txt`. It then fails if the policy contains an env rule
that is not pinned to a literal value and not listed in `deploy/allowed-wildcards.txt`; see
[Secrets and wildcards](#secrets-and-wildcards).

`DISABLE_STDIO=0` is for experiments only: with stdio disabled, `az container logs` shows
nothing. `EXCLUDE_DEFAULT_FRAGMENTS=0` is the default; the Confidential ACI SKU requires
Microsoft's signed infrastructure fragment in the policy.

## 4. Deploy

```sh
TOKEN=$(az acr login -n <acrname> --expose-token --query accessToken -o tsv)
az deployment group create -g vat-playground1 \
  -f deploy/aci-template.generated.json \
  -p deploy/aci-template.generated.parameters.json \
  -p location=eastus2 \
  -p registryServer=<acrname>.azurecr.io \
  -p registryUsername=00000000-0000-0000-0000-000000000000 registryPassword="$TOKEN" \
  -p blobStorageConnectionString="$BLOB_STORAGE_CONNECTION_STRING" \
  -p blobStorageContainer=telemetry \
  -p telemetryBufferCapacity=1024 \
  -p telemetryTargetChunkRecords=512 \
  -p telemetryTailMaxDelay=00:00:30

az container show -g vat-playground1 -n telemetee-collector --query 'instanceView.state' -o tsv
az container logs -g vat-playground1 -n telemetee-collector --container-name collector
az container logs -g vat-playground1 -n telemetee-collector --container-name attestation-sidecar
```

Expect a multi-second cold start for the image pull plus the UVM boot. The collector log should
show it listening on port 5000. It does not contact the sidecar until the first attestation
request.

## 5. Verify from outside

```sh
IP=$(az container show -g vat-playground1 -n telemetee-collector --query ipAddress.ip -o tsv)
CLI="cargo run -q -p telemetee-cli -- --base-url http://$IP:5000 --collector-version 0.1.0 \
  --issuer https://sharedeus2.eus2.attest.azure.net --hostdata $(cat deploy/hostdata.txt)"

$CLI attestation
$CLI send --record '{"schema":"fill_assist_rule_outcome","fields":{"rule_id":"r1","success":true,"domain":"a.example","client_version_major":2025}}'
```

`attestation` prints the verified claims. Check these in the output:

- `x-ms-runtime.noise_pub` is present and decodes to a 65-byte value whose first byte is `0x04`
  (SEC1 uncompressed-point tag).
- `x-ms-sevsnpvm-hostdata` equals `deploy/hostdata.txt`. The CLI already rejects a mismatch, so
  reaching the claims output means it matched.
- `x-ms-compliance-status` is `azure-compliant-uvm`.

`send` returning without an error means the collector accepted the page. A new blob should
appear under `<container>/fill_assist_rule_outcome/<guid>.parquet` in the configured storage
account within one `FlushInterval` (plus up to `TailMaxDelay` if the chunk did not fill), with
`client_version_major` as its bucket label.

Negative checks worth running once; both should fail:

```sh
$CLI --hostdata $(printf 'f%.0s' {1..64}) attestation
cargo run -q -p telemetee-cli -- --base-url http://$IP:5000 --collector-version 0.1.0 \
  --issuer https://sharedcus.cus.attest.azure.net --hostdata $(cat deploy/hostdata.txt) attestation
```

## 6. Tear down

The group is shared, so remove only what this run created:

```sh
az container delete -g vat-playground1 -n telemetee-collector --yes
az acr delete -g vat-playground1 -n <acrname> --yes
```

The collector holds no state, so there is nothing to preserve. Keep `deploy/hostdata.txt` with
the notes for the run.

## Secrets and wildcards

`confcom` pins every env var in the template into the policy. A literal value is embedded
verbatim, so a secret written as a literal would be published with the policy. A value that
comes from an ARM parameter with no default becomes a `NAME=.*` rule instead: the policy pins
that the variable exists and nothing about its value.

Two rules follow:

- Secrets are always parameters without defaults, of type `securestring`, surfaced as
  `secureValue` env vars. Never literals.
- A wildcard is an operator-controlled input to attested code. It is acceptable only when its
  value cannot change what the collector does with user data: a credential for an untrusted sink
  qualifies; endpoints, environment names, and behavioural settings do not and stay literal.

`deploy/allowed-wildcards.txt` is the reviewed list of accepted non-literal rules. Milestone 2
added five:

- `BlobStorageSink__ConnectionString=.+` and `BlobStorageSink__Container=.+` — the
  operator chooses the destination storage account and container. The sink is outside the privacy
  trust boundary; neither value can change what the collector does with user data.
- `BlobStorageSink__TargetChunkRecords=.+` and `BlobStorageSink__TailMaxDelay=.+` — sink tuning
  for blob granularity and tail flush latency. Both only affect how already-anonymized output is
  batched into blobs, not what the collector does with user data.
- `TelemetryIngestion__BufferCapacity=.+` — cap on in-memory records between flushes. Changes drop
  rate under load but does not alter the anonymization applied to any retained record.

## Diagnostic lockdown

The ACI template pins the `DOTNET_EnableDiagnostics*` env vars to literal `0` values. The collector
refuses to start if the lockdown did not take. See
[decisions.md § Why .NET diagnostics are off in Production](../docs/decisions.md#why-net-diagnostics-are-off-in-production) for the threat model and `DiagnosticsLockdown` in the
collector for what the startup check covers.

## Auditing a release

Everything the hostdata depends on is in the repository at the release tag, so an outsider can
reproduce it without trusting the published value:

```sh
git checkout v<version>
deploy/build-image.sh telemetee-collector:audit            # prints the image ID
deploy/generate-policy.sh telemetee-collector:audit        # prints the hostdata
```

The hostdata must equal the one in the release artifact and the one pinned in the client. The
release artifact also carries `policy.rego`, so what the hash commits to can be read directly:
anyone can check that its SHA-256 equals the hostdata without any tooling.
