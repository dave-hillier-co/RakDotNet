#!/usr/bin/env bash
# Builds tools/cat-vectors and writes the cat known-answer vectors into tests/vectors/cat.
# Set RAKNET_SOURCE_DIR to a local RakNet checkout to build without fetching it.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
build_dir="${here}/build"
out_dir="${here}/../../tests/vectors/cat"

configure_args=(-S "${here}" -B "${build_dir}" -DCMAKE_BUILD_TYPE=Release)
if [[ -n "${RAKNET_SOURCE_DIR:-}" ]]; then
  configure_args+=("-DFETCHCONTENT_SOURCE_DIR_RAKNET=${RAKNET_SOURCE_DIR}")
fi

cmake "${configure_args[@]}"
cmake --build "${build_dir}" --config Release --parallel
mkdir -p "${out_dir}"
"${build_dir}/cat-vectors" "${out_dir}"
