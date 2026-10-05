#!/usr/bin/env python3
"""Adds the release built in dist/ to appcast.xml, the feed Clawd's updater reads, and signs it.

    ./Scripts/appcast.py                  after ./build.sh --notarize --dmg
    ./Scripts/appcast.py --notes FILE     release notes in Markdown (default: the GitHub release's)

Signing uses the EdDSA key generate_keys stored in this Mac's keychain (account clawd-for-orca):
the disk image, then the feed itself, which Clawd requires (SURequireSignedFeed). Edit the feed
only through this script; a hand edit breaks the signature and with it every update.

Commit and push appcast.xml after the GitHub release is published: Clawd downloads the disk image
from the release, and a draft's files can't be downloaded.
"""
import argparse
import email.utils
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

MACOS = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REPO = "joonseo1227/clawd-for-orca"
KEY_ACCOUNT = "clawd-for-orca"
SPARKLE = "http://www.andymatuschak.org/xml-namespaces/sparkle"
SIGN_UPDATE = os.path.join(MACOS, ".build/artifacts/sparkle/Sparkle/bin/sign_update")


def die(message):
    sys.exit(f"error: {message}")


def setting(name):
    """A product setting from build.sh, so names and the minimum macOS live in one place."""
    with open(os.path.join(MACOS, "build.sh"), encoding="utf-8") as f:
        match = re.search(rf'^{name}="([^"]*)"', f.read(), re.MULTILINE)
    if not match:
        die(f"{name} not found in build.sh")
    return match.group(1)


def sign_update(*args):
    result = subprocess.run([SIGN_UPDATE, "--account", KEY_ACCOUNT, *args], capture_output=True, text=True)
    if result.returncode != 0:
        die(f"sign_update {' '.join(args)} failed: {result.stderr.strip() or result.stdout.strip()}")
    return result.stdout


def github_notes(tag):
    """The release's notes on GitHub, or None when gh can't find the release."""
    try:
        result = subprocess.run(["gh", "release", "view", tag, "--repo", REPO, "--json", "body", "-q", ".body"],
                                capture_output=True, text=True)
    except FileNotFoundError:
        return None
    if result.returncode != 0:
        return None
    return result.stdout.strip() or None


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--notes", help="release notes in Markdown (default: the GitHub release's notes)")
    parser.add_argument("--output", default=os.path.join(MACOS, "appcast.xml"), help="the feed to update")
    parser.add_argument("--url-prefix", help="where the disk image is downloaded from, for testing a local feed "
                                             "(default: the GitHub release)")
    args = parser.parse_args()

    if not os.access(SIGN_UPDATE, os.X_OK):
        die(f"{SIGN_UPDATE} not found; run ./build.sh first, which fetches Sparkle")
    with open(os.path.join(MACOS, "VERSION"), encoding="utf-8") as f:
        version = f.read().strip()
    tag = f"macos-v{version}"
    name = f"{setting('APP_NAME')}-{version}-macOS.dmg"
    dmg = os.path.join(MACOS, "dist", name)
    if not os.path.isfile(dmg):
        die(f"{dmg} not found; run ./build.sh --notarize --dmg first")

    enclosure = dict(re.findall(r'(sparkle:edSignature|length)="([^"]*)"', sign_update(dmg)))
    if set(enclosure) != {"sparkle:edSignature", "length"}:
        die("sign_update printed no signature for the disk image")

    if args.notes:
        with open(args.notes, encoding="utf-8") as f:
            notes = f.read().strip() or None
    else:
        notes = github_notes(tag)
        print(f"release notes: {'from the GitHub release' if notes else 'none (no GitHub release notes found)'}")

    ET.register_namespace("sparkle", SPARKLE)
    if os.path.exists(args.output):
        # The parser drops comments, and with them the old signature.
        tree = ET.parse(args.output)
        channel = tree.getroot().find("channel")
    else:
        rss = ET.Element("rss", {"version": "2.0"})
        channel = ET.SubElement(rss, "channel")
        ET.SubElement(channel, "title").text = "Clawd for macOS"
        ET.SubElement(channel, "link").text = f"https://github.com/{REPO}"
        ET.SubElement(channel, "description").text = "Clawd's macOS releases"
        ET.SubElement(channel, "language").text = "en"
        tree = ET.ElementTree(rss)

    # Rebuilding a version replaces its entry.
    for old in channel.findall("item"):
        if old.findtext(f"{{{SPARKLE}}}version") == version:
            channel.remove(old)

    item = ET.Element("item")
    ET.SubElement(item, "title").text = f"Clawd {version}"
    ET.SubElement(item, "pubDate").text = email.utils.formatdate(usegmt=True)
    ET.SubElement(item, f"{{{SPARKLE}}}version").text = version
    ET.SubElement(item, f"{{{SPARKLE}}}shortVersionString").text = version
    ET.SubElement(item, f"{{{SPARKLE}}}minimumSystemVersion").text = setting("MIN_MACOS")
    ET.SubElement(item, f"{{{SPARKLE}}}fullReleaseNotesLink").text = f"https://github.com/{REPO}/releases/tag/{tag}"
    if notes:
        ET.SubElement(item, "description", {f"{{{SPARKLE}}}format": "markdown"}).text = notes
    url = (args.url_prefix.rstrip("/") + "/" if args.url_prefix else f"https://github.com/{REPO}/releases/download/{tag}/") + name
    ET.SubElement(item, "enclosure", {
        "url": url,
        "length": enclosure["length"],
        "type": "application/octet-stream",
        f"{{{SPARKLE}}}edSignature": enclosure["sparkle:edSignature"],
    })
    # Newest first, after the channel's own fields.
    first = next((i for i, e in enumerate(channel) if e.tag == "item"), len(channel))
    channel.insert(first, item)

    ET.indent(tree, space="  ")
    tree.write(args.output, encoding="utf-8", xml_declaration=True)
    with open(args.output, "a", encoding="utf-8") as f:
        f.write("\n")
    sign_update(args.output)
    sign_update("--verify", args.output)
    shown = os.path.relpath(args.output)
    print(f"{args.output if shown.startswith('..') else shown}: Clawd {version} -> {url}")


if __name__ == "__main__":
    main()
