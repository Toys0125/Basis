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


def strip_addressable_entries(asset_path: Path) -> list[str]:
    lines = asset_path.read_text(encoding="utf-8").splitlines(keepends=True)
    output: list[str] = []
    removed_addresses: list[str] = []
    index = 0

    while index < len(lines):
        line = lines[index]
        output.append(line)
        index += 1

        if line != "  m_SerializeEntries:\n":
            continue

        while index < len(lines) and lines[index].startswith("  - m_GUID:"):
            block_start = index
            block_end = index + 1
            while block_end < len(lines):
                current = lines[block_end]
                if current.startswith("  - m_GUID:") or current.startswith("  m_ReadOnly:"):
                    break
                block_end += 1

            block = lines[block_start:block_end]
            address = ""
            for block_line in block:
                marker = "m_Address:"
                if marker in block_line:
                    address = block_line.split(marker, 1)[1].strip()
                    break

            if address.startswith(ADDRESS_PREFIXES_TO_REMOVE):
                removed_addresses.append(address)
            else:
                output.extend(block)

            index = block_end

    if removed_addresses:
        asset_path.write_text("".join(output), encoding="utf-8")

    return removed_addresses


def main() -> int:
    project_root = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("Basis")
    project_root = project_root.resolve()

    if not project_root.exists():
        print(f"Project root not found: {project_root}", file=sys.stderr)
        return 1

    for relative_dir in PACKAGE_DIRS_TO_REMOVE:
        package_dir = project_root / relative_dir
        if package_dir.exists():
            shutil.rmtree(package_dir)
            print(f"Removed package directory: {package_dir}")
        else:
            print(f"Package directory already absent: {package_dir}")

    asset_groups_dir = project_root / "Assets/AddressableAssetsData/AssetGroups"
    removed_total = 0
    for asset_path in sorted(asset_groups_dir.glob("*.asset")):
        removed = strip_addressable_entries(asset_path)
        if removed:
            removed_total += len(removed)
            print(f"Removed {len(removed)} Addressables entries from {asset_path}")
            for address in removed:
                print(f"  - {address}")

    if removed_total == 0:
        print("No sample/test Addressables entries needed removal.")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
