# Deploying the collector to Confidential ACI

Steps for a milestone 2 deployment: the attested Noise channel from milestone 1 plus the batched
anonymized sink to Datadog. Everything runs from the direnv shell, which provides `az`, `dotnet`,
and `cargo`; `docker` must be running since `confcom` hashes image layers through the local daemon.

Region is East US 2, which has the Confidential ACI SKU and hosts the `sharedeus2` attestation
provider the template defaults to. Resources go in the existing `vat-playground1` group in
subscription `33f1558f-095b-4d93-a879-372001c8c505`. The group's own region does not matter; every
command below passes `eastus2` explicitly.

## 0. One-time prerequisites

Resource providers register at subscription scope, and Contributor on the resource group cannot do
it. A subscription Owner has to run this once:

```sh
az provider register -n Microsoft.ContainerInstance
az provider register -n Microsoft.ContainerRegistry
```

Check with `az provider show -n Microsoft.ContainerInstance --query registrationState`; it must say
`Registered` before step 1.

You also need a Datadog API key for the destination account. Pass it as the `datadogApiKey`
parameter in step 4; it is a `securestring` with no default so it never enters the policy as a
literal.

## 1. Subscription and a private registry

ACI can only pull from a registry. A Basic ACR in the playground group keeps the image inside the
subscription; step 4 authenticates ACI with a short-lived AD token minted from your current `az`
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

`build-image.sh` is the reproducible build: digest-pinned base images, `SOURCE_DATE_EPOCH` from the
commit, and layer timestamps rewritten through a `docker-container` BuildKit builder it creates on
first use. The digest reference is what goes into the policy and the deployment, never the tag.

## 3. Generate the CCE policy

```sh
DISABLE_STDIO=0 deploy/generate-policy.sh "$IMAGE"
```

This pins `confcom`, pulls both images to hash their layers, injects the policy into
`deploy/aci-template.generated.json`, writes the decoded policy to `deploy/policy.rego`, and writes
the hostdata to `deploy/hostdata.txt`. It then fails if the policy contains an env rule that is not
pinned to a literal value and not listed in `deploy/allowed-wildcards.txt`; see
[Secrets and wildcards](#secrets-and-wildcards).

`DISABLE_STDIO=0` is for experiments only: with stdio disabled, `az container logs` shows nothing.
`EXCLUDE_DEFAULT_FRAGMENTS=0` is the default; the Confidential ACI SKU requires Microsoft's signed
infrastructure fragment in the policy.

## 4. Deploy

```sh
TOKEN=$(az acr login -n <acrname> --expose-token --query accessToken -o tsv)
az deployment group create -g vat-playground1 \
  -f deploy/aci-template.generated.json \
  -p deploy/aci-template.generated.parameters.json \
  -p location=eastus2 \
  -p registryServer=<acrname>.azurecr.io \
  -p registryUsername=00000000-0000-0000-0000-000000000000 registryPassword="$TOKEN" \
  -p datadogApiKey="$DATADOG_API_KEY"

az container show -g vat-playground1 -n telemetee-collector --query 'instanceView.state' -o tsv
az container logs -g vat-playground1 -n telemetee-collector --container-name collector
az container logs -g vat-playground1 -n telemetee-collector --container-name attestation-sidecar
```

Expect multi-second cold start for image pull plus UVM boot. The collector log should show it
listening on port 5000; it does not contact the sidecar until the first attestation request.

## 5. Verify from outside

```sh
IP=$(az container show -g vat-playground1 -n telemetee-collector --query ipAddress.ip -o tsv)
CLI="cargo run -q -p telemetee-cli -- --base-url http://$IP:5000 --collector-version 0.1.0 \
  --issuer https://sharedeus2.eus2.attest.azure.net --hostdata $(cat deploy/hostdata.txt)"

$CLI attestation
$CLI send --record '{"schema":"fill_assist_rule_outcome","fields":{"rule_id":"r1","success":true,"domain":"a.example","client_version_major":2025}}'
```

`attestation` prints the verified claims. Check in the output:

- `x-ms-runtime.noise_pub` is present and decodes to a 65-byte value whose first byte is `0x04`
  (SEC1 uncompressed-point tag).
- `x-ms-sevsnpvm-hostdata` equals `deploy/hostdata.txt`. The CLI already rejects a mismatch, so
  reaching the claims output means it matched.
- `x-ms-compliance-status` is `azure-compliant-uvm`.

`send` returning without an error means the collector accepted the page. Within one flush
interval (default 30 s) the record should appear in Datadog under
`service:telemetee-collector`, with `client_version_major` as its bucket label.

Negative checks worth running once, both should fail:

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

The collector holds no state, so there is nothing to preserve. Keep `deploy/hostdata.txt` with the
notes for the run.

## Secrets and wildcards

confcom pins every env var in the template into the policy. A literal value is embedded verbatim,
so a secret written as a literal would be published with the policy. A value that comes from an ARM
parameter with no default becomes a `NAME=.*` rule instead: the policy pins that the variable
exists and nothing about its value.

Two rules follow:

- Secrets are always parameters without defaults, of type `securestring`, surfaced as `secureValue`
  env vars. Never literals.
- A wildcard is an operator-controlled input to attested code. It is acceptable only when its value
  cannot change what the collector does with user data: a credential for an untrusted sink
  qualifies; endpoints, environment names, and behavioural settings do not and stay literal.

`deploy/allowed-wildcards.txt` is the reviewed list of accepted non-literal rules. Milestone 2
added `Telemetry__DatadogApiKey=.+`: the Datadog site is a literal and pinned, so the operator can
choose only *where* in Datadog the anonymized output lands, not what the collector does to records.

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
