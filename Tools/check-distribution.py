#!/usr/bin/env python3
"""Checks what a person downloads or runs to get Claude Graft, fetched from
where they get it rather than from this checkout.

    Tools/check-distribution.py              what is published now
    Tools/check-distribution.py --tag v2.2.0 a release staged for publishing

Staged means: the tag's GitHub release is a prerelease carrying the Mac zip
and disk image, the Windows setup and packages, and the new feed as
appcast-staged.xml; the tap has the new cask on a branch named release-2.2.0.
A prerelease is offered by neither updater and is not the site's latest, so all
of it can be checked, and installed on a clean machine, before anyone gets it.

Needs Python 3 and the cryptography package, for the update signatures.
GITHUB_TOKEN, if set, is used for the GitHub API.
"""

import argparse
import base64
import hashlib
import json
import os
import plistlib
import re
import sys
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
REPO = "aaditya-v-more/claude-graft"
SPARKLE = "{http://www.andymatuschak.org/xml-namespaces/sparkle}"
# Where people are told to look; each is read for links and commands.
PAGES = ["README.md", "windows/README.md", "docs/index.html"]

failures = []


def fail(message):
    failures.append(message)
    print(f"FAIL {message}")


def ok(message):
    print(f"ok   {message}")


def request(url, headers=None):
    headers = dict(headers or {})
    headers.setdefault("User-Agent", "claude-graft-distribution-check")
    if url.startswith("https://api.github.com/") and os.environ.get("GITHUB_TOKEN"):
        headers["Authorization"] = "Bearer " + os.environ["GITHUB_TOKEN"]
    return urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=120)


def fetch(url):
    with request(url) as response:
        return response.read()


def reachable(url):
    """A link a person can follow: asked for one byte, since a release asset
    is a hundred megabytes and only whether it is there matters."""
    try:
        with request(url, {"Range": "bytes=0-0"}) as response:
            return response.status in (200, 206)
    except Exception:
        return False


def api(path):
    return json.loads(fetch("https://api.github.com/" + path))


def version_key(text):
    return tuple(int(part) if part.isdigit() else 0 for part in re.split(r"[.\-]", text))


# MARK: - Pages

def check_pages():
    """Every release link on the README and the site opens, and every install
    command names a cask that exists."""
    links, commands = set(), set()
    for page in PAGES:
        text = (ROOT / page).read_text(encoding="utf-8")
        links.update(re.findall(rf"https://github\.com/{REPO}/releases[^\s\"'<>)\]]*", text))
        commands.update(re.findall(r"brew install --cask ([^\s<`\"']+)", text))
    for link in sorted(links):
        (ok if reachable(link) else fail)(f"link opens: {link}")
    if not commands:
        fail("no brew install command found on the README or the site")
    for token in sorted(commands):
        parts = token.split("/")
        if len(parts) != 3:
            fail(f"brew install --cask {token} names no tap")
            continue
        user, repo, cask = parts
        url = f"https://raw.githubusercontent.com/{user}/homebrew-{repo}/HEAD/Casks/{cask}.rb"
        try:
            body = fetch(url).decode()
            (ok if f'cask "{cask}" do' in body else fail)(f"brew install --cask {token} finds its cask")
        except Exception as error:
            fail(f"brew install --cask {token}: {url} does not open ({error})")


def check_pages_current(mac, windows_tag):
    """A download pinned to a version names the one people should get: the
    disk image the feed offers, and the newest release that carries Windows.
    2.2.1 shipped for the Mac alone, so the Windows links were pinned to 2.2.0
    and must move on with the next release that carries Windows."""
    for page in PAGES:
        text = (ROOT / page).read_text(encoding="utf-8")
        for version in sorted(set(re.findall(r"/releases/download/v([0-9.]+)/ClaudeGraft-[0-9.]+\.dmg", text))):
            if version == mac:
                ok(f"{page} hands out the {version} disk image the feed offers")
            else:
                fail(f"{page} hands out the {version} disk image while the feed offers {mac}")
        for version in sorted(set(re.findall(r"/releases/download/v([0-9.]+)/ClaudeGraftApp-win-Setup\.exe", text))):
            if f"v{version}" == windows_tag:
                ok(f"{page} hands out the newest Windows setup, {version}")
            else:
                fail(f"{page} hands out the {version} Windows setup while the newest is {windows_tag}")


# MARK: - Mac

def public_key():
    with open(ROOT / "Resources/Info.plist", "rb") as handle:
        info = plistlib.load(handle)
    return info["SUFeedURL"], base64.b64decode(info["SUPublicEDKey"])


def check_signature(data, signature, key, what):
    from cryptography.exceptions import InvalidSignature
    from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PublicKey
    try:
        Ed25519PublicKey.from_public_bytes(key).verify(base64.b64decode(signature), data)
        ok(f"{what} verifies against the key every installed copy carries")
    except InvalidSignature:
        fail(f"{what} does not verify against SUPublicEDKey; every Mac would refuse it")


