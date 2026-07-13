"""Dependency and packaged-asset health checks for source and EXE builds."""
from __future__ import annotations
import argparse, importlib, json, sys
from pathlib import Path
from typing import Any
from .runtime_paths import resource_root

def run_checks(*, gui: bool, pipeline: bool) -> dict[str, Any]:
    checks=[]
    def check_import(name):
        try:
            module=importlib.import_module(name)
            checks.append({'name':f'import:{name}','ok':True,'version':getattr(module,'__version__',None)})
        except Exception as exc:
            checks.append({'name':f'import:{name}','ok':False,'error':f'{type(exc).__name__}: {exc}'})
    check_import('numpy'); check_import('dlanm2_gui.gui'); check_import('dlanm2_gui.unified_gui')
    if gui: check_import('PySide6')
    if pipeline:
        for name in ('dlanm2_gui.project_builder','dlanm2_gui.model_importer.msh_builder','dlanm2_gui.retarget_engines.mapped_rig'):
            check_import(name)
    root=resource_root()
    for relative in (
        'reference/player_1_tpp.smd','reference/infected_turn_90r.template.anm2',
        'reference/stock_writer_control.anm2','reference/male_npc_infected.crig',
        'docs/GUI_GUIDE.md','docs/ANM2_TO_FBX.md',
        'dlanm2_gui/blender_scripts/export_anm2_fbx.py',
    ):
        path=root/relative; checks.append({'name':f'asset:{relative}','ok':path.is_file(),'path':str(path)})
    return {'ok':all(row['ok'] for row in checks),'python':sys.version,'resource_root':str(root),'checks':checks}

def main(argv=None):
    parser=argparse.ArgumentParser(); parser.add_argument('--gui',action='store_true'); parser.add_argument('--pipeline',action='store_true'); parser.add_argument('--report',type=Path); args=parser.parse_args(argv)
    result=run_checks(gui=args.gui,pipeline=args.pipeline); text=json.dumps(result,indent=2)
    if args.report: args.report.parent.mkdir(parents=True,exist_ok=True); args.report.write_text(text+'\n',encoding='utf-8')
    print(text); return 0 if result['ok'] else 1
if __name__=='__main__': raise SystemExit(main())
