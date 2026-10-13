# Deploying the collector to Confidential ACI

Steps for the milestone 1 experiments: confirm the attested Noise channel works on real SEV-SNP
hardware, and answer the open questions about the CCE policy. Everything runs from the direnv shell,
which provides `az`, `dotnet`, and `cargo`; `docker` must be running since `confcom` hashes image
layers through the local daemon.

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
infrastructure fragment in the policy, since the UVM mounts layers it covers but the hand-rolled
container list does not. Set `EXCLUDE_DEFAULT_FRAGMENTS=1` only to re-run the experiment in step 6.

## 4. Deploy

```sh
TOKEN=$(az acr login -n <acrname> --expose-token --query accessToken -o tsv)
az deployment group create -g vat-playground1 \
  -f deploy/aci-template.generated.json \
  -p deploy/aci-template.generated.parameters.json \
  -p location=eastus2 \
  -p registryServer=<acrname>.azurecr.io \
  -p registryUsername=00000000-0000-0000-0000-000000000000 registryPassword="$TOKEN"

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
$CLI echo "hello"
```

`attestation` prints the verified claims. Check in the output:

- `x-ms-runtime.noise_pub` is present and decodes to a 65-byte value whose first byte is `0x04`
  (SEC1 uncompressed-point tag). In base64 that first byte spans the first two characters, so the
  prefix is `BA`–`BP` depending on the next byte of the key; the invariant is the decoded first
  byte, not the base64 prefix.
- `x-ms-sevsnpvm-hostdata` equals `deploy/hostdata.txt`. The CLI already rejects a mismatch, so
  reaching the claims output means it matched.
- `x-ms-compliance-status` is `azure-compliant-uvm`.

`echo` returning `hello` completes the milestone: the token verified, the key came from it, and the
handshake succeeded against the container.

Negative checks worth running once, both should fail:

```sh
$CLI --hostdata $(printf 'f%.0s' {1..64}) attestation
cargo run -q -p telemetee-cli -- --base-url http://$IP:5000 --collector-version 0.1.0 \
  --issuer https://sharedcus.cus.attest.azure.net --hostdata $(cat deploy/hostdata.txt) attestation
```

## 6. Experiments

Record the outcome of each in `claude-research/anon-telemetry/milestone-1.md`.

**Default infrastructure fragment.** Resolved: the fragment is required. With
`EXCLUDE_DEFAULT_FRAGMENTS=1`, the first deploy failed with `deviceHash not found` for a layer the
hand-rolled container list did not cover (it belongs to an infra component the fragment measures).
Redeploying with the fragment included succeeded, so `EXCLUDE_DEFAULT_FRAGMENTS=0` is now the
default in `generate-policy.sh`. The TCB consequence is captured in the design brief: part of it is
delegated to whatever Microsoft signs under the `aci-cc-infra-fragment` issuer at or above
`minimum_svn`.

**Shared provider needs no bearer token.** Implicit in step 5 succeeding: the sidecar sends none.

**`confcom` stability.** Resolved: a second run of `generate-policy.sh` on the unchanged image with
the same flags produced identical hostdata.

**Token lifetime.** Resolved: `exp - iat` on this deployment is 8 hours, matching the Microsoft
sample. The collector's refresh margin can safely assume hours.

**Direct SNP report without the sidecar.** Not exercised by this deployment. It needs a build of the
collector that opens `/dev/sev-guest` itself; the research notes (`attestation-findings.md`, "Direct
from the workload") describe what is known before attempting it.

## 7. Tear down

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

`deploy/allowed-wildcards.txt` is the reviewed list of accepted non-literal rules. It starts with
the variables the ACI runtime injects, which confcom allows by default and the collector never
reads. Adding an entry is a visible diff and needs a reason.

## Auditing a release

Everything the hostdata depends on is in the repository at the release tag, so an outsider can
reproduce it without trusting the published value:

```sh
git checkout v<version>
deploy/build-image.sh telemetee-collector:audit            # prints the image ID
deploy/generate-policy.sh telemetee-collector:audit        # prints the hostdata
```

The hostdata must equal the one in the release artifact and the one pinned in the client. The
inputs, for reference: the collector image layers (reproducible from source), the sidecar image by
digest, the checked-in template, and confcom at the pinned version. Image reproducibility rests on
`dmverity-vhd` for layer hashing, a Microsoft binary that confcom pins by SHA-256 and that is open
source in hcsshim.

The release artifact also carries `policy.rego`, so what the hash commits to can be read directly:
anyone can check that its SHA-256 equals the hostdata without any tooling.

## Known unknowns

Verified locally: the build script produces identical image IDs on repeated runs, the policy script
produces identical hostdata from them, the `if()` expression on `imageRegistryCredentials` survives
confcom's rewrite, and the wildcard check fails closed. Not yet exercised: `confcom` hashing layers
pulled from a private ACR after `az acr login`. It uses the local docker daemon, so it should.