def check_appcast(feed_bytes, expect):
    """The feed Sparkle reads: every entry's download opens, and the newest is
    the archive it describes, signed with the key installed copies trust."""
    _, key = public_key()
    items = ET.fromstring(feed_bytes).findall("./channel/item")
    if not items:
        fail("the appcast has no entries")
        return None

    def short(item):
        return item.findtext(f"{SPARKLE}shortVersionString") or item.find("enclosure").get(f"{SPARKLE}shortVersionString", "")

    for item in items:
        url = item.find("enclosure").get("url")
        (ok if reachable(url) else fail)(f"appcast {short(item)} download opens")
    newest = max(items, key=lambda item: version_key(short(item)))
    version = short(newest)
    if expect and version != expect:
        fail(f"the appcast's newest entry is {version}, not {expect}")
    enclosure = newest.find("enclosure")
    archive = fetch(enclosure.get("url"))
    if str(len(archive)) == enclosure.get("length"):
        ok(f"appcast {version} describes the {len(archive)}-byte zip it links")
    else:
        fail(f"appcast {version} says {enclosure.get('length')} bytes, the zip is {len(archive)}")
    signature = enclosure.get(f"{SPARKLE}edSignature")
    if signature:
        check_signature(archive, signature, key, f"appcast {version} zip")
    else:
        fail(f"appcast {version} carries no signature")
    if newest.findtext(f"{SPARKLE}minimumAutoupdateVersion") != "1.1.0":
        fail(f"appcast {version} does not ask a 1.0.x Mac first")
    return version


def check_cask(body, expect):
    """The cask brew installs: its disk image opens and is the one it names."""
    version = re.search(r'^\s*version "([^"]+)"', body, re.M).group(1)
    sha = re.search(r'^\s*sha256 "([^"]+)"', body, re.M).group(1)
    url = re.search(r'^\s*url "([^"]+)"', body, re.M).group(1).replace("#{version}", version)
    if expect and version != expect:
        fail(f"the cask installs {version}, not {expect}")
    try:
        image = fetch(url)
    except Exception as error:
        fail(f"cask {version} disk image does not open: {url} ({error})")
        return version
    (ok if hashlib.sha256(image).hexdigest() == sha else fail)(f"cask {version} disk image matches its sha256")
    return version


# MARK: - Windows

def check_windows(release, expect):
    """What an installed Windows Graft reads: the release's feed, the package
    it names, and the setup a new install starts from."""
    assets = {asset["name"]: asset["browser_download_url"] for asset in release["assets"]}
    tag = release["tag_name"]
    if "ClaudeGraftApp-win-Setup.exe" not in assets or not reachable(assets["ClaudeGraftApp-win-Setup.exe"]):
        fail(f"{tag} has no setup a new install can start from")
    else:
        ok(f"{tag} setup opens")
    if "releases.win.json" not in assets:
        fail(f"{tag} has no releases.win.json, so no installed copy is offered it")
        return
    feed = json.loads(fetch(assets["releases.win.json"]))
    full = [entry for entry in feed["Assets"] if entry["Type"] == "Full"]
    newest = max(full, key=lambda entry: version_key(entry["Version"]))
    if expect and newest["Version"] != expect:
        fail(f"{tag}'s Windows feed offers {newest['Version']}, not {expect}")
    if newest["FileName"] not in assets:
        fail(f"{tag}'s Windows feed names {newest['FileName']}, which the release does not carry")
        return
    package = fetch(assets[newest["FileName"]])
    good = len(package) == newest["Size"] and hashlib.sha256(package).hexdigest().lower() == newest["SHA256"].lower()
    (ok if good else fail)(f"{tag} Windows package {newest['Version']} matches its size and SHA256")


def published_windows_release():
    """The one Velopack's GitHub source picks: the newest release, neither
    draft nor prerelease, that carries a Windows feed."""
    for release in api(f"repos/{REPO}/releases?per_page=30"):
        if release["draft"] or release["prerelease"]:
            continue
        if any(asset["name"] == "releases.win.json" for asset in release["assets"]):
            return release
    return None


# MARK: -

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--tag", help="check this staged prerelease instead of what is published")
    args = parser.parse_args()

    check_pages()

    feed_url, _ = public_key()
    if args.tag:
        version = args.tag.removeprefix("v")
        release = api(f"repos/{REPO}/releases/tags/{args.tag}")
        if not release["prerelease"]:
            fail(f"{args.tag} is already published, not staged")
        assets = {asset["name"]: asset["browser_download_url"] for asset in release["assets"]}
        if "appcast-staged.xml" in assets:
            check_appcast(fetch(assets["appcast-staged.xml"]), version)
        else:
            fail(f"{args.tag} carries no appcast-staged.xml")
        cask_url = f"https://raw.githubusercontent.com/aaditya-v-more/homebrew-claude-graft/release-{version}/Casks/claude-graft.rb"
        check_cask(fetch(cask_url).decode(), version)
        check_windows(release, version)
    else:
        mac = check_appcast(fetch(feed_url), None)
        cask = check_cask(fetch("https://raw.githubusercontent.com/aaditya-v-more/homebrew-claude-graft/HEAD/Casks/claude-graft.rb").decode(), None)
        # brew and Sparkle hand out the same release, or a brew install is
        # straight away offered an update, or offered nothing it should be.
        if mac and cask and mac != cask:
            fail(f"the appcast offers {mac} while the cask installs {cask}")
        release = published_windows_release()
        if release:
            check_windows(release, None)
        else:
            fail("no published release carries a Windows feed")
        check_pages_current(mac, release["tag_name"] if release else None)

    print()
    print(f"{len(failures)} problem(s)" if failures else "everything a person downloads checks out")
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    main()
