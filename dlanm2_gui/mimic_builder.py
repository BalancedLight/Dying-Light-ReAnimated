from __future__ import annotations
from dataclasses import dataclass
from . import anm2
from .anm2_writer import build_payload_from_values
from .trackmap import dl_name_hash
@dataclass(frozen=True,slots=True)
class MimicBuild:
 payload:bytes; frame_count:int; fps:int; report:dict
def build_mimic_anm2(scan,*,mapping=None):
 curves=list(scan.animated_curves); mapping=mapping or {c.name:c.name for c in curves}; selected=[c for c in curves if c.name in mapping];descriptors=[dl_name_hash(mapping[c.name]) for c in selected];fc=max(2,scan.frame_count);values=[]
 for f in range(fc):
  frame=[]
  for c in selected:
   v=c.values[min(f,len(c.values)-1)] if c.values else c.default_value;frame.append([0.,0.,0.,float(v),0.,0.,1.,1.,1.])
  values.append(frame)
 flags=[[False,False,False,True,False,False,False,False,False] for _ in selected];h=anm2.Anm2Header(42,1,fc,len(selected),1,0,0,1,0,0);payload=build_payload_from_values(h,descriptors,values,flags);return MimicBuild(payload,fc,scan.fps,{'shape_count':len(selected),'source_shapes':[c.name for c in selected],'target_shapes':[mapping[c.name] for c in selected]})
