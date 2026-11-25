#!/usr/bin/env python3
"""Copies every npm tarball from an S3 bucket into Noodle (one-time migration).

Walks all keys below a prefix, for example

    npm/registry.npmjs.org/react/react-18.3.1.tgz
    npm/registry.npmjs.org/@types/node/node-22.10.0.tgz

and uploads each .tgz to Noodle, streaming it straight from the GET to the
upload (nothing is written to disk). Noodle reads name and version from the
tarball itself, so the key layout below the prefix does not matter.

    export AWS_ACCESS_KEY_ID=... AWS_SECRET_ACCESS_KEY=... NOODLE_API_KEY=...
    scripts/migrate-from-s3.py \\
        --source-url https://minio.corp.example:9000 --source-bucket packages \\
        --source-prefix npm/registry.npmjs.org/ \\
        --noodle-url https://npm.corp.example --parallel 64

Resumable: progress is kept in an SQLite file (--state). Stop it at any time
(Ctrl-C finishes the uploads in flight, a second Ctrl-C quits at once) or let
it crash, and run the same command again: it continues where it stopped,
without listing the bucket again. Uploads are idempotent, so the few that
were in flight are simply repeated.

Tarballs added to the bucket after it was listed completely are picked up
with --relist (lists everything again, copies only keys it has not seen).

Fast: the bucket is listed while uploading, many uploads run in parallel over
kept-alive connections, and several Noodle instances can be given (repeat
--noodle-url) to spread the load without a load balancer in between. One
copy of the script handles several hundred small tarballs a second; to go
beyond that, run N copies side by side with --shard 0/N ... --shard N-1/N and
a --state file each (every copy lists the whole bucket, which is cheap, and
takes its share of the keys).

Every upload is checked: the SHA-512 of the bytes read from S3 must match the
integrity Noodle reports. Tarballs Noodle rejects (damaged, no package.json)
or that conflict with a different tarball of the same version are recorded
and listed with --failures; transient errors are retried, and what still
failed is retried on the next run.

Needs Python 3.9+ and nothing outside the standard library. S3 is addressed
path style (MinIO's default); TLS certificates are checked against the system
trust store unless --source-ca-file / --noodle-ca-file is given.

Settings can also come from the environment: SOURCE_S3_URL, SOURCE_S3_BUCKET,
SOURCE_S3_PREFIX, SOURCE_S3_REGION, AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY,
AWS_SESSION_TOKEN, NOODLE_URL (comma separated), NOODLE_API_KEY.

Exit codes: 0 every tarball is in Noodle, 1 some were rejected, conflict or
failed (see --failures) or the run was interrupted, 2 it could not run at all
(settings, credentials, connection).
"""

from __future__ import annotations

import argparse
import base64
import datetime
import hashlib
import hmac
import http.client
import json
import os
import queue
import random
import signal
import sqlite3
import ssl
import sys
import threading
import time
import urllib.parse
import xml.etree.ElementTree as ElementTree
import zlib
from dataclasses import dataclass
from typing import Dict, List, Optional, Tuple

USER_AGENT = "noodle-s3-migration/1.0"
EMPTY_SHA256 = hashlib.sha256(b"").hexdigest()
S3_NS = "{http://s3.amazonaws.com/doc/2006-03-01/}"
BLOCK = 1024 * 1024
RETRYABLE_HTTP = {408, 425, 429, 500, 502, 503, 504}
# Statuses that need no more work.
DONE = ("added", "present", "conflict", "rejected", "missing")
SCHEMA = """
CREATE TABLE IF NOT EXISTS objects (
    key TEXT PRIMARY KEY,
    size INTEGER NOT NULL,
    status TEXT NOT NULL DEFAULT 'pending',
    attempts INTEGER NOT NULL DEFAULT 0,
    http_status INTEGER,
    name TEXT,
    version TEXT,
    message TEXT,
    updated REAL
) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS objects_todo ON objects (key)
    WHERE status IN ('pending', 'failed');
CREATE TABLE IF NOT EXISTS meta (
    name TEXT PRIMARY KEY,
    value TEXT
) WITHOUT ROWID;
"""


