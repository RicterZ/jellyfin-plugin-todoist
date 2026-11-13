"""Build a Jellyfin-installable flat ZIP and its repository manifest."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
from datetime import datetime, timezone
from zipfile import ZipFile, ZipInfo, ZIP_DEFLATED
import xml.etree.ElementTree as ET


def package(project: Path, output: Path, repository: str) -> tuple[Path, Path]:
    metadata = json.loads((project / "meta.json").read_text())
    version = metadata["version"]
    if version != ET.parse(project / "Jellyfin.Plugin.Todoist.csproj").findtext(".//Version"):
        raise ValueError("Project and plugin manifest versions must match")
    output.mkdir(parents=True, exist_ok=True)
    archive = output / f"Jellyfin.Plugin.Todoist_{version}.zip"
    with ZipFile(archive, "w", compression=ZIP_DEFLATED) as bundle:
        for source in (project / "bin/Release/net9.0/Jellyfin.Plugin.Todoist.dll", project / "meta.json"):
            entry = ZipInfo(source.name, date_time=(1980, 1, 1, 0, 0, 0))
            entry.compress_type = ZIP_DEFLATED
            entry.external_attr = 0o100644 << 16
            bundle.writestr(entry, source.read_bytes())
    manifest = output / "manifest.json"
    manifest.write_text(json.dumps([{
        "guid": metadata["guid"], "name": metadata["name"], "owner": metadata["owner"],
        "category": metadata["category"],
        "overview": "将 Jellyfin 剧集同步到每个用户的 Todoist 项目",
        "description": "独立 OAuth 授权、项目关联、观看后自动完成任务，以及 Jellyfin 原生后台。",
        "versions": [{
            "version": version, "targetAbi": metadata["targetAbi"],
            "changelog": (project.parent / "RELEASE_NOTES.md").read_text(encoding="utf-8"),
            "sourceUrl": f"https://github.com/{repository}/releases/download/v{version}/{archive.name}",
            # Jellyfin's installer requires MD5 for this field.
            "checksum": hashlib.md5(archive.read_bytes(), usedforsecurity=False).hexdigest(),
            "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        }],
    }], ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return archive, manifest


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", default="RicterZ/jellyfin-plugin-todoist")
    parser.add_argument("--skip-build", action="store_true")
    args = parser.parse_args()
    project = Path(__file__).resolve().parent / "Jellyfin.Plugin.Todoist"
    if not args.skip_build:
        subprocess.run([os.environ.get("DOTNET", "dotnet"), "build", str(project), "-c", "Release", "--nologo"], check=True)
    for artifact in package(project, project.parent / "dist", args.repository):
        print(artifact)


if __name__ == "__main__":
    main()
