from __future__ import annotations
from dataclasses import dataclass,asdict,field
from pathlib import Path
import hashlib,json,shutil
from .animation_scr import AnimationScrSequence,build_animation_scr_sections
from .bone_maps import GenericBoneMap,BoneMapPair,skeleton_signature
from .chrome_rig import ChromeRig
from .chrome_rig_builder import build_chrome_rig_from_smd_template
from .oracle.binary_fbx_mixamo import _FbxDocument
from .retarget_profiles import SourceBoneMappingProfile,HUMANOID_ROLES,auto_map_source_bones
from .retarget_engines.exact_rig import build_exact_rig_anm2
from .retarget_engines.mapped_rig import build_mapped_rig_anm2
from .root_mapping import RootMappingSelection
from .rp6l import build_animation_library_rpack,extract_animation_library
from .script_targets import ScriptTargetRegistry
from .pack_manifest import PackManifest,PackResourceManifest,sha256_bytes
from .fbx_blendshapes import scan_fbx_blendshapes
from .mimic_builder import build_mimic_anm2
@dataclass(slots=True)
class BuiltAnimation:
 animation_id:str; source_fbx:str; source_animation_stack:str; resource_name:str; script_resource:str; root_policy:str; ik_preset:str; mapping_profile_id:str; frame_count:int; fps:int; page_count:int=1; page_frame_spans:list[int]=field(default_factory=list); anm2_path:str=''; sha256:str=''; retarget_report:str=''
@dataclass(slots=True)
class ProjectBuildResult:
 status:str; pack_path:str; manifest_path:str; report_path:str; build_mode:str; pack_sha256:str; animation_count:int; script_count:int; built_animations:list[BuiltAnimation]=field(default_factory=list); warnings:list[str]=field(default_factory=list)
 def to_dict(self):return asdict(self)
def _safe(v):
 import re
 return re.sub(r'_+','_',re.sub(r'[^A-Za-z0-9_]+','_',v.strip()).strip('_')).lower()
def _resource(project,row):
 b=_safe(row.resource_name); p=_safe(project.export.resource_prefix); return b if not p or b==p or b.startswith(p+'_') else p+'_'+b
def _script(project,row):
 v=row.script_target or project.export.default_script_target
 if v=='custom':v=project.export.custom_script_resource
 return ScriptTargetRegistry().resolve_resource_name(v)
def _humanoid_map(project,row,doc,rig):
 payload=project.mapping_profiles.get(row.mapping_profile_id) if row.mapping_profile_id else None
 profile=SourceBoneMappingProfile.from_dict(payload) if payload and payload.get('format')=='dl-reanimated-retarget-profile' else auto_map_source_bones(doc.limb_models,doc.parent_by_name,profile_name=f'Auto map: {row.display_name}')
 project.mapping_profiles[profile.profile_id]=profile.to_dict(); row.mapping_profile_id=profile.profile_id
 errors=profile.validate(doc.limb_models)
 if errors:raise ValueError(f'Retarget mapping for {row.display_name!r} is incomplete:\n- '+'\n- '.join(errors))
 source_hash=skeleton_signature((n,doc.parent_by_name.get(n)) for n in sorted(doc.limb_models)); gm=GenericBoneMap.create(f'{row.display_name} humanoid map',rig.skeleton_hash,source_hash,source_rig_ref=rig.rig_id); by={b.name:b for b in rig.bones}; used=set()
 for role in HUMANOID_ROLES:
  source=profile.role_to_bone.get(role.role_id); target=role.target_name
  if source and target in by and source not in used:
   gm.pairs.append(BoneMapPair(by[target].descriptor,target,source,profile.confidence_by_role.get(role.role_id,1),profile.method_by_role.get(role.role_id,'humanoid')));used.add(source)
 return gm,profile.profile_id