class Transient(Exception):
    """Worth trying again (network trouble, 5xx, timeouts)."""


class Fatal(Exception):
    """The migration cannot continue (credentials, API key, settings)."""


@dataclass(frozen=True)
class Item:
    key: str
    size: int


@dataclass
class Result:
    key: str
    status: str
    attempts: int
    http_status: Optional[int] = None
    name: Optional[str] = None
    version: Optional[str] = None
    message: Optional[str] = None
    size: int = 0


class Endpoint:
    """Where to connect: scheme, host and TLS settings of a base URL."""

    def __init__(self, url: str, ca_file: Optional[str], timeout: float):
        parts = urllib.parse.urlsplit(url.rstrip("/"))
        if parts.scheme not in ("http", "https") or not parts.hostname:
            raise Fatal(f"not an http(s) URL: {url}")
        self.url = url.rstrip("/")
        self.https = parts.scheme == "https"
        self.netloc = parts.netloc
        self.hostname = parts.hostname
        self.port = parts.port or (443 if self.https else 80)
        self.base_path = parts.path.rstrip("/")
        self.timeout = timeout
        self.context = ssl.create_default_context(cafile=ca_file) if self.https else None

    def connect(self) -> http.client.HTTPConnection:
        if self.https:
            return http.client.HTTPSConnection(
                self.hostname, self.port, timeout=self.timeout,
                context=self.context, blocksize=BLOCK)
        return http.client.HTTPConnection(
            self.hostname, self.port, timeout=self.timeout, blocksize=BLOCK)


class S3:
    """Just enough S3 (ListObjectsV2, GetObject) with Signature Version 4."""

    def __init__(self, endpoint: Endpoint, bucket: str, region: str,
                 access_key: str, secret_key: str, session_token: Optional[str]):
        self.endpoint = endpoint
        self.bucket = bucket
        self.region = region
        self.access_key = access_key
        self.secret_key = secret_key
        self.session_token = session_token

    def object_path(self, key: str) -> str:
        return f"{self.endpoint.base_path}/{self.bucket}/" + urllib.parse.quote(key, safe="/~")

    def signed_headers(self, method: str, path: str, query: str) -> Dict[str, str]:
        now = datetime.datetime.now(datetime.timezone.utc)
        amz_date = now.strftime("%Y%m%dT%H%M%SZ")
        day = now.strftime("%Y%m%d")
        headers = {
            "host": self.endpoint.netloc,
            "x-amz-content-sha256": EMPTY_SHA256,
            "x-amz-date": amz_date,
        }
        if self.session_token:
            headers["x-amz-security-token"] = self.session_token
        names = sorted(headers)
        canonical = "\n".join([
            method, path, query,
            "".join(f"{name}:{headers[name]}\n" for name in names),
            ";".join(names), EMPTY_SHA256])
        scope = f"{day}/{self.region}/s3/aws4_request"
        to_sign = "\n".join([
            "AWS4-HMAC-SHA256", amz_date, scope,
            hashlib.sha256(canonical.encode()).hexdigest()])
        signing_key = ("AWS4" + self.secret_key).encode()
        for part in (day, self.region, "s3", "aws4_request"):
            signing_key = hmac.new(signing_key, part.encode(), hashlib.sha256).digest()
        signature = hmac.new(signing_key, to_sign.encode(), hashlib.sha256).hexdigest()
        headers["authorization"] = (
            f"AWS4-HMAC-SHA256 Credential={self.access_key}/{scope}, "
            f"SignedHeaders={';'.join(names)}, Signature={signature}")
        headers["user-agent"] = USER_AGENT
        return headers

    def list_page(self, connection: http.client.HTTPConnection, prefix: str,
                  start_after: Optional[str], token: Optional[str]
                  ) -> Tuple[List[Item], List[str], Optional[str]]:
        """One page of keys: (objects, all keys, next continuation token)."""
        params = {"list-type": "2", "prefix": prefix, "max-keys": "1000",
                  "encoding-type": "url"}
        if token:
            params["continuation-token"] = token
        elif start_after:
            params["start-after"] = start_after
        query = "&".join(
            f"{urllib.parse.quote(k, safe='-_.~')}={urllib.parse.quote(v, safe='-_.~')}"
            for k, v in sorted(params.items()))
        path = f"{self.endpoint.base_path}/{self.bucket}"
        connection.request("GET", f"{path}?{query}",
                           headers=self.signed_headers("GET", path, query))
        response = connection.getresponse()
        body = response.read()
        if response.status != 200:
            raise classify(response.status, f"listing the bucket: {s3_error(body)}")
        root = ElementTree.fromstring(body)
        items, keys = [], []
        for content in root.iter(f"{S3_NS}Contents"):
            key = urllib.parse.unquote_plus(content.findtext(f"{S3_NS}Key", ""))
            keys.append(key)
            items.append(Item(key, int(content.findtext(f"{S3_NS}Size", "0"))))
        truncated = root.findtext(f"{S3_NS}IsTruncated", "false") == "true"
        next_token = root.findtext(f"{S3_NS}NextContinuationToken") if truncated else None
        if truncated and not next_token:
            raise Transient("listing was truncated without a continuation token")
        return items, keys, next_token

    def open(self, connection: http.client.HTTPConnection, key: str
             ) -> Optional[http.client.HTTPResponse]:
        """Starts reading an object; None when it no longer exists."""
        path = self.object_path(key)
        connection.request("GET", path, headers=self.signed_headers("GET", path, ""))
        response = connection.getresponse()
        if response.status == 404:
            response.read()
            return None
        if response.status != 200:
            raise classify(response.status, f"reading {key}: {s3_error(response.read(4096))}")
        return response


