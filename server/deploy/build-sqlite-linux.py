#!/usr/bin/env python3
"""Build and validate app-local SQLite 3.53.3 with the host Linux toolchain.

Python 3.6+; requires gcc, standard development headers, and HTTPS access to
sqlite.org. This creates a NEW output directory and never installs a library,
edits a database, or replaces a running application's native library.

Example: python3 build-sqlite-linux.py --output /tmp/mugonzei-sqlite-3.53.3
"""

from __future__ import print_function

import argparse
import ctypes
import datetime
import hashlib
import json
import os
import platform
import shutil
import subprocess
import sys
import time
import urllib.parse
import urllib.request
import zipfile


VERSION = "3.53.3"
VERSION_NUMBER = 3053003
SOURCE_ID = "2026-06-26 20:14:12 d4c0e51e4aeb96955b99185ab9cde75c339e2c29c3f3f12428d364a10d782c62"
SOURCE_URL = "https://www.sqlite.org/2026/sqlite-amalgamation-3530300.zip"
RELEASE_URL = "https://www.sqlite.org/releaselog/3_53_3.html"
LICENSE_URL = "https://www.sqlite.org/copyright.html"
# ZIP hash was independently computed from the official HTTPS download. The
# release page publishes the sqlite3.c SHA3-256 below, not this archive hash.
ZIP_SHA256 = "646421e12aac110282ef8cc68f1a62d4bb15fc7b8f09da0b53e29ee690500431"
C_SHA3_256 = "28e484abdaa43630e34040ef6ed92be973a1ad54107803d8af5145b889c23ed7"
ARCHIVE_NAME = "sqlite-amalgamation-3530300.zip"
ARCHIVE_ROOT = "sqlite-amalgamation-3530300/"
MAX_DOWNLOAD_BYTES = 8 * 1024 * 1024
MAX_SOURCE_BYTES = 24 * 1024 * 1024
BUILD_FLAGS_SOURCE = (
    "https://github.com/ericsink/cb/blob/"
    "cd2922b8867e4360f0976601414bd24a3ad613d8/bld/cb.cs#L1246-L1268"
)
PROVIDER_SOURCE = (
    "https://github.com/ericsink/SQLitePCL.raw/blob/v2.1.11/"
    "src/SQLitePCLRaw.provider.e_sqlite3/Generated/"
    "provider_e_sqlite3_funcptrs_notwin.cs#L235-L253"
)
# Match SQLitePCLRaw's basic Linux features. -O0 is deliberate for small hosts;
# no parallel compiler or LTO is used. Explicitly link pthread/dl/m for glibc
# versions predating the libpthread/libdl merge in glibc 2.34.
FLAGS = [
    "-O0", "-fPIC", "-shared", "-pthread",
    "-DSQLITE_THREADSAFE=1", "-DSQLITE_OS_UNIX=1",
    "-DSQLITE_ENABLE_COLUMN_METADATA", "-DSQLITE_ENABLE_FTS3_PARENTHESIS",
    "-DSQLITE_ENABLE_FTS4", "-DSQLITE_ENABLE_FTS5",
    "-DSQLITE_ENABLE_JSON1", "-DSQLITE_ENABLE_MATH_FUNCTIONS",
    "-DSQLITE_ENABLE_RTREE", "-DSQLITE_ENABLE_SNAPSHOT",
    "-DSQLITE_DEFAULT_FOREIGN_KEYS=1",
]
LINK_FLAGS = ["-Wl,-soname,libe_sqlite3.so", "-Wl,-z,relro,-z,now", "-ldl", "-lm"]
REQUIRED_OPTIONS = [
    "THREADSAFE=1", "DEFAULT_FOREIGN_KEYS", "ENABLE_COLUMN_METADATA",
    "ENABLE_FTS3_PARENTHESIS", "ENABLE_FTS4", "ENABLE_FTS5",
    "ENABLE_MATH_FUNCTIONS", "ENABLE_RTREE", "ENABLE_SNAPSHOT",
]
REQUIRED_EXPORTS = [
    "sqlite3_libversion", "sqlite3_libversion_number", "sqlite3_sourceid",
    "sqlite3_compileoption_get", "sqlite3_compileoption_used", "sqlite3_threadsafe",
    "sqlite3_initialize", "sqlite3_open_v2", "sqlite3_close_v2", "sqlite3_exec",
    "sqlite3_free", "sqlite3_prepare_v2", "sqlite3_step", "sqlite3_finalize",
    "sqlite3_reset", "sqlite3_clear_bindings", "sqlite3_bind_int64",
    "sqlite3_bind_double", "sqlite3_bind_null", "sqlite3_bind_text",
    "sqlite3_bind_blob", "sqlite3_column_count", "sqlite3_column_int64",
    "sqlite3_column_double", "sqlite3_column_text", "sqlite3_column_blob",
    "sqlite3_column_type", "sqlite3_changes", "sqlite3_last_insert_rowid",
    "sqlite3_errmsg", "sqlite3_extended_errcode", "sqlite3_busy_timeout",
    "sqlite3_table_column_metadata", "sqlite3_create_function_v2",
    "sqlite3_snapshot_get", "sqlite3_snapshot_open", "sqlite3_snapshot_free",
]


