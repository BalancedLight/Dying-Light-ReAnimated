from __future__ import annotations
from dataclasses import dataclass
from pathlib import Path
from typing import Any
import numpy as np
from ..model_importer.fbx_model import (
    FbxScene, FbxNode, FBX_TICKS_PER_SECOND, ROTATION_ORDERS,
    _properties70, _child_value, _clean_name, _vector_property,
    _translation_matrix, _scale_matrix, _euler_matrix,
)

@dataclass(frozen=True,slots=True)
class AnimationStack:
    name:str
    layer_ids:tuple[int,...]
    start_tick:int
    stop_tick:int


def _sample_curve(curve:tuple[list[int],list[float]],tick:int,default:float)->float:
    times,values=curve
    if not times:return float(default)
    if tick<=times[0]:return float(values[0])
    if tick>=times[-1]:return float(values[-1])
    import bisect
    i=bisect.bisect_right(times,tick)-1
    a,b=times[i],times[i+1]
    if b==a:return float(values[i])
    f=(tick-a)/(b-a)
    return float(values[i]*(1-f)+values[i+1]*f)

class _FbxDocument:
    def __init__(self,path:str|Path,animation_stack:str|None=None):
        self.path=Path(path); self.scene=FbxScene.from_path(self.path)
        self.object_by_id=self.scene.object_by_id; self.parents=self.scene.parents; self.children=self.scene.children
        self.objects=self.scene.top['Objects']
        self.limb_models={self.scene.model_names[i]:i for i in self.scene.limb_ids}
        self.null_models={self.scene.model_names[i]:i for i in self.scene.model_ids if self.scene.model_subtypes.get(i) in {'Null','Root'}}
        self.parent_by_name={n:(self.scene.model_names.get(self.scene.model_parent_id(i)) if self.scene.model_parent_id(i) else None) for n,i in self.limb_models.items()}
        layers={i:_clean_name(n.properties[1]) for i,n in self.object_by_id.items() if n.name=='AnimationLayer' and len(n.properties)>=2}
        stacks=[]
        for s in self.scene.animation_stacks:
            ids=tuple(i for i,n in layers.items() if n in s.layer_names)
            stacks.append(AnimationStack(s.name,ids,s.start_tick,s.stop_tick))
        self.animation_stacks=tuple(stacks); self.selected_animation_stack=None
        self.meters_per_unit=self.scene.meters_per_unit
        self._curve_cache={}
        if animation_stack: self.select_animation_stack(animation_stack)
        elif len(self.animation_stacks)==1:self.selected_animation_stack=self.animation_stacks[0]
    def select_animation_stack(self,name:str|None):
        if not name and len(self.animation_stacks)==1:self.selected_animation_stack=self.animation_stacks[0]; return
        row=next((x for x in self.animation_stacks if x.name==name),None)
        if row is None: raise ValueError(f'FBX animation stack not found: {name!r}')
        self.selected_animation_stack=row
    def frame_ticks(self,fps=30):
        if self.selected_animation_stack: a,b=self.selected_animation_stack.start_tick,self.selected_animation_stack.stop_tick
        elif self.animation_stacks: a,b=self.animation_stacks[0].start_tick,self.animation_stacks[0].stop_tick
        else:
            times=[t for c in self._all_curves().values() for t in c[0]]
            a,b=(min(times),max(times)) if times else (0,0)
        step=FBX_TICKS_PER_SECOND/float(fps); count=max(1,int(round((b-a)/step))+1)
        return [int(round(a+i*step)) for i in range(count)]
    def frame_count(self,fps=30): return len(self.frame_ticks(fps))
    def _linked(self,oid,name=None):
        for kind,other,rest in self.children.get(oid,[])+self.parents.get(oid,[]):
            n=self.object_by_id.get(other)
            if n is not None and (name is None or n.name==name): yield kind,other,rest,n
    def _all_curves(self):
        if self._curve_cache:return self._curve_cache
        out={}
        for oid,n in self.object_by_id.items():
            if n.name!='AnimationCurve':continue
            times=[int(x) for x in (_child_value(n,'KeyTime',[]) or [])]; vals=[float(x) for x in (_child_value(n,'KeyValueFloat',[]) or [])]
            if times and len(times)==len(vals):out[oid]=(times,vals)
        self._curve_cache=out; return out
    def _animated_properties(self,tick:int):
        layer_ids=set(self.selected_animation_stack.layer_ids if self.selected_animation_stack else [i for i,n in self.object_by_id.items() if n.name=='AnimationLayer'])
        curve_nodes=set()
        for lid in layer_ids:
            for kind,oid,rest,n in self._linked(lid,'AnimationCurveNode'):
                if kind=='OO':curve_nodes.add(oid)
        result={}
        curves=self._all_curves()
        for cnid in curve_nodes:
            target=None; prop=''
            for kind,oid,rest,n in self._linked(cnid,'Model'):
                if kind=='OP': target=oid; prop=str(rest[0]) if rest else ''; break
            if target is None:continue
            props=_properties70(self.object_by_id[target]); default=_vector_property(props,prop,(0,0,0) if prop!='Lcl Scaling' else (1,1,1)).astype(float)
            value=default.copy()
            for kind,cid,rest,n in self._linked(cnid,'AnimationCurve'):
                if kind!='OP' or cid not in curves:continue
                axis=str(rest[0]) if rest else ''
                idx=0 if axis.endswith('X') else 1 if axis.endswith('Y') else 2 if axis.endswith('Z') else None
                if idx is not None:value[idx]=_sample_curve(curves[cid],tick,value[idx])
            result.setdefault(target,{})[prop]=value
        return result
    def _local_matrix(self,object_id:int,tick=0,use_animation=True):
        node=self.object_by_id[object_id]; props=_properties70(node); anim=self._animated_properties(tick).get(object_id,{}) if use_animation else {}
        get=lambda n,d:np.asarray(anim.get(n,_vector_property(props,n,d)),float)
        t=get('Lcl Translation',(0,0,0)); r=get('Lcl Rotation',(0,0,0)); s=get('Lcl Scaling',(1,1,1))
        pre=_vector_property(props,'PreRotation',(0,0,0)); post=_vector_property(props,'PostRotation',(0,0,0)); ro=_vector_property(props,'RotationOffset',(0,0,0)); rp=_vector_property(props,'RotationPivot',(0,0,0)); so=_vector_property(props,'ScalingOffset',(0,0,0)); sp=_vector_property(props,'ScalingPivot',(0,0,0)); order=ROTATION_ORDERS.get(int((props.get('RotationOrder') or [0])[0]),'XYZ')
        return _translation_matrix(t)@_translation_matrix(ro)@_translation_matrix(rp)@_euler_matrix(pre,order)@_euler_matrix(r,order)@np.linalg.inv(_euler_matrix(post,order))@_translation_matrix(-rp)@_translation_matrix(so)@_translation_matrix(sp)@_scale_matrix(s)@_translation_matrix(-sp)
    def global_matrices(self,tick=0,use_animation=True):
        out={}; byid={}; visiting=set()
        def resolve(i):
            if i in byid:return byid[i]
            if i in visiting:raise ValueError('FBX hierarchy cycle')
            visiting.add(i); local=self._local_matrix(i,tick,use_animation); p=self.scene.model_parent_id(i); v=resolve(p)@local if p in self.scene.model_names else local; visiting.remove(i); byid[i]=v; return v
        for n,i in {**self.null_models,**self.limb_models}.items():out[n]=resolve(i)
        return out

__all__=['FBX_TICKS_PER_SECOND','_FbxDocument','_properties70','_child_value','_clean_name','_sample_curve']