def build_project(project,*,progress=None):
 log=progress or (lambda m:None); errors=project.validate()
 if errors:raise ValueError('Project validation failed:\n- '+'\n- '.join(errors))
 rows=[x for x in project.animations if x.enabled]
 if not rows:raise ValueError('Project does not contain any enabled animations')
 out=Path(project.export.output_directory);out.mkdir(parents=True,exist_ok=True);pack=out/project.export.pack_filename;report_dir=out/'dl_reanimated_build';shutil.rmtree(report_dir,ignore_errors=True);(report_dir/'retarget_reports').mkdir(parents=True)
 final_anim={};final_scripts={};warnings=[]
 if project.export.mode=='append' and project.export.existing_rpack:
  lib=extract_animation_library(Path(project.export.existing_rpack).read_bytes());final_anim.update(lib.animations);final_scripts.update(lib.animation_scripts)
 if project.rig.retarget_mode=='exact':
  rp=Path(project.rig.target_rig_path)
  if not rp.is_file():raise FileNotFoundError(f'Target .crig not found: {rp}')
  rig=ChromeRig.load(rp)
 else:
  smd=Path(project.rig.canonical_smd);template=Path(project.rig.target_template_anm2)
  if not smd.is_file():raise FileNotFoundError(f'Target SMD not found: {smd}')
  if not template.is_file():raise FileNotFoundError(f'Target template ANM2 not found: {template}')
  rig=build_chrome_rig_from_smd_template(smd,template)
 sequences={};built=[];manifest=[]
 for i,row in enumerate(rows,1):
  src=Path(row.source_fbx)
  if not src.is_file():raise FileNotFoundError(f'Animation FBX not found: {src}')
  log(f'[{i}/{len(rows)}] Reading skeleton: {src.name}');doc=_FbxDocument(src,animation_stack=row.source_animation_stack or None);root=RootMappingSelection.from_animation(row)
  profile_id=''
  if project.rig.retarget_mode=='exact':
   p=project.mapping_profiles.get(row.mapping_profile_id) if row.mapping_profile_id else None
   if p and p.get('format')=='dl-reanimated-bone-map':
    result=build_mapped_rig_anm2(src,rig,GenericBoneMap.from_dict(p),fps=row.fps,animation_stack=row.source_animation_stack or None,root_mapping=root);profile_id=row.mapping_profile_id
   else:
    try:result=build_exact_rig_anm2(src,rig,fps=row.fps,animation_stack=row.source_animation_stack or None)
    except ValueError as exc:
     raise ValueError(str(exc)+'\n\nOpen Animations > Root & .crig Mapping, run Auto-map, and review the rows.') from exc
  else:
   gm,profile_id=_humanoid_map(project,row,doc,rig);result=build_mapped_rig_anm2(src,rig,gm,fps=row.fps,animation_stack=row.source_animation_stack or None,root_mapping=root);result.report['retarget_mode']='humanoid_mapped';result.report['root_policy_requested']=row.root_policy
  name=_resource(project,row);script=_script(project,row)
  mimic_settings=row.extensions.get('mimic',{}) if isinstance(row.extensions.get('mimic',{}),dict) else {}
  content_mode=str(mimic_settings.get('mode','auto'))
  scan=None
  try: scan=scan_fbx_blendshapes(src,fps=row.fps,animation_stack=row.source_animation_stack or None,document=doc)
  except Exception as face_exc: warnings.append(f'{row.display_name}: facial scan skipped: {face_exc}')
  include_mimic=content_mode in {'both','mimic_only'} or (content_mode=='auto' and scan is not None and scan.has_facial_animation)
  include_body=content_mode!='mimic_only'
  end=result.frame_count-1 if row.end_frame is None else row.end_frame;start=0 if row.start_frame is None else row.start_frame
  rp=report_dir/'retarget_reports'/f'{name}.json';rp.write_text(json.dumps(result.report,indent=2)+'\n')
  if include_body:
   if name in final_anim and project.export.collision_policy=='error':raise ValueError(f'Animation resource already exists: {name}')
   final_anim[name]=result.payload;sequences.setdefault(script,[]).append(AnimationScrSequence(name,f'{name}.anm2',float(start),float(end),float(row.fps)));sha=sha256_bytes(result.payload);built.append(BuiltAnimation(row.animation_id,str(src),row.source_animation_stack,name,script,row.root_policy,row.ik_preset,profile_id,result.frame_count,row.fps,1,[max(0,result.frame_count-1)],f'rpack:{pack.name}#_ANIMATION_/{name}',sha,str(rp)));manifest.append(PackResourceManifest(name,script,str(src),row.root_policy,result.frame_count,row.fps,sha,profile_id,row.ik_preset,{'target_rig':rig.rig_id,'content':'body'}))
  if include_mimic and scan is not None and scan.has_facial_animation:
   mapping={str(x.get('source')):str(x.get('target') or x.get('source')) for x in mimic_settings.get('mapping',[]) if isinstance(x,dict) and x.get('source')} or None
   mb=build_mimic_anm2(scan,mapping=mapping);mn=_safe(str(mimic_settings.get('resource_name') or (name+'_mimic')));final_anim[mn]=mb.payload;sequences.setdefault(script,[]).append(AnimationScrSequence(mn,f'{mn}.anm2',0.,float(mb.frame_count-1),float(row.fps)));msha=sha256_bytes(mb.payload);built.append(BuiltAnimation(row.animation_id,str(src),row.source_animation_stack,mn,script,'mimic',row.ik_preset,profile_id,mb.frame_count,row.fps,1,[max(0,mb.frame_count-1)],f'rpack:{pack.name}#_ANIMATION_/{mn}',msha,str(rp)));manifest.append(PackResourceManifest(mn,script,str(src),'mimic',mb.frame_count,row.fps,msha,profile_id,row.ik_preset,{'target_rig':rig.rig_id,'content':'mimic','mimic_report':mb.report}))
  for m in result.report.get('warnings',[]):warnings.append(f'{row.display_name}: {m}')
 for s,seq in sequences.items():final_scripts[s]=build_animation_scr_sections(seq)
 data=build_animation_library_rpack(animation_resources=sorted(final_anim.items()),animation_scripts={k:final_scripts[k] for k in sorted(final_scripts)});pack.write_bytes(data);man=PackManifest(pack.name,sha256_bytes(data),project.project_id,manifest,sorted(final_scripts),project.export.mode,{'upstream_base':'7dd5858df8b14bd286c71980cafa2924c5d3eeaa'});mp=man.save_for_pack(pack);report={'status':'ok','project_id':project.project_id,'target_rig':rig.rig_id,'retarget_mode':project.rig.retarget_mode,'animations':[asdict(x) for x in built],'warnings':warnings};rpath=report_dir/'build_report.json';rpath.write_text(json.dumps(report,indent=2)+'\n');log(f'Build complete: {pack}');return ProjectBuildResult('ok',str(pack),str(mp),str(rpath),project.export.mode,man.pack_sha256,len(final_anim),len(final_scripts),built,warnings)
def export_project_anm2_files(project,output_directory,progress=None,warning=None):
 old=(project.export.output_directory,project.export.write_intermediate_anm2);project.export.output_directory=str(output_directory);project.export.write_intermediate_anm2=True;res=build_project(project,progress=progress);lib=extract_animation_library(Path(res.pack_path).read_bytes());out=Path(output_directory);paths=[]
 for n,b in lib.animations.items():p=out/f'{n}.anm2';p.write_bytes(b);paths.append(p)
 project.export.output_directory,project.export.write_intermediate_anm2=old;return paths
