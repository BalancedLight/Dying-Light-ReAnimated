"""Versioned generic source-to-target bone maps for reverse conversion."""
from __future__ import annotations
from dataclasses import asdict, dataclass, field
import json, os, re, tempfile, uuid
from pathlib import Path
from typing import Any, Iterable, Mapping
from .chrome_rig import ChromeRig
from .trackmap import dl_name_hash
BONE_MAP_FORMAT="dl-reanimated-bone-map"; BONE_MAP_SCHEMA_VERSION=1; BONE_MAP_EXTENSION=".dlrbmap.json"
def normalize_bone_name(value:str)->str:
 value=value.rsplit(":",1)[-1]; value=re.sub(r"([a-z0-9])([A-Z])",r"\1_\2",value); value=re.sub(r"[^a-zA-Z0-9]+","_",value).strip("_").lower()
 for old,new in {"left":"l","right":"r","upperarm":"upper_arm","lowerarm":"forearm","lower_arm":"forearm","upperleg":"thigh","lowerleg":"calf"}.items(): value=value.replace(old,new)
 return re.sub(r"_+","_",value)
@dataclass(slots=True)
class BoneMapPair:
 source_descriptor:int; source_bone:str; target_bone:str; confidence:float=1.0; method:str="manual"
@dataclass(slots=True)
class GenericBoneMap:
 profile_id:str; name:str; source_skeleton_hash:str; target_skeleton_hash:str; source_rig_ref:str=""; pairs:list[BoneMapPair]=field(default_factory=list); extensions:dict[str,Any]=field(default_factory=dict); format:str=BONE_MAP_FORMAT; schema_version:int=BONE_MAP_SCHEMA_VERSION
 @classmethod
 def create(cls,name,source_hash,target_hash,*,source_rig_ref=""): return cls(str(uuid.uuid4()),name,source_hash,target_hash,source_rig_ref)
 def validate(self):
  errors=[]
  if self.format!=BONE_MAP_FORMAT or self.schema_version!=BONE_MAP_SCHEMA_VERSION: errors.append("Unsupported generic bone-map format or schema version.")
  targets=[r.target_bone for r in self.pairs if r.target_bone]; descriptors=[r.source_descriptor for r in self.pairs]
  if len(targets)!=len(set(targets)): errors.append("A target bone may only be assigned once.")
  if len(descriptors)!=len(set(descriptors)): errors.append("A source descriptor may only be mapped once.")
  return errors
 def to_dict(self): return asdict(self)
 @classmethod
 def from_dict(cls,payload):
  if payload.get("format")!=BONE_MAP_FORMAT: raise ValueError("Not a DL ReAnimated generic bone-map file.")
  if int(payload.get("schema_version",0))!=BONE_MAP_SCHEMA_VERSION: raise ValueError("Unsupported generic bone-map schema version.")
  allowed=set(cls.__dataclass_fields__); unknown={k:v for k,v in payload.items() if k not in allowed}; ext=dict(payload.get("extensions",{}));
  if unknown: ext.setdefault("unknown_fields",{}).update(unknown)
  result=cls(str(payload.get("profile_id") or uuid.uuid4()),str(payload.get("name","Generic bone map")),str(payload.get("source_skeleton_hash","")),str(payload.get("target_skeleton_hash","")),str(payload.get("source_rig_ref","")),[BoneMapPair(**dict(r)) for r in payload.get("pairs",[])],ext)
  errors=result.validate()
  if errors: raise ValueError("Invalid generic bone map:\n- "+"\n- ".join(errors))
  return result
 def save(self,path):
  d=Path(path)
  if not d.name.lower().endswith(BONE_MAP_EXTENSION): d=d.with_name(d.name+BONE_MAP_EXTENSION)
  d.parent.mkdir(parents=True,exist_ok=True); text=json.dumps(self.to_dict(),indent=2,ensure_ascii=False)+"\n"; h,tmp=tempfile.mkstemp(prefix=d.name+".",suffix=".tmp",dir=d.parent)
  try:
   with os.fdopen(h,"w",encoding="utf-8",newline="\n") as s: s.write(text); s.flush(); os.fsync(s.fileno())
   os.replace(tmp,d)
  finally:
   if os.path.exists(tmp): os.unlink(tmp)
  return d
 @classmethod
 def load(cls,path): return cls.from_dict(json.loads(Path(path).read_text(encoding="utf-8")))
def skeleton_signature(rows:Iterable[tuple[str,str|None]])->str:
 import hashlib
 return hashlib.sha256("\n".join(f"{n}|{p or ''}" for n,p in rows).encode()).hexdigest()
def auto_map_skeletons(source_rig:ChromeRig,target_names:Iterable[str],target_parents:Mapping[str,str|None],*,target_skeleton_hash=""):
 names=list(target_names); normalized={n:normalize_bone_name(n) for n in names}; profile=GenericBoneMap.create(f"{source_rig.name} to target skeleton",source_rig.skeleton_hash,target_skeleton_hash or skeleton_signature((n,target_parents.get(n)) for n in names),source_rig_ref=source_rig.rig_id); used=set()
 def parent_name(b): return source_rig.bones[b.parent_index].name if b.parent_index>=0 else None
 for bone in source_rig.bones:
  candidates=[]; source_normal=normalize_bone_name(bone.name); aliases={source_normal,*(normalize_bone_name(v) for v in bone.aliases)}; sp=parent_name(bone); mp=next((r.target_bone for r in profile.pairs if r.source_bone==sp),None)
  for target in names:
   if target in used: continue
   tn=normalized[target]; score=0.; method="heuristic"
   if dl_name_hash(target)==bone.descriptor: score,method=1.,"descriptor"
   elif target==bone.name: score,method=.99,"exact"
   elif tn in aliases: score,method=.96,"normalized"
   elif any(tn.endswith("_"+a) or a.endswith("_"+tn) for a in aliases if a): score,method=.86,"normalized_suffix"
   else:
    overlap=len(set(source_normal.split("_"))&set(tn.split("_"))); score=.35+min(.35,overlap*.12) if overlap else 0.
    if mp and target_parents.get(target)==mp: score+=.18
    if sum(1 for r in source_rig.bones if r.parent_index==bone.index)==sum(1 for r in names if target_parents.get(r)==target): score+=.08
   candidates.append((min(score,1.),target,method))
  candidates.sort(reverse=True)
  if not candidates: continue
  best=candidates[0]; runner=candidates[1][0] if len(candidates)>1 else 0.
  if best[0]>=.85 or (best[0]>=.68 and best[0]-runner>=.12): profile.pairs.append(BoneMapPair(bone.descriptor,bone.name,best[1],best[0],best[2])); used.add(best[1])
 return profile
__all__=["BONE_MAP_EXTENSION","BONE_MAP_FORMAT","BONE_MAP_SCHEMA_VERSION","BoneMapPair","GenericBoneMap","auto_map_skeletons","normalize_bone_name","skeleton_signature"]
