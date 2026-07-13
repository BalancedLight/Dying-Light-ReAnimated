from pathlib import Path
from dlanm2_gui.chrome_rig_builder import build_chrome_rig_from_smd_template
from dlanm2_gui.chrome_rig import ChromeRig
from dlanm2_gui.rp6l import build_animation_library_rpack,extract_animation_library
from dlanm2_gui.animation_scr import AnimationScrSequence,build_animation_scr_sections
from dlanm2_gui.root_mapping import read_smd_hierarchy,choose_hierarchy_root,parent_names_from_smd

def test_builtin_humanoid_crig_roundtrip(tmp_path):
 root=Path(__file__).resolve().parents[1]
 rig=build_chrome_rig_from_smd_template(root/'reference/player_1_tpp.smd',root/'reference/infected_turn_90r.template.anm2')
 assert len(rig.bones)>=60
 p=rig.save(tmp_path/'human.crig')
 loaded=ChromeRig.load(p)
 assert loaded.skeleton_hash==rig.skeleton_hash
 assert not loaded.validate().errors

def test_animation_rpack_roundtrip():
 sections=build_animation_scr_sections([AnimationScrSequence('test','test.anm2',0,1,30)])
 data=build_animation_library_rpack(animation_resources={'test':b'ANM2test'},animation_scripts={'anims_man_all_DLC60':sections})
 lib=extract_animation_library(data)
 assert lib.animations['test']==b'ANM2test'
 assert 'anims_man_all_DLC60' in lib.animation_scripts

def test_no_bip01_fallback_fixture():
 root=Path(__file__).resolve().parents[1]
 rows=read_smd_hierarchy(root/'tests/fixtures/no_bip01_target.smd')
 names=[x.name for x in rows]
 assert 'bip01' not in {x.lower() for x in names}
 assert choose_hierarchy_root(names,parent_names_from_smd(rows))=='pelvis'
