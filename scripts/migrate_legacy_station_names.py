#!/usr/bin/env python3
"""One-shot OFFLINE migration for Factions station display names.

This replaces any legacy "<former-tag> Outpost" value with a deterministic catalogue
name for the brain's current owner, and removes the current owner tag from existing
"<TAG> <Name>" display names. It only changes SourceDoc.DisplayName values.
Use while Torch/SE is fully stopped and only with a fresh backup directory; the host
startup reconciliation then applies those persisted names to MyFactionStation records
and materialized grids.

Example (from the live Instance/Factions directory):
  python3 migrate_legacy_station_names.py \
    --i-understand-server-must-be-offline \
    --brains-dir brains \
    --index index.json \
    --catalogue station_name_catalogue.json \
    --backup-dir station-name-migration-backup
"""
import argparse
import hashlib
import json
import re
import shutil
import sys
from pathlib import Path

OWNER_PREFIX_RE = re.compile(r"^([A-Za-z0-9]{4})\s+(.+)$")


def stable_index(value, count):
    digest = hashlib.sha256(value.encode("utf-8")).digest()
    return int.from_bytes(digest[:8], "big") % count


def station_type(source, fallback):
    prefab = str(source.get("StationPrefabId") or "")
    if "outpost" in prefab.lower():
        return "Outposts"
    return fallback


def without_known_owner_prefix(display_name, known_owner_tags):
    match = OWNER_PREFIX_RE.fullmatch(display_name)
    if match and match.group(1).upper() in known_owner_tags:
        return match.group(2)
    return display_name


def is_legacy_default(display_name, known_owner_tags):
    match = OWNER_PREFIX_RE.fullmatch(display_name)
    return bool(match and match.group(1).upper() in known_owner_tags
                and match.group(2).lower() == "outpost")


def is_complete_station_name(display_name):
    return bool(display_name and len(display_name.strip().split()) >= 2)


def is_physical_station(source):
    return bool(source.get("StableStationId") and source.get("StationPrefabId"))


def select_complete_name(tag, source_id, personality_id, category, names, used):
    pool = names.get((personality_id, category))
    if not pool:
        # Match runtime behaviour: factorum and any unmapped personality use Military/default.
        personality_id = "default"
        pool = names.get((personality_id, category), [])
    candidates = [name for name in pool
                  if is_complete_station_name(name) and name not in used.setdefault(tag, set())]
    if not candidates:
        return None, personality_id
    return candidates[stable_index(tag + "|" + str(source_id or ""), len(candidates))], personality_id


def main():
    parser = argparse.ArgumentParser(description="Offline-only migration for legacy '<TAG> Outpost' brain display names.")
    parser.add_argument("--i-understand-server-must-be-offline", action="store_true", help="required acknowledgement")
    parser.add_argument("--brains-dir", required=True, type=Path)
    parser.add_argument("--index", required=True, type=Path)
    parser.add_argument("--catalogue", required=True, type=Path)
    parser.add_argument("--backup-dir", required=True, type=Path, help="must not exist; receives byte-for-byte brain backups")
    parser.add_argument("--fallback-station-type", choices=["Outposts", "OrbitalStations", "SpaceStations"], default="SpaceStations",
                        help="category for sources whose prefab cannot identify an outpost (default: SpaceStations)")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    if not args.i_understand_server_must_be_offline:
        parser.error("refusing to run without --i-understand-server-must-be-offline")
    if not args.brains_dir.is_dir() or not args.index.is_file() or not args.catalogue.is_file():
        parser.error("brains directory, index, and generated catalogue must exist")
    if args.backup_dir.exists():
        parser.error("backup directory already exists; use a NEW directory for this one-shot run")

    index = json.loads(args.index.read_text(encoding="utf-8"))
    personality_by_tag = {entry.get("Tag"): entry.get("PersonalityId") or "default" for entry in index if entry.get("Tag")}
    known_owner_tags = {tag.upper() for tag in personality_by_tag}
    catalogue = json.loads(args.catalogue.read_text(encoding="utf-8"))
    names = {}
    for entry in catalogue.get("Entries", []):
        key = (entry.get("PersonalityId", "default").lower(), entry.get("StationType"))
        names[key] = [n.get("Name") for n in entry.get("Names", []) if n.get("Name")]

    used = {tag: set() for tag in personality_by_tag}
    docs = []
    for brain_path in sorted(args.brains_dir.glob("*.json")):
        doc = json.loads(brain_path.read_text(encoding="utf-8"))
        tag = doc.get("Tag") or brain_path.stem
        for source in doc.get("Sources") or []:
            display = source.get("DisplayName")
            if display:
                used.setdefault(tag, set()).add(without_known_owner_prefix(display, known_owner_tags))
        docs.append((brain_path, tag, doc))

    pending = []
    for brain_path, tag, doc in docs:
        for source in doc.get("Sources") or []:
            original = source.get("DisplayName")
            selected = original
            if original and is_legacy_default(original, known_owner_tags):
                selected = None
            elif original:
                selected = without_known_owner_prefix(original, known_owner_tags)

            # Give every persisted physical station a complete catalogue name. N### is an
            # implementation ID, not a useful dashboard/station label once the node has a
            # stable station entity and prefab identity.
            if is_physical_station(source) and not is_complete_station_name(selected):
                category = station_type(source, args.fallback_station_type)
                personality = personality_by_tag.get(tag, "default").lower()
                selected, resolved_personality = select_complete_name(
                    tag, source.get("Id"), personality, category, names, used)
                if not selected:
                    print("SKIP {}:{}: no unused complete {} names for personality {}".format(
                        brain_path.name, source.get("Id"), category, resolved_personality))
                    continue

            if selected is None:
                continue
            if selected != original:
                source["DisplayName"] = selected
                used.setdefault(tag, set()).add(selected)
                pending.append((brain_path, source.get("Id"), selected))

    if not pending:
        print("No legacy or owner-tagged SourceDoc.DisplayName values found; nothing changed.")
        return 0
    for path, source_id, replacement in pending:
        print("MIGRATE {}:{} -> {}".format(path.name, source_id, replacement))
    if args.dry_run:
        print("Dry run only: no files changed.")
        return 0

    args.backup_dir.mkdir(parents=True)
    changed_paths = {path for path, _, _ in pending}
    for path in changed_paths:
        shutil.copy2(path, args.backup_dir / path.name)
    for path, _, doc in docs:
        if path in changed_paths:
            path.write_text(json.dumps(doc, indent=2) + "\n", encoding="utf-8")
    print("Migrated {} display name(s) across {} brain file(s). Backups: {}".format(
        len(pending), len(changed_paths), args.backup_dir))
    return 0


if __name__ == "__main__":
    sys.exit(main())
