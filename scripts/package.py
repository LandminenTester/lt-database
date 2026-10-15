"""Package only public runtime files; never include local VFS credentials."""
import hashlib
from pathlib import Path
import tomllib
import zipfile

root = Path(__file__).resolve().parents[1]
version = tomllib.loads((root / "data/config.toml").read_text(encoding="utf-8"))["General"]["version"]
wasm = root / "dist/sv.wasm"
if wasm.read_bytes()[:8] not in (b"\x00asm\x01\x00\x00\x00", b"\x00asm\x0d\x00\x01\x00"):
    raise SystemExit("dist/sv.wasm is not a WebAssembly module/component; run the Release WASM build first")
out = root / "release"
out.mkdir(exist_ok=True)
archive = out / f"lt_database-{version}.zip"
with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as z:
    for src, dest in [
        ("data/config.toml", "config.toml"), ("dist/sv.wasm", "sv.wasm"),
        ("vfs/database.example.json", "vfs/database.example.json"),
        ("README.md", "README.md"), ("LICENSE", "LICENSE"), ("NOTICE.md", "NOTICE.md"),
    ]:
        z.write(root / src, dest)
    for folder in ("docs", "import"):
        for file in sorted((root / folder).rglob("*")):
            if file.is_file():
                z.write(file, file.relative_to(root).as_posix())
digest = hashlib.sha256(archive.read_bytes()).hexdigest()
(out / "SHA256SUMS.txt").write_text(f"{digest}  {archive.name}\n", encoding="utf-8")
print(f"Packaged {archive.name} ({archive.stat().st_size} bytes), SHA256 {digest}")