def s3_error(body: bytes) -> str:
    try:
        root = ElementTree.fromstring(body)
        return f"{root.findtext('Code')}: {root.findtext('Message')}"
    except ElementTree.ParseError:
        return body[:200].decode("utf-8", "replace")


def classify(status: int, message: str) -> Exception:
    if status in (401, 403):
        return Fatal(f"HTTP {status} {message}")
    if status in RETRYABLE_HTTP or status >= 500:
        return Transient(f"HTTP {status} {message}")
    return Fatal(f"HTTP {status} {message}")


class HashingReader:
    """Reads a response body in blocks for the upload, hashing it on the way."""

    def __init__(self, response: http.client.HTTPResponse):
        self.response = response
        self.sha512 = hashlib.sha512()
        self.count = 0

    def read(self, size: int = BLOCK) -> bytes:
        data = self.response.read(size)
        self.sha512.update(data)
        self.count += len(data)
        return data


class State:
    """The SQLite file that makes the migration resumable."""

    def __init__(self, path: str):
        self.path = path
        self.db = self.connect()
        self.db.executescript(SCHEMA)

    def connect(self) -> sqlite3.Connection:
        db = sqlite3.connect(self.path, timeout=120, isolation_level=None,
                             check_same_thread=False)
        db.execute("PRAGMA journal_mode=WAL")
        db.execute("PRAGMA synchronous=NORMAL")
        return db

    def get(self, name: str) -> Optional[str]:
        row = self.db.execute("SELECT value FROM meta WHERE name = ?", (name,)).fetchone()
        return row[0] if row else None

    def set(self, db: sqlite3.Connection, name: str, value: str) -> None:
        db.execute("INSERT OR REPLACE INTO meta (name, value) VALUES (?, ?)", (name, value))

    def counts(self) -> Dict[str, Tuple[int, int]]:
        return {status: (count, size or 0) for status, count, size in self.db.execute(
            "SELECT status, COUNT(*), SUM(size) FROM objects GROUP BY status")}


class Stats:
    def __init__(self, counts: Dict[str, Tuple[int, int]]):
        self.lock = threading.Lock()
        self.by_status: Dict[str, int] = {}
        self.listed = sum(count for count, _ in counts.values())
        self.done_before = sum(counts.get(s, (0, 0))[0] for s in DONE)
        self.bytes = 0
        self.started = time.monotonic()
        self.in_flight = 0

    def add(self, result: Result) -> None:
        with self.lock:
            self.by_status[result.status] = self.by_status.get(result.status, 0) + 1
            self.bytes += result.size