def write_json(path, value):
    with open(path, "x", encoding="utf-8") as handle:
        json.dump(value, handle, ensure_ascii=False, indent=2, sort_keys=True)
        handle.write("\n")


def sha256_file(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(65536), b""):
            digest.update(chunk)
    return digest.hexdigest()


def download_archive(path):
    digest = hashlib.sha256()
    size = 0
    request = urllib.request.Request(SOURCE_URL, headers={"User-Agent": "Mugonzei-SQLite-Builder/1"})
    with urllib.request.urlopen(request, timeout=60) as response:
        destination = urllib.parse.urlparse(response.geturl())
        if destination.scheme != "https" or destination.netloc not in ("www.sqlite.org", "sqlite.org"):
            raise RuntimeError("The SQLite download redirected outside the official HTTPS host.")
        with open(path, "xb") as handle:
            while True:
                chunk = response.read(65536)
                if not chunk:
                    break
                size += len(chunk)
                if size > MAX_DOWNLOAD_BYTES:
                    raise RuntimeError("The SQLite archive exceeded the download size limit.")
                digest.update(chunk)
                handle.write(chunk)
    if digest.hexdigest() != ZIP_SHA256:
        raise RuntimeError("SQLite archive SHA-256 mismatch; compilation was not started.")
    return size


def extract_sources(archive_path, source_directory):
    os.mkdir(source_directory)
    # Read only these fixed members; never trust ZIP paths or extractall(). The
    # original ZIP is also retained, including the original public-domain headers.
    with zipfile.ZipFile(archive_path, "r") as archive:
        for name in ("sqlite3.c", "sqlite3.h", "sqlite3ext.h", "shell.c"):
            info = archive.getinfo(ARCHIVE_ROOT + name)
            if info.file_size > MAX_SOURCE_BYTES:
                raise RuntimeError("Unexpected source member size: " + name)
            data = archive.read(info)
            if name == "sqlite3.c" and hashlib.sha3_256(data).hexdigest() != C_SHA3_256:
                raise RuntimeError("sqlite3.c differs from the SHA3-256 published by SQLite.")
            with open(os.path.join(source_directory, name), "xb") as handle:
                handle.write(data)


