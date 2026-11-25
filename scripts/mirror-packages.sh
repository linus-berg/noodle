#!/usr/bin/env bash
# Downloads packages from registry.npmjs.org and uploads their tarballs to a
# Noodle server (tarballs only: Noodle derives everything else from them).
#
#   scripts/mirror-packages.sh                         # everything examples/ needs
#   scripts/mirror-packages.sh express lodash@4.17.21 @types/node@22.10.0
#   scripts/mirror-packages.sh --lockfile app/package-lock.json --lockfile app/yarn.lock
#   NOODLE_URL=https://npm.corp.example NOODLE_API_KEY=... \
#     CURL_OPTS="--cacert deploy/certs/ca.crt" scripts/mirror-packages.sh chalk
#
# A name without a version mirrors its latest version (dependencies are not
# resolved; use a lockfile for a whole dependency tree). Lockfiles of npm
# (package-lock.json, npm-shrinkwrap.json), Yarn classic and berry
# (yarn.lock), pnpm (pnpm-lock.yaml) and bun (bun.lock) are read; every
# registry package in them is mirrored, also those for other platforms.
#
# Settings (environment variables):
#   NOODLE_URL       server to upload to (default: the development server,
#                    http://localhost:5090)
#   NOODLE_API_KEY   API key (default: dev-api-key, from appsettings.Development.json)
#   SOURCE_URL       where packages come from (default: https://registry.npmjs.org)
#   DOWNLOAD_DIR     download cache (default: $TMPDIR/noodle-packages)
#   PARALLEL         downloads and uploads at a time (default: 4)
#   WITH_ADVISORIES  1 = also import OSV's npm security advisories (npm audit)
#   ADVISORY_URL     where those come from (default: OSV's npm/all.zip)
#   CURL_OPTS        extra curl options for the uploads, e.g. --cacert
#
# Safe to run again: tarballs already on the server are reported as
# "present". Exits non-zero when anything failed or conflicted.
set -euo pipefail

export NOODLE_URL="${NOODLE_URL:-http://localhost:5090}"
export NOODLE_API_KEY="${NOODLE_API_KEY:-dev-api-key}"
export SOURCE_URL="${SOURCE_URL:-https://registry.npmjs.org}"
export DOWNLOAD_DIR="${DOWNLOAD_DIR:-${TMPDIR:-/tmp}/noodle-packages}"
export CURL_OPTS="${CURL_OPTS:-}"
PARALLEL="${PARALLEL:-4}"
WITH_ADVISORIES="${WITH_ADVISORIES:-0}"
ADVISORY_URL="${ADVISORY_URL:-https://osv-vulnerabilities.storage.googleapis.com/npm/all.zip}"
EXAMPLE_LOCKFILE="$(dirname "$0")/../examples/package-lock.json"

# Prints name@version for every registry package in a lockfile.
lockfile_packages() {
  local file="$1"
  case "$(basename "$file")" in
    package-lock.json | npm-shrinkwrap.json)
      grep -oE '"resolved": *"https?://[^"]+\.tgz"' "$file" | sed -E 's/.*"(https?:[^"]+)"/\1/' |
        while read -r url; do url_to_spec "$url"; done
      ;;
    yarn.lock)
      if grep -q '^__metadata:' "$file"; then
        # Yarn berry: resolution: "name@npm:1.2.3"
        grep -oE '^  resolution: "(@[^/"]+/)?[^@"]+@npm:[^"]+"' "$file" |
          sed -E 's/^  resolution: "(.*)@npm:(.*)"$/\1@\2/'
      else
        # Yarn classic: resolved "https://registry.yarnpkg.com/name/-/name-1.2.3.tgz#sha1"
        grep -oE '^  resolved "https?://[^"#]+\.tgz' "$file" | sed -E 's/^  resolved "//' |
          while read -r url; do url_to_spec "$url"; done
      fi
      ;;
    pnpm-lock.yaml)
      # Keys of the packages and snapshots sections: name@1.2.3 or
      # name@1.2.3(peer@x) ('quoted' for scoped names).
      grep -oE "^  '?(@[^/']+/)?[^@' ]+@[0-9][^':( ]*" "$file" | sed -E "s/^  '?//" | sort -u
      ;;
    bun.lock)
      # "name": ["name@1.2.3", "", {...}, "sha512-..."]
      grep -oE '\["(@[^/"]+/)?[^@"]+@[0-9][^"]*", ""' "$file" | sed -E 's/^\["([^"]+)".*/\1/'
      ;;
    *)
      echo "!! unknown lockfile $file (expected package-lock.json, yarn.lock, pnpm-lock.yaml or bun.lock)" >&2
      return 1
      ;;
  esac
}

