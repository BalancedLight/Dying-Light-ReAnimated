from __future__ import annotations
from dataclasses import dataclass,asdict,field
from pathlib import Path
import hashlib,json
def sha256_bytes(b): return hashlib.sha256(b).hexdigest().upper()
def manifest_path_for_pack(p): return Path(str(p)+'.dlrmanifest.json')
@dataclass(slots=True)
class PackResourceManifest:
 resource_name:str; script_resource:str; source_fbx:str; root_policy:str; frame_count:int; fps:int; sha256:str; mapping_profile_id:str=''; ik_preset:str='runtime'; extensions:dict=field(default_factory=dict)
@dataclass(slots=True)
class PackManifest:
 pack_name:str; pack_sha256:str; project_id:str; animation_resources:list[PackResourceManifest]; animation_scripts:list[str]; build_mode:str='new'; extensions:dict=field(default_factory=dict)
 def save_for_pack(self,p): q=manifest_path_for_pack(p); q.write_text(json.dumps(asdict(self),indent=2)+'\n'); return q
 @classmethod
 def load_for_pack(cls,p):
  q=manifest_path_for_pack(p)
  if not q.exists():return None
  d=json.loads(q.read_text()); d['animation_resources']=[PackResourceManifest(**x) for x in d.get('animation_resources',[])]; return cls(**d)
 def verify_pack_hash(self,p): return sha256_bytes(Path(p).read_bytes())==self.pack_sha256