def verify_library(path):
    # Loading by absolute path tests THIS newly built library, not system SQLite.
    library = ctypes.CDLL(path)
    for name in REQUIRED_EXPORTS:
        if not hasattr(library, name):
            raise RuntimeError("Required SQLite export is missing: " + name)
    library.sqlite3_libversion.restype = ctypes.c_char_p
    library.sqlite3_sourceid.restype = ctypes.c_char_p
    library.sqlite3_libversion_number.restype = ctypes.c_int
    library.sqlite3_threadsafe.restype = ctypes.c_int
    version = library.sqlite3_libversion().decode("ascii")
    source_id = library.sqlite3_sourceid().decode("ascii")
    if version != VERSION or library.sqlite3_libversion_number() != VERSION_NUMBER or source_id != SOURCE_ID:
        raise RuntimeError("The compiled SQLite version/source ID did not match the pinned release.")
    if library.sqlite3_threadsafe() != 1:
        raise RuntimeError("SQLite must use serialized thread-safe mode (THREADSAFE=1).")
    library.sqlite3_compileoption_used.argtypes = [ctypes.c_char_p]
    library.sqlite3_compileoption_used.restype = ctypes.c_int
    for option in REQUIRED_OPTIONS:
        if not library.sqlite3_compileoption_used(option.encode("ascii")):
            raise RuntimeError("Required SQLite compile option is missing: " + option)
    library.sqlite3_compileoption_get.argtypes = [ctypes.c_int]
    library.sqlite3_compileoption_get.restype = ctypes.c_char_p
    options = []
    for index in range(500):
        option = library.sqlite3_compileoption_get(index)
        if option is None:
            break
        options.append(option.decode("ascii"))
    else:
        raise RuntimeError("Unexpected SQLite compile option count.")

    library.sqlite3_open_v2.argtypes = [ctypes.c_char_p, ctypes.POINTER(ctypes.c_void_p), ctypes.c_int, ctypes.c_char_p]
    library.sqlite3_open_v2.restype = ctypes.c_int
    library.sqlite3_exec.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p]
    library.sqlite3_exec.restype = ctypes.c_int
    library.sqlite3_close_v2.argtypes = [ctypes.c_void_p]
    library.sqlite3_close_v2.restype = ctypes.c_int
    database = ctypes.c_void_p()
    try:
        if library.sqlite3_open_v2(b":memory:", ctypes.byref(database), 2 | 4 | 0x10000, None) != 0:
            raise RuntimeError("SQLite could not open an in-memory validation database.")
        sql = (b"CREATE TABLE parent(id INTEGER PRIMARY KEY);"
               b"CREATE TABLE child(pid INTEGER REFERENCES parent(id));"
               b"BEGIN IMMEDIATE;INSERT INTO parent VALUES(1);"
               b"INSERT INTO child VALUES(1);COMMIT;")
        if library.sqlite3_exec(database, sql, None, None, None) != 0:
            raise RuntimeError("SQLite in-memory transaction validation failed.")
        if library.sqlite3_exec(database, b"INSERT INTO child VALUES(2);", None, None, None) != 19:
            raise RuntimeError("SQLite did not enforce the default foreign-key constraint.")
    finally:
        if database.value:
            library.sqlite3_close_v2(database)
    return {
        "version": version, "sourceId": source_id, "exports": REQUIRED_EXPORTS,
        "compileOptions": options, "threadsafe": 1,
        "inMemoryTransaction": True, "defaultForeignKeyConstraint": True,
        "scope": "Loaded on the build host; no application database or live service was accessed.",
    }