class Migration:
    def __init__(self, args: argparse.Namespace):
        self.args = args
        timeout = args.timeout
        self.s3 = S3(Endpoint(args.source_url, args.source_ca_file, timeout),
                     args.source_bucket, args.source_region, args.source_access_key,
                     args.source_secret_key, args.source_session_token)
        self.noodles = [Endpoint(url, args.noodle_ca_file, timeout) for url in args.noodle_url]
        self.state = State(args.state)
        self.work: "queue.Queue[Optional[Item]]" = queue.Queue(maxsize=args.parallel * 8)
        self.results: "queue.Queue[Optional[Result]]" = queue.Queue()
        self.stop = threading.Event()
        self.fatal: Optional[str] = None
        self.listing_done = False
        self.stats = Stats(self.state.counts())

    # --- setup -------------------------------------------------------------

    def check_source(self) -> None:
        """Refuses to resume a state file made for another bucket or prefix."""
        shard, shards = self.args.shard
        source = f"{self.s3.endpoint.url}/{self.s3.bucket}/{self.args.source_prefix}"
        if shards > 1:
            source += f" (shard {shard}/{shards})"
        known = self.state.get("source")
        if known is None:
            self.state.set(self.state.db, "source", source)
        elif known != source:
            raise Fatal(f"{self.args.state} belongs to {known}, not {source}; "
                        "use another --state file")
        if self.args.relist:
            # List again from the start; known keys are skipped.
            self.state.db.execute("DELETE FROM meta WHERE name IN "
                                  "('listed_after', 'listing_complete')")

    def check_noodle(self) -> None:
        for noodle in self.noodles:
            connection = noodle.connect()
            try:
                connection.request("GET", f"{noodle.base_path}/health/ready",
                                   headers={"User-Agent": USER_AGENT})
                response = connection.getresponse()
                response.read()
                if response.status != 200:
                    raise Fatal(f"{noodle.url} is not ready (HTTP {response.status})")
            except (OSError, http.client.HTTPException) as ex:
                hint = " (use --noodle-ca-file)" if isinstance(
                    ex, ssl.SSLCertVerificationError) else ""
                raise Fatal(f"cannot reach {noodle.url}: {ex}{hint}") from ex
            finally:
                connection.close()

    # --- producer ----------------------------------------------------------

    def enqueue(self, item: Item) -> bool:
        while not self.stop.is_set():
            try:
                self.work.put(item, timeout=0.5)
                return True
            except queue.Full:
                continue
        return False

    def produce(self) -> None:
        """Feeds the workers: leftovers of earlier runs, then the listing."""
        limit = self.args.limit
        queued = 0
        after = ""
        # Listed before but not finished (pending, or failed last time).
        while not self.stop.is_set() and not self.args.list_only:
            rows = self.state.db.execute(
                "SELECT key, size FROM objects WHERE status IN ('pending', 'failed') "
                "AND key > ? ORDER BY key LIMIT 1000", (after,)).fetchall()
            if not rows:
                break
            for key, size in rows:
                if (limit and queued >= limit) or not self.enqueue(Item(key, size)):
                    return
                queued += 1
            after = rows[-1][0]
        if self.state.get("listing_complete") == "1":
            self.listing_done = True
            return
        self.list_bucket(queued)

    def list_bucket(self, queued: int) -> None:
        limit = self.args.limit
        start_after = self.state.get("listed_after")
        token: Optional[str] = None
        connection = self.s3.endpoint.connect()
        suffix = self.args.suffix
        writer = self.state.connect()
        try:
            while not self.stop.is_set():
                items, keys, token = retry(
                    lambda: self.list_with(connection, start_after, token),
                    self.args.retries, self.stop)
                wanted = [item for item in items
                          if item.key.endswith(suffix) and self.in_shard(item.key)]
                # Only keys not seen before need work (with --relist, most
                # are known; unfinished ones were queued above already).
                known = set()
                if wanted:
                    marks = ",".join("?" * len(wanted))
                    known = {row[0] for row in writer.execute(
                        f"SELECT key FROM objects WHERE key IN ({marks})",
                        [item.key for item in wanted])}
                new = [item for item in wanted if item.key not in known]
                writer.execute("BEGIN IMMEDIATE")
                writer.executemany(
                    "INSERT OR IGNORE INTO objects (key, size) VALUES (?, ?)",
                    [(item.key, item.size) for item in new])
                if keys:
                    start_after = keys[-1]
                    self.state.set(writer, "listed_after", start_after)
                if token is None:
                    self.state.set(writer, "listing_complete", "1")
                writer.execute("COMMIT")
                with self.stats.lock:
                    self.stats.listed += len(new)
                for item in new:
                    if self.args.list_only:
                        continue
                    if (limit and queued >= limit) or not self.enqueue(item):
                        return
                    queued += 1
                if token is None:
                    self.listing_done = True
                    return
        finally:
            connection.close()
            writer.close()

    def in_shard(self, key: str) -> bool:
        shard, shards = self.args.shard
        return shards == 1 or zlib.crc32(key.encode()) % shards == shard

    def list_with(self, connection: http.client.HTTPConnection,
                  start_after: Optional[str], token: Optional[str]):
        try:
            return self.s3.list_page(connection, self.args.source_prefix, start_after, token)
        except ssl.SSLCertVerificationError as ex:
            raise Fatal(f"TLS certificate of the S3 endpoint: {ex} "
                        "(add its CA with --source-ca-file)") from ex
        except (OSError, http.client.HTTPException) as ex:
            # A closed connection reconnects on its next request.
            connection.close()
            raise Transient(f"listing the bucket: {ex}") from ex

    # --- workers -----------------------------------------------------------

    def work_loop(self, index: int) -> None:
        noodle = self.noodles[index % len(self.noodles)]
        connections: Dict[str, Optional[http.client.HTTPConnection]] = {
            "source": None, "noodle": None}
        try:
            while not self.stop.is_set():
                try:
                    item = self.work.get(timeout=0.5)
                except queue.Empty:
                    if self.producer_finished.is_set() and self.work.empty():
                        return
                    continue
                if item is None:
                    return
                with self.stats.lock:
                    self.stats.in_flight += 1
                try:
                    result = self.migrate(item, noodle, connections)
                finally:
                    with self.stats.lock:
                        self.stats.in_flight -= 1
                if result is not None:
                    self.stats.add(result)
                    self.results.put(result)
        except Fatal as ex:
            self.fail(str(ex))
        finally:
            for connection in connections.values():
                if connection is not None:
                    connection.close()

    def migrate(self, item: Item, noodle: Endpoint,
                connections: Dict[str, Optional[http.client.HTTPConnection]]
                ) -> Optional[Result]:
        last_error = ""
        for attempt in range(1, self.args.retries + 1):
            if self.stop.is_set():
                return None
            try:
                result = self.upload_once(item, noodle, connections)
                result.attempts = attempt
                return result
            except Transient as ex:
                last_error = str(ex)
                close_all(connections)
                if attempt < self.args.retries:
                    self.stop.wait(min(60.0, 2 ** attempt) * (0.5 + random.random()))
        return Result(item.key, "failed", self.args.retries, message=last_error)

    def upload_once(self, item: Item, noodle: Endpoint,
                    connections: Dict[str, Optional[http.client.HTTPConnection]]) -> Result:
        try:
            if connections["source"] is None:
                connections["source"] = self.s3.endpoint.connect()
            if connections["noodle"] is None:
                connections["noodle"] = noodle.connect()
            source = self.s3.open(connections["source"], item.key)
            if source is None:
                return Result(item.key, "missing", 0, message="deleted from the bucket")
            length = int(source.getheader("Content-Length", item.size))
            reader = HashingReader(source)
            upload = connections["noodle"]
            try:
                upload.request("PUT", f"{noodle.base_path}/-/noodle/upload", body=reader, headers={
                    "Authorization": f"Bearer {self.args.noodle_api_key}",
                    "Content-Type": "application/octet-stream",
                    "Content-Length": str(length),
                    "User-Agent": USER_AGENT,
                })
            except (BrokenPipeError, ConnectionResetError):
                # Noodle may answer before reading everything (401, 413).
                pass
            response = upload.getresponse()
            body = response.read()
        except ssl.SSLCertVerificationError as ex:
            raise Fatal(f"TLS certificate not trusted: {ex} "
                        "(use --source-ca-file / --noodle-ca-file)") from ex
        except (OSError, http.client.HTTPException) as ex:
            raise Transient(f"{type(ex).__name__}: {ex}") from ex
        if reader.count != length:
            # The rest of the object never arrived; the connection is unusable.
            close_all(connections)
            if response.status not in (401, 403, 413):
                raise Transient(f"read {reader.count} of {length} bytes from S3")
        return self.interpret(item, response.status, body, reader, length)

    def interpret(self, item: Item, status: int, body: bytes, reader: HashingReader,
                  length: int) -> Result:
        try:
            answer = json.loads(body) if body else {}
        except ValueError:
            answer = {}
        message = answer.get("message") or answer.get("error")
        name, version = answer.get("name"), answer.get("version")
        if status in (200, 201):
            expected = "sha512-" + base64.b64encode(reader.sha512.digest()).decode()
            if answer.get("integrity") != expected:
                raise Transient("integrity reported by Noodle does not match the bytes sent")
            return Result(item.key, "added" if status == 201 else "present", 0, status,
                          name, version, None, length)
        if status == 409:
            return Result(item.key, "conflict", 0, status, name, version, message, length)
        if status in (400, 413):
            return Result(item.key, "rejected", 0, status, name, version, message, length)
        if status in (401, 403):
            raise Fatal(f"Noodle refused the API key (HTTP {status}): {message}")
        if status in RETRYABLE_HTTP or status >= 500:
            raise Transient(f"Noodle answered HTTP {status}: {message}")
        return Result(item.key, "failed", 0, status, name, version,
                      f"HTTP {status}: {message}", length)

    def fail(self, message: str) -> None:
        if self.fatal is None:
            self.fatal = message
        self.stop.set()

    # --- state writer and progress ------------------------------------------

    def write_results(self) -> None:
        db = self.state.connect()
        batch: List[Result] = []
        last = time.monotonic()
        try:
            while True:
                try:
                    result = self.results.get(timeout=0.5)
                except queue.Empty:
                    result = None
                    if self.writer_stop.is_set():
                        break
                if result is not None:
                    batch.append(result)
                if batch and (len(batch) >= 1000 or time.monotonic() - last > 1):
                    self.flush(db, batch)
                    batch = []
                    last = time.monotonic()
            while not self.results.empty():
                batch.append(self.results.get())
            if batch:
                self.flush(db, batch)
        finally:
            db.close()

    @staticmethod
    def flush(db: sqlite3.Connection, batch: List[Result]) -> None:
        now = time.time()
        db.execute("BEGIN IMMEDIATE")
        db.executemany(
            "UPDATE objects SET status = ?, attempts = attempts + ?, http_status = ?, "
            "name = ?, version = ?, message = ?, updated = ? WHERE key = ?",
            [(r.status, r.attempts, r.http_status, r.name, r.version, r.message, now, r.key)
             for r in batch])
        db.execute("COMMIT")

    def report_progress(self) -> None:
        while not self.progress_stop.wait(self.args.progress):
            print(self.progress_line(), file=sys.stderr, flush=True)

    def progress_line(self) -> str:
        stats = self.stats
        with stats.lock:
            elapsed = max(0.001, time.monotonic() - stats.started)
            by = dict(stats.by_status)
            done_now = sum(by.values())
            line = (f"{stats.listed:,} listed{' (complete)' if self.listing_done else ''}, "
                    f"{stats.done_before + done_now:,} done this far: "
                    + ", ".join(f"{by.get(s, 0):,} {s}" for s in
                                ("added", "present", "conflict", "rejected", "missing", "failed"))
                    + f" | {done_now / elapsed:,.0f}/s, {stats.bytes / elapsed / 1e6:,.1f} MB/s"
                    + f" | {stats.in_flight} in flight")
            remaining = stats.listed - stats.done_before - done_now
            if self.listing_done and done_now and remaining > 0:
                eta = remaining / (done_now / elapsed)
                line += f" | about {datetime.timedelta(seconds=int(eta))} left"
        return line

    # --- run ---------------------------------------------------------------

    def run(self) -> int:
        self.check_source()
        counts = self.state.counts()
        if counts:
            print("Resuming: " + ", ".join(
                f"{count:,} {status}" for status, (count, _) in sorted(counts.items())),
                file=sys.stderr)
        if not self.args.list_only:
            self.check_noodle()
        self.producer_finished = threading.Event()
        self.writer_stop = threading.Event()
        self.progress_stop = threading.Event()
        threading.Thread(target=self.report_progress, daemon=True).start()
        writer = threading.Thread(target=self.write_results)
        writer.start()
        workers = [] if self.args.list_only else [
            threading.Thread(target=self.work_loop, args=(i,), daemon=True)
            for i in range(self.args.parallel)]
        for worker in workers:
            worker.start()
        try:
            self.produce()
        except Fatal as ex:
            self.fail(str(ex))
        except Transient as ex:
            self.fail(f"giving up: {ex}")
        self.producer_finished.set()
        for worker in workers:
            worker.join()
        self.writer_stop.set()
        writer.join()
        self.progress_stop.set()
        print(self.progress_line(), file=sys.stderr)
        return self.summarize()

    def summarize(self) -> int:
        counts = self.state.counts()
        total = sum(count for count, _ in counts.values())
        size = sum(s for _, s in counts.values())
        print(f"\n{total:,} tarballs ({size / 1e9:,.1f} GB) known; listing "
              f"{'complete' if self.state.get('listing_complete') == '1' else 'NOT complete'}",
              file=sys.stderr)
        for status in ("added", "present", "conflict", "rejected", "missing", "failed", "pending"):
            if status in counts:
                print(f"  {status:9s} {counts[status][0]:>12,}", file=sys.stderr)
        problems = sum(counts.get(s, (0, 0))[0] for s in ("conflict", "rejected", "failed"))
        if self.args.failures:
            with open(self.args.failures, "w", encoding="utf-8") as output:
                output.write("status\thttp\tkey\tname\tversion\tmessage\n")
                for row in self.state.db.execute(
                        "SELECT status, http_status, key, name, version, message FROM objects "
                        "WHERE status IN ('conflict', 'rejected', 'failed') ORDER BY key"):
                    output.write("\t".join("" if v is None else str(v).replace("\t", " ")
                                           .replace("\n", " ") for v in row) + "\n")
            print(f"Problems listed in {self.args.failures}", file=sys.stderr)
        if self.fatal:
            print(f"Stopped: {self.fatal}", file=sys.stderr)
            return 2
        if self.args.list_only:
            return 0
        unfinished = counts.get("pending", (0, 0))[0] + counts.get("failed", (0, 0))[0]
        if self.stop.is_set() or self.state.get("listing_complete") != "1" or unfinished:
            print("Not finished; run the same command again to continue.", file=sys.stderr)
            return 1
        return 1 if problems else 0


