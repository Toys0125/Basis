#!/usr/bin/env python3
from __future__ import annotations

import shutil
import sys
from pathlib import Path


PACKAGE_DIRS_TO_REMOVE = (
    "Packages/com.basis.examples",
    "Packages/com.basis.pooltable",
)

ADDRESS_PREFIXES_TO_REMOVE = (
    "Packages/com.basis.examples/",
    "Packages/com.basis.pooltable/",
    "Packages/com.basis.tests/",
)


def normalize_project_path(path: Path, project_root: Path) -> str:
    return path.relative_to(project_root).as_posix()


def build_guid_index(project_root: Path) -> dict[str, str]:
    guid_to_asset_path: dict[str, str] = {}

    for meta_path in project_root.rglob("*.meta"):
        guid = ""
        with meta_path.open(encoding="utf-8") as handle:
            for line in handle:
                if line.startswith("guid: "):
                    guid = line.removeprefix("guid: ").strip()
                    break

        if not guid:
            continue

        asset_path = meta_path.with_suffix("")
        guid_to_asset_path[guid] = normalize_project_path(asset_path, project_root)

    return guid_to_asset_path


def should_remove_entry(address: str, guid: str, guid_to_asset_path: dict[str, str]) -> bool:
    if address.startswith(ADDRESS_PREFIXES_TO_REMOVE):
        return True

    asset_path = guid_to_asset_path.get(guid, "")
    return asset_path.startswith(PACKAGE_DIRS_TO_REMOVE)


def strip_addressable_entries(asset_path: Path, guid_to_asset_path: dict[str, str]) -> list[str]:
    lines = asset_path.read_text(encoding="utf-8").splitlines(keepends=True)
    output: list[str] = []
    removed_entries: list[str] = []
    index = 0

    while index < len(lines):
        line = lines[index]
        output.append(line)
        index += 1

        if line != "  m_SerializeEntries:\n":
            continue

        while index < len(lines) and lines[index].startswith("  - m_GUID:"):
            block_start = index
            guid = lines[index].split(":", 1)[1].strip()
            block_end = index + 1
            while block_end < len(lines):
                current = lines[block_end]
                if current.startswith("  - m_GUID:"):
                    break
                block_end += 1

            block = lines[block_start:block_end]
            address = ""
            for block_line in block:
                marker = "m_Address:"
                if marker in block_line:
                    address = block_line.split(marker, 1)[1].strip()
                    break

            if should_remove_entry(address, guid, guid_to_asset_path):
                asset_ref = guid_to_asset_path.get(guid, "<missing>")
                removed_entries.append(f"{address} [guid={guid} path={asset_ref}]")
            else:
                output.extend(block)

            index = block_end

    if removed_entries:
        asset_path.write_text("".join(output), encoding="utf-8")

    return removed_entries


def main() -> int:
    project_root = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("Basis")
    project_root = project_root.resolve()

    if not project_root.exists():
        print(f"Project root not found: {project_root}", file=sys.stderr)
        return 1

    guid_to_asset_path = build_guid_index(project_root)

    asset_groups_dir = project_root / "Assets/AddressableAssetsData/AssetGroups"
    removed_total = 0
    for asset_path in sorted(asset_groups_dir.glob("*.asset")):
        removed = strip_addressable_entries(asset_path, guid_to_asset_path)
        if removed:
            removed_total += len(removed)
            print(f"Removed {len(removed)} Addressables entries from {asset_path}")
            for entry in removed:
                print(f"  - {entry}")

    for relative_dir in PACKAGE_DIRS_TO_REMOVE:
        package_dir = project_root / relative_dir
        if package_dir.exists():
            shutil.rmtree(package_dir)
            print(f"Removed package directory: {package_dir}")
        else:
            print(f"Package directory already absent: {package_dir}")

    if removed_total == 0:
        print("No sample/test Addressables entries needed removal.")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
