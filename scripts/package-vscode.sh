#!/usr/bin/env bash
set -euo pipefail

version="${1:?version required}"
output_dir="${2:-publish/editors}"
root_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
extension_dir="$root_dir/editors/vscode"
mkdir -p "$root_dir/$output_dir"

pushd "$extension_dir" >/dev/null
backup_dir="$(mktemp -d)"
cp package.json package-lock.json "$backup_dir/"
cleanup() {
  cp "$backup_dir/package.json" package.json
  cp "$backup_dir/package-lock.json" package-lock.json
  rm -rf "$backup_dir"
  rm -f "${vsix:-sushi-language-$version.vsix}"
  popd >/dev/null
}
trap cleanup EXIT

npm version "$version" --no-git-tag-version --allow-same-version >/dev/null
npm run compile
./node_modules/.bin/vsce package --allow-missing-repository --no-dependencies
vsix="sushi-language-$version.vsix"
tmp_dir="$(mktemp -d)"
mkdir -p "$tmp_dir/extension/node_modules"
cp -R node_modules/vscode-languageclient node_modules/vscode-jsonrpc \
  node_modules/vscode-languageserver-protocol node_modules/vscode-languageserver-types \
  node_modules/semver node_modules/minimatch node_modules/brace-expansion \
  node_modules/balanced-match "$tmp_dir/extension/node_modules/"
(cd "$tmp_dir" && zip -q -r "$extension_dir/$vsix" extension/node_modules)
rm -rf "$tmp_dir"
cp "$extension_dir/$vsix" "$root_dir/$output_dir/$vsix"
