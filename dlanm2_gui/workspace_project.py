from __future__ import annotations
from dataclasses import dataclass,asdict,field,fields
from datetime import datetime,timezone
from pathlib import Path
from typing import Any,Mapping
import json,os,tempfile,uuid
from . import __version__
PROJECT_FORMAT='dl-reanimated-project'; PROJECT_EXTENSION='.dlraproj'; CURRENT_PROJECT_SCHEMA_VERSION=6

def now():return datetime.now(timezone.utc).replace(microsecond=0).isoformat()
def filtered(cls,p):return {k:v for k,v in p.items() if k in {x.name for x in fields(cls)}}
@dataclass(slots=True)
class ProjectAnimation:
 animation_id:str; source_fbx:str; display_name:str; resource_name:str; source_animation_stack:str=''; enabled:bool=True; script_target:str=''; root_policy:str='inplace'; ik_preset:str='runtime'; mapping_profile_id:str=''; fps:int=30; start_frame:int|None=None; end_frame:int|None=None; notes:str=''; tags:list[str]=field(default_factory=list); extensions:dict[str,Any]=field(default_factory=dict)
 @classmethod
 def create(cls,source_fbx,resource_name=None,animation_stack=''):
  p=Path(source_fbx); return cls(str(uuid.uuid4()),str(p),p.stem,resource_name or p.stem,animation_stack)
@dataclass(slots=True)
class RigSettings:
 target_rig_ref:str='builtin:male_npc_infected'; target_rig_path:str=''; retarget_mode:str='humanoid'; use_imported_animation_bind_pose:bool=True; source_rest_fbx:str=''; trusted_source_rest_json:str=''; canonical_smd:str='reference/player_1_tpp.smd'; target_template_anm2:str='reference/infected_turn_90r.template.anm2'; stock_writer_control_anm2:str='reference/stock_writer_control.anm2'; target_rig_name:str='Dying Light male humanoid'; extensions:dict[str,Any]=field(default_factory=dict)
@dataclass(slots=True)
class ExportSettings:
 mode:str='new'; output_directory:str='build'; pack_filename:str='common_anims_sp_pc.rpack'; existing_rpack:str=''; collision_policy:str='error'; default_script_target:str='male_npc_infected_dlc60'; custom_script_resource:str=''; resource_prefix:str='dl_reanimated'; include_validation_controls:bool=False; write_intermediate_anm2:bool=False; extensions:dict[str,Any]=field(default_factory=dict)
@dataclass(slots=True)
class Anm2ToFbxItem:
 conversion_id:str; source_anm2:str; output_name:str; source_rig_ref:str='builtin:male_npc_infected'; source_rig_path:str=''; enabled:bool=True; fps:int=30; start_frame:int|None=None; end_frame:int|None=None; extensions:dict[str,Any]=field(default_factory=dict)
 @classmethod
 def create(cls,p,output_name=None):q=Path(p);return cls(str(uuid.uuid4()),str(q),output_name or q.stem)
@dataclass(slots=True)
class Anm2ToFbxSettings:
 mode:str='native'; target_fbx:str=''; output_directory:str='build/fbx'; translation_scale:str='auto'; selected_mapping_profile_id:str=''; items:list[Anm2ToFbxItem]=field(default_factory=list); bone_mapping_profiles:dict[str,dict[str,Any]]=field(default_factory=dict); extensions:dict[str,Any]=field(default_factory=dict)
@dataclass(slots=True)
class DlReanimatedProject:
 project_id:str; name:str; created_utc:str; modified_utc:str; rig:RigSettings=field(default_factory=RigSettings); export:ExportSettings=field(default_factory=ExportSettings); animations:list[ProjectAnimation]=field(default_factory=list); mapping_profiles:dict[str,dict[str,Any]]=field(default_factory=dict); user_script_targets:list[dict[str,Any]]=field(default_factory=list); anm2_to_fbx:Anm2ToFbxSettings=field(default_factory=Anm2ToFbxSettings); notes:str=''; extensions:dict[str,Any]=field(default_factory=dict); schema_version:int=CURRENT_PROJECT_SCHEMA_VERSION; minimum_reader_version:int=1; format:str=PROJECT_FORMAT; created_with:str=__version__
 @classmethod
 def new(cls,name='Untitled Project'):t=now();return cls(str(uuid.uuid4()),name,t,t)
 def animation_by_id(self,i):return next((x for x in self.animations if x.animation_id==i),None)
 def touch(self):self.modified_utc=now();self.created_with=__version__
 def validate(self):
  e=[]
  if not self.name.strip():e.append('Project name cannot be empty.')
  if self.rig.retarget_mode not in {'humanoid','exact'}:e.append('Retarget mode must be humanoid or exact.')
  if self.rig.retarget_mode=='exact' and not self.rig.target_rig_path:e.append('Exact mode requires a .crig target.')
  if not self.export.output_directory:e.append('Choose an output folder.')
  if not self.export.pack_filename.lower().endswith('.rpack'):e.append('Pack filename must end in .rpack.')
  return e
 def to_dict(self,project_path=None):self.touch();return asdict(self)
 @classmethod
 def from_dict(cls,p):
  p=dict(p); rig=RigSettings(**filtered(RigSettings,dict(p.get('rig',{})))); exp=ExportSettings(**filtered(ExportSettings,dict(p.get('export',{})))); rev=dict(p.get('anm2_to_fbx',{})); items=[Anm2ToFbxItem(**filtered(Anm2ToFbxItem,dict(x))) for x in rev.pop('items',[])]; return cls(str(p.get('project_id') or uuid.uuid4()),str(p.get('name','Imported Project')),str(p.get('created_utc',now())),str(p.get('modified_utc',now())),rig,exp,[ProjectAnimation(**filtered(ProjectAnimation,dict(x))) for x in p.get('animations',[])],{str(k):dict(v) for k,v in dict(p.get('mapping_profiles',{})).items()},[dict(x) for x in p.get('user_script_targets',[])],Anm2ToFbxSettings(**filtered(Anm2ToFbxSettings,rev),items=items),str(p.get('notes','')),dict(p.get('extensions',{})),CURRENT_PROJECT_SCHEMA_VERSION,1,PROJECT_FORMAT,str(p.get('created_with','legacy')))
 def save(self,path):p=Path(path);p=p if p.suffix.lower()==PROJECT_EXTENSION else p.with_suffix(PROJECT_EXTENSION);p.parent.mkdir(parents=True,exist_ok=True);p.write_text(json.dumps(self.to_dict(p),indent=2)+'\n');return p
 @classmethod
 def load(cls,path):return cls.from_dict(json.loads(Path(path).read_text()))
