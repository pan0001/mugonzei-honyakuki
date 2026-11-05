#!/usr/bin/env python3
"""Publish a verified Windows installer and the public website on the project's existing Linux host."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import tempfile

ROOT = Path("/opt/mugonzei/website")
REPO = "pan0001/mugonzei-honyakuki"
PAGES = ("index.html", "landing.css", "main.js", "account.html", "account.js",
         "status.html", "status.js", "styles.css", "favicon.svg", "robots.txt")


def require(test, reason):
    if not test:
        raise SystemExit(reason)


def digest(path):
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def fetch(url, target, expected):
    # The public release URL redirects to GitHub's asset host. TLS is mandatory on each hop.
    subprocess.run(["curl", "--fail", "--silent", "--show-error", "--location", "--proto", "=https",
                    "--proto-redir", "=https", "--connect-timeout", "15", "--max-time", "600",
                    "--retry", "2", "--output", str(target), url], check=True)
    require(digest(target) == expected, "Download SHA-256 mismatch: " + target.name)
    os.chmod(target, 0o644)


def controlled(path):
    s = path.lstat()
    require(stat.S_ISDIR(s.st_mode) and s.st_uid == 0 and not (s.st_mode & 0o022),
            "Expected root-owned directory: " + str(path))


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("manifest", help="JSON: commit, previous, tag, installerSha256 and pages (hash per filename)")
    m = json.loads(Path(ap.parse_args().manifest).read_text(encoding="utf8"))
    commit, previous, tag, installer_hash = (m[k] for k in ("commit", "previous", "tag", "installerSha256"))
    require(os.geteuid() == 0, "Run on the existing website host as root")
    os.umask(0o022)
    require(all(re.fullmatch(r"[0-9a-f]{40}", v) for v in (commit, previous)) and
            re.fullmatch(r"v[0-9]+\.[0-9]+\.[0-9]+(?:-preview)?", tag), "Unexpected identifiers")
    pages = m["pages"]
    require(set(pages) == set(PAGES) and all(re.fullmatch(r"[0-9a-f]{64}", v)
            for v in [installer_hash] + list(pages.values())), "Invalid file manifest")
    releases = ROOT / "releases"
    controlled(ROOT)
    controlled(releases)
    current = ROOT / "current"
    site = releases / commit
    old = releases / previous
    require(current.is_symlink() and current.resolve() in (old, site), "Live version changed; refusing to switch")
    controlled(old)
    downloads = ROOT / "downloads"
    downloads.mkdir(mode=0o755, exist_ok=True)
    controlled(downloads)
    name = "Mugonzei-Honyakuki-Setup-win-x64-" + tag + ".exe"
    destination = downloads / tag
    checksums = installer_hash + "  " + name + "\n"
    if not destination.exists():
        with tempfile.TemporaryDirectory(prefix=".prepare-", dir=str(downloads)) as work:
            stage = Path(work) / tag
            stage.mkdir(mode=0o755)
            fetch("https://github.com/" + REPO + "/releases/download/" + tag + "/" + name,
                  stage / name, installer_hash)
            (stage / (name + ".sha256")).write_text(checksums, encoding="ascii")
            os.chmod(stage / (name + ".sha256"), 0o644)
            stage.rename(destination)
    controlled(destination)
    require((destination / name).is_file() and not (destination / name).is_symlink() and
            digest(destination / name) == installer_hash and
            (destination / (name + ".sha256")).read_text(encoding="ascii") == checksums,
            "Existing published installer is different; preserve the release and choose a new version")
    if not site.exists():
        with tempfile.TemporaryDirectory(prefix=".prepare-", dir=str(releases)) as work:
            stage = Path(work) / commit
            stage.mkdir(mode=0o755)
            for fname in PAGES:
                fetch("https://raw.githubusercontent.com/" + REPO + "/" + commit + "/website/" + fname,
                      stage / fname, pages[fname])
            (stage / "downloads").symlink_to(downloads, target_is_directory=True)
            stage.rename(site)
    controlled(site)
    require(set(p.name for p in site.iterdir()) == set(PAGES) | {"downloads"} and
            (site / "downloads").is_symlink() and (site / "downloads").resolve() == downloads and
            all(not (site / p).is_symlink() and digest(site / p) == pages[p] for p in PAGES),
            "Static release does not match the approved manifest")
    # Re-read before the atomic change. No API service or Caddy restart is needed.
    if current.resolve() != site:
        require(current.resolve() == old, "Another release changed the current site")
        with tempfile.TemporaryDirectory(prefix=".switch-", dir=str(ROOT)) as work:
            link = Path(work) / "next"
            link.symlink_to(site, target_is_directory=True)
            os.replace(link, current)
    print("PUBLIC_SITE=" + commit + " PREVIOUS=" + previous + " INSTALLER_SHA256=" + installer_hash)

if __name__ == "__main__":
    main()