def build(output, compiler):
    started = time.monotonic()
    print("Downloading and verifying official SQLite " + VERSION + "...", flush=True)
    archive_path = os.path.join(output, ARCHIVE_NAME)
    archive_size = download_archive(archive_path)
    extract_sources(archive_path, os.path.join(output, "source"))
    notice = (
        "SQLite " + VERSION + "\n\n"
        "SQLite is in the public domain. Original source headers are preserved in source/\n"
        "and in the original amalgamation ZIP. No SQLitePCLRaw/cb source is included.\n"
        "Official copyright statement: " + LICENSE_URL + "\n"
        "Official release and source-file checksum: " + RELEASE_URL + "\n"
        "Official source archive: " + SOURCE_URL + "\n\n"
        "The ZIP SHA-256 was computed from the official HTTPS download; the sqlite3.c\n"
        "SHA3-256 is published on the release page. Both are checked before compilation.\n"
        "The Linux provider.e_sqlite3 2.1.11 encryption methods return SQLITE_ERROR in\n"
        "managed code, so cb's native encryption stubs are not needed for this project.\n"
        + PROVIDER_SOURCE + "\n"
    )
    with open(os.path.join(output, "SQLITE-SOURCE-NOTICE.txt"), "x", encoding="utf-8") as handle:
        handle.write(notice)
    version_output = subprocess.check_output([compiler, "--version"], stderr=subprocess.STDOUT)
    compiler_version = version_output.decode("utf-8", errors="replace").splitlines()[0]
    command = [compiler] + FLAGS + ["source/sqlite3.c"] + LINK_FLAGS + ["-o", "libe_sqlite3.so"]
    print("Compiling with -O0 in one compiler process; see compile.log...", flush=True)
    with open(os.path.join(output, "compile.log"), "xb") as log:
        result = subprocess.run(command, cwd=output, stdout=log, stderr=subprocess.STDOUT)
    if result.returncode != 0:
        raise RuntimeError("gcc failed with exit code {0}; inspect compile.log. No library was installed.".format(result.returncode))
    library_path = os.path.join(output, "libe_sqlite3.so")
    print("Validating the newly built library with ctypes...", flush=True)
    validation = verify_library(library_path)
    readelf = shutil.which("readelf")
    elf_report = None
    if readelf:
        with open(os.path.join(output, "elf-version-info.txt"), "xb") as log:
            result = subprocess.run([readelf, "--version-info", "libe_sqlite3.so"], cwd=output, stdout=log, stderr=subprocess.STDOUT)
        if result.returncode != 0:
            raise RuntimeError("readelf validation failed; inspect elf-version-info.txt.")
        elf_report = "elf-version-info.txt"
    recorded_command = [os.path.basename(compiler)] + command[1:]
    manifest = {
        "formatVersion": 1, "status": "validated", "sqliteVersion": VERSION,
        "createdAt": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "elapsedSeconds": round(time.monotonic() - started, 3),
        "source": {
            "url": SOURCE_URL, "archive": ARCHIVE_NAME, "archiveBytes": archive_size,
            "archiveSha256": ZIP_SHA256,
            "archiveHashProvenance": "Computed from the official HTTPS download; not a separately published archive hash.",
            "sqlite3CSha3_256": C_SHA3_256, "publishedSourceHashUrl": RELEASE_URL,
            "license": "Public domain", "licenseUrl": LICENSE_URL,
        },
        "build": {
            "compiler": compiler_version, "command": recorded_command,
            "workingDirectory": "output directory", "featureFlagsSource": BUILD_FLAGS_SOURCE,
            "optimization": "-O0 for lower build memory; no parallel compilation or LTO",
            "nativeEncryptionStubs": False, "providerEvidence": PROVIDER_SOURCE,
        },
        "host": {
            "system": platform.system(), "machine": platform.machine(),
            "libc": list(platform.libc_ver()), "python": platform.python_version(),
        },
        "output": {"file": "libe_sqlite3.so", "sha256": sha256_file(library_path), "bytes": os.path.getsize(library_path)},
        "validation": validation, "elfVersionReport": elf_report,
        "installationPerformed": False,
    }
    write_json(os.path.join(output, "build-manifest.json"), manifest)
    print("Validated: " + library_path, flush=True)
    print("Nothing was installed. Review build-manifest.json before a separate deployment.", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--output", required=True, help="New output directory; existing paths are refused.")
    parser.add_argument("--cc", default="gcc", help="C compiler executable (default: gcc); no shell command or extra flags.")
    args = parser.parse_args()
    if sys.version_info < (3, 6):
        parser.error("Python 3.6 or later is required.")
    if sys.platform != "linux":
        parser.error("Run this script on the target Linux host or a compatible Linux build host.")
    compiler = shutil.which(args.cc)
    if compiler is None:
        parser.error("C compiler was not found: " + args.cc)
    requested = os.path.abspath(os.path.expanduser(args.output))
    if os.path.lexists(requested):
        parser.error("Output already exists; choose a new directory. Existing files will not be overwritten.")
    output = os.path.realpath(requested)
    if not os.path.isdir(os.path.dirname(output)):
        parser.error("Output parent directory must already exist.")
    try:
        os.mkdir(output)
    except OSError as error:
        parser.error("Could not create a new output directory: " + str(error))
    try:
        build(output, compiler)
    except (Exception, KeyboardInterrupt) as error:
        message = str(error) or type(error).__name__
        try:
            write_json(os.path.join(output, "build-failure.json"), {"status": "failed", "error": message, "installationPerformed": False})
        except OSError:
            pass
        print("Build failed: " + message, file=sys.stderr)
        print("Partial files were retained for inspection; use a new output directory to retry.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
