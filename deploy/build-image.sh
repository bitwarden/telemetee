#!/usr/bin/env bash
# Reproducible build of the collector image.
#
# Usage:
#   deploy/build-image.sh <name:tag>                     # local build, prints the image ID
#   deploy/build-image.sh <name:tag> --push <registry>   # pushes <registry>/<name:tag>, prints the manifest digest
#
# <name:tag> is the repository name and tag only, e.g. telemetee-collector:0.1.0. For --push,
# <registry> is the registry host, e.g. myacr.azurecr.io; the script forms <registry>/<name:tag>
# itself so there is no way to push a tag without a registry prefix.
#
# Two builds of the same commit must produce the same image ID; the release workflow checks this.
# Reproducibility rests on: base images pinned by digest in the Dockerfile, deterministic
# `dotnet publish` output, SOURCE_DATE_EPOCH for the image config, and BuildKit rewriting layer
# file timestamps to that epoch. The last needs a docker-container builder; the default docker
# driver silently ignores rewrite-timestamp. --no-cache so a check build really rebuilds.
set -euo pipefail

IMAGE="${1:?image name:tag (e.g. telemetee-collector:0.1.0)}"
PUSH=0
REF="$IMAGE"
if [[ "${2:-}" == "--push" ]]; then
  PUSH=1
  REGISTRY="${3:?--push requires a registry host (e.g. myacr.azurecr.io)}"
  REF="$REGISTRY/$IMAGE"
elif [[ -n "${2:-}" ]]; then
  echo "unknown argument: $2" >&2
  exit 2
fi

EPOCH="${SOURCE_DATE_EPOCH:-$(git log -1 --pretty=%ct 2>/dev/null || echo 0)}"
BUILDER="${BUILDX_BUILDER:-telemetee}"

if ! docker buildx inspect "$BUILDER" >/dev/null 2>&1; then
  docker buildx create --name "$BUILDER" --driver docker-container >/dev/null
fi

if (( PUSH )); then
  output="type=image,push=true,rewrite-timestamp=true"
else
  output="type=docker,rewrite-timestamp=true"
fi

docker buildx build \
  --builder "$BUILDER" \
  --no-cache \
  --provenance=false --sbom=false \
  --build-arg SOURCE_DATE_EPOCH="$EPOCH" \
  --output "$output" \
  -f src/Telemetee.Collector/Dockerfile \
  -t "$REF" \
  . >&2

if (( PUSH )); then
  docker buildx imagetools inspect "$REF" --format '{{.Manifest.Digest}}'
else
  docker image inspect "$REF" --format '{{.Id}}'
fi
