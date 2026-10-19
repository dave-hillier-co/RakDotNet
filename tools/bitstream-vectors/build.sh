#!/usr/bin/env bash
# Builds and runs bitstream-vectors against RakNet's sources and the static library tools/interop-peer builds.
# RAKNET_SOURCE_DIR: a RakNet checkout at commit 1a169895 (its Source folder is used for headers).
# RAKNET_LIB: libraknet_static.a; defaults to the one tools/interop-peer/build.sh produces.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
: "${RAKNET_SOURCE_DIR:?Set RAKNET_SOURCE_DIR to a RakNet checkout}"
lib="${RAKNET_LIB:-${here}/../interop-peer/build/libraknet_static.a}"
out="${here}/build"
mkdir -p "${out}"

# The same flags tools/interop-peer compiles RakNet with (docs/plan.md, interop test harness).
clang++ -std=gnu++98 -fno-strict-float-cast-overflow -w -I"${RAKNET_SOURCE_DIR}/Source" \
  "${here}/bitstream-vectors.cpp" "${lib}" -o "${out}/bitstream-vectors"
"${out}/bitstream-vectors"
