"""Validate the ZIP layout and checksum consumed by Jellyfin's installer."""
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from zipfile import ZipFile

from build_release import package


class ReleaseTests(unittest.TestCase):
    def test_installable_package(self):
        project = Path(__file__).parent / "Jellyfin.Plugin.Todoist"
        with tempfile.TemporaryDirectory() as temporary:
            archive, manifest = package(project, Path(temporary), "RicterZ/jellyfin-plugin-todoist")
            info = json.loads(manifest.read_text())[0]
            with ZipFile(archive) as bundle:
                self.assertEqual(set(bundle.namelist()), {"Jellyfin.Plugin.Todoist.dll", "meta.json"})
                metadata = json.loads(bundle.read("meta.json"))
            version = info["versions"][0]
            self.assertEqual(info["guid"], metadata["guid"])
            self.assertEqual(version["version"], metadata["version"])
            self.assertEqual(version["targetAbi"], metadata["targetAbi"])
            self.assertEqual(version["checksum"], hashlib.md5(archive.read_bytes(), usedforsecurity=False).hexdigest())
            self.assertEqual(version["sourceUrl"], f'https://github.com/RicterZ/jellyfin-plugin-todoist/releases/download/v{metadata["version"]}/{archive.name}')
            previous = archive.read_bytes()
            package(project, Path(temporary), "RicterZ/jellyfin-plugin-todoist")
            self.assertEqual(previous, archive.read_bytes())


if __name__ == "__main__":
    unittest.main()
