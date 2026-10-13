#!/usr/bin/env bash
# Generates the CCE policy for deploy/aci-template.json and prints the hostdata clients pin.
#
# Usage: deploy/generate-policy.sh <collector image by digest> [output template path]
#
# Outputs, next to the template:
#   deploy/aci-template.generated.json  template with the policy injected
#   deploy/policy.rego                  the decoded policy, published so auditors can read what the
#                                       hostdata commits to without regenerating it
#   deploy/hostdata.txt                 SHA-256 of the policy, the value clients pin
#
# The confcom version is pinned because the policy hash is a function of the tool as well as the
# inputs. Bump CONFCOM_VERSION deliberately and expect a new hostdata when you do. The dev flake
# supplies confcom through nixpkgs; elsewhere the script installs it as an az extension.
#
# Knobs for experiments (each changes the hostdata):
#   EXCLUDE_DEFAULT_FRAGMENTS=0  Keep Microsoft's signed infrastructure fragment in the policy.
#                                Required on the Confidential ACI SKU; excluding it leaves infra
#                                layers unmeasured and the UVM denies them at mount with
#                                "deviceHash not found". Set to 1 only to re-run that experiment.
#   DISABLE_STDIO=1              Forbid stdout/stderr access. Set to 0 while debugging a deployment,
#                                otherwise `az container logs` shows nothing.
set -euo pipefail

CONFCOM_VERSION="${CONFCOM_VERSION:-2.3.0}"
IMAGE="${1:?collector image reference by digest}"
OUTPUT="${2:-deploy/aci-template.generated.json}"
EXCLUDE_DEFAULT_FRAGMENTS="${EXCLUDE_DEFAULT_FRAGMENTS:-0}"
DISABLE_STDIO="${DISABLE_STDIO:-1}"

installed=$(az version --query "extensions.confcom" -o tsv 2>/dev/null || true)
if [[ "$installed" != "$CONFCOM_VERSION" ]]; then
  az extension add --name confcom --version "$CONFCOM_VERSION" --upgrade --yes >/dev/null
  installed=$(az version --query "extensions.confcom" -o tsv)
fi
echo "confcom $installed"

cp deploy/aci-template.json "$OUTPUT"
PARAMETERS="${OUTPUT%.json}.parameters.json"
cat > "$PARAMETERS" <<EOF
{
  "\$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#",
  "contentVersion": "1.0.0.0",
  "parameters": {
    "collectorImage": { "value": "$IMAGE" }
  }
}
EOF

# --omit-id keeps the registry name out of the policy, so a dev ACR and the release registry yield
# the same hostdata for the same layers. Wildcard prompts are left enabled on purpose: a new
# wildcard must be a deliberate change, and the allowlist check below catches it either way.
args=(acipolicygen -a "$OUTPUT" -p "$PARAMETERS" --omit-id)
[[ "$EXCLUDE_DEFAULT_FRAGMENTS" == "1" ]] && args+=(--exclude-default-fragments)
# Always pass one of the explicit stdio flags; omitting both makes confcom warn that it is using
# the default, and the default may change in a future release.
if [[ "$DISABLE_STDIO" == "1" ]]; then
  args+=(--disable-stdio)
else
  args+=(--enable-stdio)
fi

# acipolicygen injects the policy into the template and prints the SHA-256 of the decoded policy.
output=$(az confcom "${args[@]}" < /dev/null | tee /dev/stderr)
hostdata=$(grep -oE '\b[0-9a-f]{64}\b' <<< "$output" | tail -1)
[[ -n "$hostdata" ]] || { echo "no policy hash found in confcom output" >&2; exit 1; }

# Decode the policy, confirm the printed hash is its SHA-256, and reject any non-literal env rule
# that is not in the allowlist.
python3 - "$OUTPUT" "$hostdata" deploy/allowed-wildcards.txt deploy/policy.rego <<'EOF'
import base64, hashlib, json, re, sys

template, hostdata, allowlist, rego_out = sys.argv[1:]
with open(template) as f:
    policy = json.load(f)["resources"][0]["properties"]["confidentialComputeProperties"]["ccePolicy"]
rego = base64.b64decode(policy)
if hashlib.sha256(rego).hexdigest() != hostdata:
    sys.exit("printed hash does not match SHA-256 of the injected policy")
with open(rego_out, "wb") as f:
    f.write(rego)

with open(allowlist) as f:
    allowed = {line.strip() for line in f if line.strip() and not line.startswith("#")}
containers = json.loads(re.search(r"^containers := (\[.*\])$", rego.decode(), re.M).group(1))
unlisted = sorted({
    f'{container["name"]}: {rule["pattern"]}'
    for container in containers
    for rule in container["env_rules"]
    if rule["strategy"] != "string" and rule["pattern"] not in allowed
})
if unlisted:
    print("env rules not pinned to a literal and not in deploy/allowed-wildcards.txt:", file=sys.stderr)
    for entry in unlisted:
        print(f"  {entry}", file=sys.stderr)
    sys.exit(1)
EOF

echo "$hostdata" > deploy/hostdata.txt
echo "hostdata: $hostdata"
