from __future__ import annotations
from dataclasses import dataclass
from pathlib import Path
import math
from .oracle.binary_fbx_mixamo import _FbxDocument,_properties70,_child_value,_clean_name,_sample_curve
@dataclass(frozen=True,slots=True)
class FbxBlendShapeCurve:
 name:str; channel_id:int; aliases:tuple[str,...]; values:tuple[float,...]; default_value:float; source_scale:float; animated:bool
@dataclass(frozen=True,slots=True)
class FbxFacialScan:
 source_path:str; animation_stack:str; fps:int; frame_count:int; curves:tuple[FbxBlendShapeCurve,...]; warnings:tuple[str,...]=()
 @property
 def animated_curves(self):return tuple(x for x in self.curves if x.animated)
 @property
 def animated_shape_names(self):return tuple(x.name for x in self.animated_curves)
 @property
 def has_facial_animation(self):return bool(self.animated_curves)
 def curve_by_name(self):return {x.name:x for x in self.curves}
 def summary(self):return {'source_path':self.source_path,'animation_stack':self.animation_stack,'fps':self.fps,'frame_count':self.frame_count,'shape_count':len(self.curves),'animated_shape_count':len(self.animated_curves),'shape_names':[x.name for x in self.curves],'animated_shape_names':list(self.animated_shape_names),'warnings':list(self.warnings)}
def scan_fbx_blendshapes(source,*,fps=30,animation_stack=None,document=None):
 d=document or _FbxDocument(source,animation_stack=animation_stack)
 if animation_stack and d.selected_animation_stack is None:d.select_animation_stack(animation_stack)
 ticks=d.frame_ticks(fps=fps); curves=d._all_curves(); rows=[]
 for cid,node in d.object_by_id.items():
  if node.name!='Deformer' or len(node.properties)<3 or str(node.properties[2])!='BlendShapeChannel':continue
  name=_clean_name(node.properties[1]); props=_properties70(node); default=float((props.get('DeformPercent') or [0])[0]); found=None
  for kind,cnid,rest,cn in d._linked(cid,'AnimationCurveNode'):
   if kind!='OP' or (rest and 'deform' not in str(rest[0]).lower()):continue
   for ck,curve_id,crest,curve_node in d._linked(cnid,'AnimationCurve'):
    if ck=='OP' and curve_id in curves:found=curves[curve_id];break
  raw=found[1] if found else [default];scale=.01 if max([abs(default),*(abs(x) for x in raw)],default=0)>2 else 1.;values=tuple(float(_sample_curve(found,t,default)*scale if found else default*scale) for t in ticks);animated=(max(values,default=0)-min(values,default=0))>1e-6;rows.append(FbxBlendShapeCurve(name,cid,(name,),values,default*scale,scale,animated))
 warnings=[]
 if not rows:warnings.append('No FBX BlendShapeChannel objects were found.')
 elif not any(x.animated for x in rows):warnings.append('Blendshape channels exist, but no changing DeformPercent curve was found.')
 return FbxFacialScan(str(Path(source).resolve()),d.selected_animation_stack.name if d.selected_animation_stack else '',fps,len(ticks),tuple(sorted(rows,key=lambda x:x.name.lower())),tuple(warnings))
def detect_facial_animation(source,**kw):return scan_fbx_blendshapes(source,**kw).has_facial_animation
