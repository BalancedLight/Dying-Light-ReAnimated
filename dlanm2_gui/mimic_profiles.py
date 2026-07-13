"""Declarative facial target profiles and conservative blendshape mapping."""
from __future__ import annotations
from dataclasses import asdict, dataclass
import json, re
from pathlib import Path
from typing import Any, Iterable, Mapping
from .trackmap import dl_name_hash
BUILTIN_COMMON46_REF='builtin:human_common46'
@dataclass(frozen=True,slots=True)
class MimicTarget:
    name:str; descriptor:int; display_name:str=''; aliases:tuple[str,...]=()
    @classmethod
    def create(cls,name,*,display_name='',aliases=()):return cls(name,dl_name_hash(name),display_name or name,tuple(aliases))
@dataclass(frozen=True,slots=True)
class MimicMappingRow:
    source:str; target_descriptor:int; weight:float=1.0; bias:float=0.0; enabled:bool=True; confidence:float=1.0; method:str='manual'
    def to_dict(self):return asdict(self)
    @classmethod
    def from_dict(cls,row:Mapping[str,Any]):return cls(source=str(row['source']),target_descriptor=int(row.get('target_descriptor',row.get('target',0))),weight=float(row.get('weight',1)),bias=float(row.get('bias',0)),enabled=bool(row.get('enabled',True)),confidence=float(row.get('confidence',1)),method=str(row.get('method','manual')))
@dataclass(frozen=True,slots=True)
class MimicProfile:
    profile_id:str; name:str; targets:tuple[MimicTarget,...]; description:str=''
    def to_dict(self):return {'format':'dl-reanimated-mimic-profile','schema_version':1,'profile_id':self.profile_id,'name':self.name,'description':self.description,'targets':[asdict(x) for x in self.targets]}
    @classmethod
    def from_dict(cls,payload):
        rows=[]
        for row in payload.get('targets',[]):
            rows.append(MimicTarget(str(row['name']),int(row.get('descriptor',dl_name_hash(str(row['name'])))),str(row.get('display_name') or row['name']),tuple(row.get('aliases',()))))
        return cls(str(payload.get('profile_id','custom')),str(payload.get('name','Custom facial profile')),tuple(rows),str(payload.get('description','')))
    @classmethod
    def load(cls,path):return cls.from_dict(json.loads(Path(path).read_text(encoding='utf-8')))
COMMON_TARGET_NAMES=(
'morph_l_u_lid','morph_r_u_lid','morph_l_l_lid','morph_r_l_lid','morph_l_brow_up','morph_r_brow_up','morph_lips_L_smile','morph_lips_R_smile','morph_lips_funnel','morph_jaw_open','morph_nose','wide','w','pbm','open','fv',
)
BUILTIN_COMMON46=MimicProfile(BUILTIN_COMMON46_REF,'Human / infected facial targets',tuple(MimicTarget.create(name) for name in COMMON_TARGET_NAMES),'Named common facial controls; unknown stock descriptors can still be supplied by a custom profile.')
def _norm(value):return re.sub(r'_+','_',re.sub(r'[^a-z0-9]+','_',value.lower()).strip('_'))
def auto_map_shapes(source_names:Iterable[str],profile:MimicProfile):
    result=[]; targets={_norm(t.name):t for t in profile.targets}
    alias={_norm(a):t for t in profile.targets for a in t.aliases}
    semantic={'jawopen':'morph_jaw_open','mouthopen':'morph_jaw_open','eyeblinkleft':'morph_l_u_lid','eyeblinkright':'morph_r_u_lid','mouthsmileleft':'morph_lips_L_smile','mouthsmileright':'morph_lips_R_smile','mouthfunnel':'morph_lips_funnel'}
    by_name={t.name:t for t in profile.targets}
    for source in source_names:
        n=_norm(source); target=targets.get(n) or alias.get(n); method='exact'; confidence=1.0
        if target is None and n in semantic: target=by_name.get(semantic[n]); method='semantic'; confidence=.9
        if target is not None: result.append(MimicMappingRow(str(source),target.descriptor,confidence=confidence,method=method))
    return result
def mapping_from_payload(payload):
    result=[]
    for row in payload:
        try:result.append(MimicMappingRow.from_dict(row))
        except (KeyError,TypeError,ValueError):pass
    return result
def resolve_mimic_profile(project):
    ext=getattr(getattr(project,'rig',None),'extensions',{}) or {}; ref=str(ext.get('mimic_profile_ref','auto'))
    embedded=ext.get('mimic_profile_embedded')
    if isinstance(embedded,dict):return MimicProfile.from_dict(embedded)
    path=str(ext.get('mimic_profile_path','') or '')
    if ref=='custom' and path and Path(path).is_file():return MimicProfile.load(path)
    policy=str(ext.get('facial_animation_policy','auto'))
    if policy=='no':return None
    return BUILTIN_COMMON46
def profile_for_names(names):return MimicProfile('custom:auto','FBX morph names',tuple(MimicTarget.create(n) for n in names))
__all__=['BUILTIN_COMMON46_REF','MimicMappingRow','MimicProfile','MimicTarget','auto_map_shapes','mapping_from_payload','profile_for_names','resolve_mimic_profile']
