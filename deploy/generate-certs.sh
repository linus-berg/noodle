#!/usr/bin/env bash
# Creates an internal CA and a TLS certificate that lets Noodle answer for
# registry.npmjs.org and registry.yarnpkg.com (and any extra names you pass).
#
#   ./generate-certs.sh                         # the two registry names only
#   ./generate-certs.sh npm.corp.example 10.1.2.3
#
# Output (in ./certs, or $CERT_DIR):
#   ca.crt        distribute to every developer and build machine (see README)
#   ca.key        keep offline / secret; only needed to issue new certificates
#   noodle.crt    server certificate (with the CA appended, full chain)
#   noodle.key    server private key
#
# If your organisation already runs a CA, issue a certificate for the same
# names from it instead and skip this script.
set -euo pipefail

CERT_DIR="${CERT_DIR:-$(dirname "$0")/certs}"
DAYS="${DAYS:-825}"
mkdir -p "$CERT_DIR"
cd "$CERT_DIR"

names=(registry.npmjs.org registry.yarnpkg.com "$@")
san=""
for name in "${names[@]}"; do
  if [[ "$name" =~ ^[0-9.]+$ || "$name" == *:* ]]; then
    san+="IP:${name},"
  else
    san+="DNS:${name},"
  fi
done
san="${san%,}"

if [[ ! -f ca.key ]]; then
  echo "Creating CA (ca.crt / ca.key)"
  openssl req -x509 -newkey rsa:4096 -sha256 -days 3650 -nodes \
    -keyout ca.key -out ca.crt \
    -subj "/CN=Noodle Internal CA" \
    -addext "basicConstraints=critical,CA:TRUE" \
    -addext "keyUsage=critical,keyCertSign,cRLSign" >/dev/null 2>&1
  chmod 600 ca.key
else
  echo "Reusing existing CA (ca.crt / ca.key)"
fi

echo "Issuing server certificate for: ${names[*]}"
openssl req -newkey rsa:2048 -sha256 -nodes \
  -keyout noodle.key -out noodle.csr \
  -subj "/CN=${names[0]}" >/dev/null 2>&1
cat > noodle.ext <<EXT
basicConstraints=CA:FALSE
keyUsage=critical,digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth
subjectAltName=${san}
EXT
# 825 days is the maximum validity Apple platforms accept for TLS certificates.
openssl x509 -req -in noodle.csr -CA ca.crt -CAkey ca.key -CAcreateserial \
  -days "$DAYS" -sha256 -extfile noodle.ext -out noodle.leaf.crt >/dev/null 2>&1
cat noodle.leaf.crt ca.crt > noodle.crt
rm -f noodle.csr noodle.ext noodle.leaf.crt ca.srl
# The container runs as an unprivileged user.
chmod 644 noodle.crt noodle.key

echo "Done. Next: trust ca.crt on clients and point the registry names at this host."
