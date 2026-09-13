#!/usr/bin/env bash
set -euo pipefail

mode="push"
if [[ "${1:-}" == "--check" ]]; then
  mode="check"
  shift
fi
kind="${1:-patch}"
remote="${SUSHI_RELEASE_REMOTE:-origin}"

case "$kind" in
  major|minor|patch) ;;
  [0-9]*.[0-9]*.[0-9]*) ;;
  *)
    echo "Usage: scripts/release.sh [major|minor|patch|X.Y.Z]" >&2
    exit 2
    ;;
esac

if [[ -n "$(git status --short)" ]]; then
  echo "Release requires a clean working tree." >&2
  exit 1
fi

latest="$(git tag --list 'v[0-9]*.[0-9]*.[0-9]*' --sort=-version:refname | head -n 1)"
latest="${latest#v}"
if [[ -z "$latest" ]]; then latest="0.0.0"; fi

if [[ "$kind" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  version="$kind"
else
  IFS=. read -r major minor patch <<<"$latest"
  case "$kind" in
    major) version="$((major + 1)).0.0" ;;
    minor) version="$major.$((minor + 1)).0" ;;
    patch) version="$major.$minor.$((patch + 1))" ;;
  esac
fi

tag="v$version"
if git rev-parse --verify --quiet "refs/tags/$tag" >/dev/null; then
  echo "Tag $tag already exists." >&2
  exit 1
fi

just ready
if [[ "$mode" == "check" ]]; then
  echo "Release check passed for $tag."
  exit 0
fi
git tag -a "$tag" -m "Release $tag"
git push "$remote" --follow-tags
echo "Released $tag via $remote."
