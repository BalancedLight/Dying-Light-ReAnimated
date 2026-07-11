# PyInstaller specification for the Windows one-folder GUI build.
from pathlib import Path

root = Path(SPECPATH).resolve()

datas = []
for directory in ("reference", "docs", "examples"):
    source = root / directory
    if source.exists():
        datas.append((str(source), directory))
blender_helpers = root / "dlanm2_gui" / "blender_scripts"
if blender_helpers.exists():
    datas.append((str(blender_helpers / "export_anm2_fbx.py"), "dlanm2_gui/blender_scripts"))
for filename in (
    "README.md",
    "START_HERE.txt",
    "common_anims_sp_pc.rpack",
    "common_anims_sp_pc.rpack.dlrmanifest.json",
):
    source = root / filename
    if source.exists():
        datas.append((str(source), "."))

hiddenimports = [
    "dlanm2_gui.oracle.custom_fbx_release_candidate_editor_rpack",
]

a = Analysis(
    [str(root / "dl_reanimated_gui.py")],
    pathex=[str(root)],
    binaries=[],
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=["tkinter"],
    noarchive=False,
    optimize=1,
)
pyz = PYZ(a.pure)
exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name="DL-ReAnimated",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)
coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    strip=False,
    upx=True,
    upx_exclude=[],
    name="DL-ReAnimated",
)