def retry(call, attempts: int, stop: threading.Event):
    for attempt in range(1, attempts + 1):
        try:
            return call()
        except Transient:
            if attempt == attempts or stop.is_set():
                raise
            stop.wait(min(60.0, 2 ** attempt) * (0.5 + random.random()))
    raise Transient("unreachable")


def close_all(connections: Dict[str, Optional[http.client.HTTPConnection]]) -> None:
    for name, connection in connections.items():
        if connection is not None:
            connection.close()
        connections[name] = None


def parse_args(argv: List[str]) -> argparse.Namespace:
    env = os.environ.get
    parser = argparse.ArgumentParser(
        description="Copies every npm tarball from an S3 bucket into Noodle.",
        formatter_class=argparse.RawDescriptionHelpFormatter, epilog=__doc__)
    parser.add_argument("--source-url", default=env("SOURCE_S3_URL"),
                        help="S3 endpoint, e.g. https://minio.corp.example:9000")
    parser.add_argument("--source-bucket", default=env("SOURCE_S3_BUCKET"))
    parser.add_argument("--source-prefix",
                        default=env("SOURCE_S3_PREFIX", "npm/registry.npmjs.org/"),
                        help="only keys below this prefix (default: %(default)s)")
    parser.add_argument("--source-region", default=env("SOURCE_S3_REGION", "us-east-1"))
    parser.add_argument("--source-access-key", default=env("AWS_ACCESS_KEY_ID"))
    parser.add_argument("--source-secret-key", default=env("AWS_SECRET_ACCESS_KEY"))
    parser.add_argument("--source-session-token", default=env("AWS_SESSION_TOKEN"))
    parser.add_argument("--source-ca-file", help="CA certificate(s) for the S3 endpoint")
    parser.add_argument("--noodle-url", action="append",
                        help="Noodle server; repeat it to spread uploads over several instances")
    parser.add_argument("--noodle-api-key", default=env("NOODLE_API_KEY"))
    parser.add_argument("--noodle-ca-file", help="CA certificate(s) for Noodle")
    parser.add_argument("--state", default="noodle-migration.sqlite",
                        help="progress file (default: %(default)s)")
    parser.add_argument("--parallel", type=int, default=32,
                        help="uploads at a time (default: %(default)s)")
    parser.add_argument("--suffix", default=".tgz",
                        help="only keys ending in this (default: %(default)s)")
    parser.add_argument("--retries", type=int, default=6,
                        help="attempts per tarball and run for transient errors "
                             "(default: %(default)s)")
    parser.add_argument("--timeout", type=float, default=300,
                        help="network timeout in seconds (default: %(default)s)")
    parser.add_argument("--limit", type=int, default=0,
                        help="copy at most this many tarballs in this run (for a trial)")
    parser.add_argument("--list-only", action="store_true",
                        help="only list the bucket into the state file and count")
    parser.add_argument("--shard", default="0/1", metavar="K/N",
                        help="take only the K-th of N parts of the bucket (0-based), to run N "
                             "copies of this script side by side, each with its own --state")
    parser.add_argument("--relist", action="store_true",
                        help="list the bucket again from the start and copy what was added "
                             "since (e.g. right before switching over)")
    parser.add_argument("--failures", help="write conflicts, rejections and failures to this TSV")
    parser.add_argument("--progress", type=float, default=10,
                        help="seconds between progress lines (default: %(default)s)")
    args = parser.parse_args(argv)
    if not args.noodle_url:
        args.noodle_url = [u for u in (env("NOODLE_URL") or "").split(",") if u]
    missing = [flag for flag, value in (
        ("--source-url", args.source_url), ("--source-bucket", args.source_bucket),
        ("--source-access-key", args.source_access_key),
        ("--source-secret-key", args.source_secret_key)) if not value]
    if not args.list_only:
        missing += [flag for flag, value in (
            ("--noodle-url", args.noodle_url), ("--noodle-api-key", args.noodle_api_key))
            if not value]
    if missing:
        parser.error("missing " + ", ".join(missing))
    if args.parallel < 1 or args.retries < 1:
        parser.error("--parallel and --retries must be at least 1")
    try:
        shard, shards = (int(part) for part in args.shard.split("/"))
    except ValueError:
        parser.error("--shard must look like 0/4")
    if not 0 <= shard < shards:
        parser.error("--shard K/N needs 0 <= K < N")
    args.shard = (shard, shards)
    return args


def main(argv: List[str]) -> int:
    args = parse_args(argv)
    try:
        migration = Migration(args)
    except (Fatal, OSError, sqlite3.Error) as ex:
        print(f"!! {ex}", file=sys.stderr)
        return 2
    interrupts = 0

    def interrupt(signum, frame):
        nonlocal interrupts
        interrupts += 1
        if interrupts > 1:
            print("\nQuitting at once; the uploads in flight are repeated next time.",
                  file=sys.stderr)
            os._exit(1)
        print("\nFinishing the uploads in flight (Ctrl-C again to quit at once)...",
              file=sys.stderr)
        migration.stop.set()

    signal.signal(signal.SIGINT, interrupt)
    signal.signal(signal.SIGTERM, interrupt)
    try:
        return migration.run()
    except Fatal as ex:
        print(f"!! {ex}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