# https://registry.npmjs.org/@scope/name/-/name-1.2.3.tgz -> @scope/name@1.2.3
url_to_spec() {
  local path name file
  path="${1#*://*/}"
  name="${path%%/-/*}"
  file="${path##*/-/}"
  echo "$name@${file#"${name##*/}"-}" | sed -E 's/\.tgz$//'
}

# Newest version by the source registry's "latest" tag.
latest_version() {
  local escaped="${1/\//%2f}" tags
  tags=$(curl -fsS --compressed "$SOURCE_URL/-/package/$escaped/dist-tags") || return 1
  sed -nE 's/.*"latest" *: *"([^"]+)".*/\1/p' <<< "$tags" | grep .
}

# Downloads and uploads one name@version; prints one status line.
mirror_one() {
  local spec="$1" name version file url response status
  name="${spec%@*}"
  version="${spec##*@}"
  file="$DOWNLOAD_DIR/${name//\//+}-$version.tgz"
  url="$SOURCE_URL/$name/-/${name##*/}-$version.tgz"
  if [[ ! -s "$file" ]]; then
    if ! curl -fsS -o "$file.part.$$" "$url"; then
      rm -f "$file.part.$$"
      echo "failed   $spec (download $url)"
      return
    fi
    mv "$file.part.$$" "$file"
  fi

  response=$(mktemp)
  # shellcheck disable=SC2086
  status=$(curl -sS $CURL_OPTS -o "$response" -w '%{http_code}' -X PUT \
    -H "Authorization: Bearer $NOODLE_API_KEY" -H "Content-Type: application/octet-stream" \
    --data-binary "@$file" "$NOODLE_URL/-/noodle/upload") || status=000
  case "$status" in
    201) echo "added    $spec" ;;
    200) echo "present  $spec" ;;
    409) echo "conflict $spec: the server has a different tarball for this version" ;;
    *) echo "failed   $spec (HTTP $status: $(head -c 300 "$response"))" ;;
  esac
  rm -f "$response"
}
export -f mirror_one

mirror_advisories() {
  local file="$DOWNLOAD_DIR/osv-npm-all.zip" response status
  echo "Downloading advisories from $ADVISORY_URL"
  curl -fsS -o "$file.part" "$ADVISORY_URL"
  mv "$file.part" "$file"
  response=$(mktemp)
  # shellcheck disable=SC2086
  status=$(curl -sS $CURL_OPTS -o "$response" -w '%{http_code}' -X PUT \
    -H "Authorization: Bearer $NOODLE_API_KEY" -H "Content-Type: application/zip" \
    --data-binary "@$file" "$NOODLE_URL/-/noodle/advisories?source=$(basename "$ADVISORY_URL")") || status=000
  if [[ "$status" == 200 ]]; then
    echo "   imported advisories: $(cat "$response")"
    rm -f "$response"
  else
    echo "!! advisory import failed (HTTP $status): $(head -c 300 "$response")" >&2
    rm -f "$response"
    return 1
  fi
}

specs=()
lockfiles=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --lockfile) lockfiles+=("$2"); shift 2 ;;
    -h | --help) sed -n '2,32p' "$0"; exit 0 ;;
    *) specs+=("$1"); shift ;;
  esac
done
if [[ ${#specs[@]} -eq 0 && ${#lockfiles[@]} -eq 0 ]]; then
  lockfiles=("$EXAMPLE_LOCKFILE")
fi

mkdir -p "$DOWNLOAD_DIR"
list=$(mktemp)
results=$(mktemp)
trap 'rm -f "$list" "$results"' EXIT
for lockfile in ${lockfiles[@]+"${lockfiles[@]}"}; do
  lockfile_packages "$lockfile" >> "$list"
done
for spec in ${specs[@]+"${specs[@]}"}; do
  # A leading @ belongs to the scope, not to a version.
  if [[ "${spec:1}" == *@* ]]; then
    echo "$spec" >> "$list"
  elif version=$(latest_version "$spec"); then
    echo "$spec@$version" >> "$list"
  else
    echo "failed   $spec (not on $SOURCE_URL)" >> "$results"
  fi
done

sort -u -o "$list" "$list"
echo "Mirroring $(wc -l < "$list") package version(s) from $SOURCE_URL to $NOODLE_URL"
xargs -r -P "$PARALLEL" -I{} bash -c 'mirror_one "$1"' _ {} < "$list" | tee -a "$results" |
  grep -vE '^(present|added) ' || true

if [[ "$WITH_ADVISORIES" == "1" ]]; then
  mirror_advisories || echo "failed   advisories" >> "$results"
fi

added=$(grep -c '^added ' "$results" || true)
present=$(grep -c '^present ' "$results" || true)
failed=$(grep -cE '^(failed|conflict) ' "$results" || true)
echo "Done: $added added, $present already present, $failed failed"
[[ $failed -eq 0 ]]
