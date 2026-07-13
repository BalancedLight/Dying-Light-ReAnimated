from __future__ import annotations
from dataclasses import dataclass
from pathlib import Path
from typing import Any
import math
import unicodedata
import numpy as np
from ..model_importer.fbx_model import (
    FbxScene, FbxNode, FBX_TICKS_PER_SECOND, ROTATION_ORDERS, _child,
    _properties70, _child_value, _clean_name, _vector_property,
    _translation_matrix, _scale_matrix, _euler_matrix, _axis_rotation,
)

@dataclass(frozen=True,slots=True)
class FbxAnimationStack:
    name:str
    layer_names:tuple[str,...]
    start_tick:int
    stop_tick:int
    object_id:int
    layer_ids:tuple[int,...]


def _sample_curve(curve:tuple[list[int],list[float]]|None,tick:int,default:float)->float:
    if curve is None:return float(default)
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
        limb_ids=set(self.scene.limb_ids)
        self.parent_by_name={
            n:(self.scene.model_names.get(self.scene.model_parent_id(i)) if self.scene.model_parent_id(i) in limb_ids else None)
            for n,i in self.limb_models.items()
        }
        self.top=self.scene.top
        self.animation_stacks=self._animation_stack_inventory(); self.selected_animation_stack=None
        self.meters_per_unit=self.scene.meters_per_unit
        self._curve_cache={}; self.curves={}; self.animation_start_tick=0; self.animation_stop_tick=0
        self._build_bind_inventory()
        if animation_stack: self.select_animation_stack(animation_stack)
        elif len(self.animation_stacks)==1:self.select_animation_stack(self.animation_stacks[0].name)
    def select_animation_stack(self,name:str|None=None):
        if not self.animation_stacks:
            if name: raise ValueError(f"FBX has no animation stack named {name!r}: {self.path}")
            self.selected_animation_stack=None; self.curves={}; return None
        if not name:
            if len(self.animation_stacks)!=1:
                available=', '.join(repr(row.name) for row in self.animation_stacks)
                raise ValueError('FBX contains multiple animations; choose an animation stack: '+available)
            row=self.animation_stacks[0]
        else:
            matches=[item for item in self.animation_stacks if item.name==name]
            if not matches:
                available=', '.join(repr(item.name) for item in self.animation_stacks)
                raise ValueError(f"FBX animation stack {name!r} was not found; available stacks: {available}")
            if len(matches)>1: raise ValueError(f"FBX contains duplicate animation stack names: {name!r}")
            row=matches[0]
        if len(row.layer_ids)!=1:
            layers=', '.join(repr(value) for value in row.layer_names) or 'none'
            raise ValueError(f"FBX animation stack {row.name!r} contains {len(row.layer_ids)} layers ({layers}); bake/flatten it to one animation layer before import")
        self.selected_animation_stack=row; self.curves=self._animation_curves(row.layer_ids[0])
        times=[time for curve_times,_ in self.curves.values() for time in curve_times]
        self.animation_start_tick=int(row.start_tick); self.animation_stop_tick=int(row.stop_tick)
        if times:
            if self.animation_start_tick==self.animation_stop_tick==0:self.animation_start_tick=min(times)
            self.animation_stop_tick=max(self.animation_stop_tick,max(times))
        return row
    def frame_ticks(self,fps=30):
        if len(self.animation_stacks)>1 and self.selected_animation_stack is None:self.select_animation_stack(None)
        a=int(self.animation_start_tick); b=max(a,int(self.animation_stop_tick))
        count=int(math.ceil((b-a)*fps/FBX_TICKS_PER_SECOND))+1
        return [min(b,a+int(round(i*FBX_TICKS_PER_SECOND/fps))) for i in range(max(1,count))]
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
    def _animation_curves(self,layer_id):
        result={}
        for kind,curve_node_id,_rest in self.children.get(layer_id,[]):
            node=self.object_by_id.get(curve_node_id)
            if kind!='OO' or node is None or node.name!='AnimationCurveNode':continue
            model_id=None; property_name=None
            for parent_kind,parent_id,rest in self.parents.get(curve_node_id,[]):
                parent=self.object_by_id.get(parent_id)
                if parent_kind=='OP' and parent is not None and parent.name=='Model':
                    model_id=parent_id; property_name=str(rest[0]) if rest else ''
            if model_id is None or not property_name:continue
            for child_kind,curve_id,rest in self.children.get(curve_node_id,[]):
                curve=self.object_by_id.get(curve_id)
                if child_kind!='OP' or curve is None or curve.name!='AnimationCurve':continue
                axis=str(rest[0]).split('|')[-1] if rest else ''
                times=[int(value) for value in (_child_value(curve,'KeyTime',[]) or [])]
                values=[float(value) for value in (_child_value(curve,'KeyValueFloat',[]) or [])]
                if times and len(times)==len(values):result[(model_id,property_name,axis)]=(times,values)
        return result
    def _animation_stack_inventory(self):
        layers={int(node.properties[0]):_clean_name(node.properties[1]) for node in self.objects.children if node.name=='AnimationLayer' and len(node.properties)>=2}
        takes={}; takes_node=self.top.get('Takes')
        if takes_node:
            for node in takes_node.children:
                local=_child(node,'LocalTime') if node.name=='Take' else None
                if local and len(local.properties)>=2:takes[_clean_name(node.properties[0])]=(int(local.properties[0]),int(local.properties[1]))
        rows=[]; claimed=set()
        for node in self.objects.children:
            if node.name!='AnimationStack' or len(node.properties)<2:continue
            oid=int(node.properties[0]); name=_clean_name(node.properties[1])
            layer_ids=tuple(child for kind,child,_ in self.children.get(oid,[]) if kind=='OO' and child in layers);claimed.update(layer_ids)
            props=_properties70(node); start=int((props.get('LocalStart') or [0])[0]); stop=int((props.get('LocalStop') or [start])[0])
            if name in takes:start,stop=takes[name]
            rows.append(FbxAnimationStack(name,tuple(layers[value] for value in layer_ids),start,stop,oid,layer_ids))
        for oid,name in layers.items():
            if oid not in claimed:rows.append(FbxAnimationStack(name,(name,),0,0,oid,(oid,)))
        return tuple(rows)
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
        node=self.object_by_id[object_id]; props=_properties70(node)
        get=lambda n,d:np.asarray(_vector_property(props,n,d),float).copy()
        t=get('Lcl Translation',(0,0,0)); r=get('Lcl Rotation',(0,0,0)); s=get('Lcl Scaling',(1,1,1))
        if use_animation:
            for index,axis in enumerate('XYZ'):
                t[index]=_sample_curve(self.curves.get((object_id,'Lcl Translation',axis)),tick,t[index])
                r[index]=_sample_curve(self.curves.get((object_id,'Lcl Rotation',axis)),tick,r[index])
                s[index]=_sample_curve(self.curves.get((object_id,'Lcl Scaling',axis)),tick,s[index])
        pre=_vector_property(props,'PreRotation',(0,0,0)); post=_vector_property(props,'PostRotation',(0,0,0)); ro=_vector_property(props,'RotationOffset',(0,0,0)); rp=_vector_property(props,'RotationPivot',(0,0,0)); so=_vector_property(props,'ScalingOffset',(0,0,0)); sp=_vector_property(props,'ScalingPivot',(0,0,0)); order=ROTATION_ORDERS.get(int((props.get('RotationOrder') or [0])[0]),'XYZ')
        return _translation_matrix(t)@_translation_matrix(ro)@_translation_matrix(rp)@_euler_matrix(pre,order)@_euler_matrix(r,order)@np.linalg.inv(_euler_matrix(post,order))@_translation_matrix(-rp)@_translation_matrix(so)@_translation_matrix(sp)@_scale_matrix(s)@_translation_matrix(-sp)
    def global_matrices(self,tick=0,use_animation=True):
        out={}; byid={}; visiting=set()
        def resolve(i):
            if i in byid:return byid[i]
            if i in visiting:raise ValueError('FBX hierarchy cycle')
            visiting.add(i); local=self._local_matrix(i,tick,use_animation); p=self.scene.model_parent_id(i); v=resolve(p)@local if p in self.scene.model_names else local; visiting.remove(i); byid[i]=v; return v
        for n,i in {**self.null_models,**self.limb_models}.items():
            value=resolve(i)
            if i in self.limb_models.values():value=self._scene_scale_normalizer(i)@value
            out[n]=value
        return out

    def _scene_scale_normalizer(self,bone_id):
        limb=set(self.scene.limb_ids); parent=self.scene.model_parent_id(bone_id)
        while parent in limb:
            parent=self.scene.model_parent_id(parent)
        wrapper_id=None
        while parent in self.scene.model_names and parent not in limb:
            wrapper_id=parent;parent=self.scene.model_parent_id(parent)
        if wrapper_id is None:return np.eye(4,dtype=float)
        wrapper=self.scene.model_global_matrix(wrapper_id);linear=wrapper[:3,:3];scales=np.linalg.norm(linear,axis=0)
        uniform=float(np.mean(scales))
        if not np.isfinite(uniform) or uniform<=1e-12 or max(abs(scales-uniform))>max(1e-5,uniform*1e-5) or abs(uniform-1.0)<1e-5:
            return np.eye(4,dtype=float)
        props=_properties70(self.object_by_id[wrapper_id])
        native_dlr=bool((props.get('dlr_native_anm2_export') or [0])[0])
        normalized=np.eye(4,dtype=float) if native_dlr else wrapper.copy()
        if not native_dlr:
            normalized[:3,:3]=linear/uniform;normalized[:3,3]=wrapper[:3,3]/uniform
        try:return normalized@np.linalg.inv(wrapper)
        except np.linalg.LinAlgError:return np.eye(4,dtype=float)

    def _build_bind_inventory(self):
        pose=self.scene.bind_pose_matrices
        links={}; conflicts=[]
        for geometry in self.scene.geometries:
            for cluster in geometry.clusters:
                if cluster.bone_id is None or cluster.transform_link is None:continue
                previous=links.get(cluster.bone_id)
                if previous is not None and not np.allclose(previous,cluster.transform_link,atol=1e-5,rtol=1e-5):
                    conflicts.append(self.scene.model_names.get(cluster.bone_id,str(cluster.bone_id)))
                links.setdefault(cluster.bone_id,cluster.transform_link.copy())
        pose_link_conflicts=[]
        for oid,pose_matrix in pose.items():
            if oid in links and not np.allclose(pose_matrix,links[oid],atol=1e-5,rtol=1e-5):
                pose_link_conflicts.append(self.scene.model_names.get(oid,str(oid)))
        globals_by_name={}; source_by_name={}
        for name,oid in self.limb_models.items():
            if oid in pose:matrix=pose[oid].copy(); source='Pose::BindPose'
            elif oid in links:matrix=links[oid].copy(); source='TransformLink'
            else:matrix=self.scene.model_global_matrix(oid); source='ModelTransformsFallback'
            matrix=self._scene_scale_normalizer(oid)@matrix
            globals_by_name[name]=matrix;source_by_name[name]=source
        locals_by_name={}
        for name,matrix in globals_by_name.items():
            parent=self.parent_by_name.get(name)
            if parent in globals_by_name:
                try:locals_by_name[name]=np.linalg.inv(globals_by_name[str(parent)])@matrix
                except np.linalg.LinAlgError:locals_by_name[name]=np.full((4,4),np.nan)
            else:locals_by_name[name]=matrix.copy()
        counts={key:list(source_by_name.values()).count(key) for key in ('Pose::BindPose','TransformLink','ModelTransformsFallback')}
        if counts['Pose::BindPose']==len(self.limb_models):selected='Pose::BindPose'
        elif counts['Pose::BindPose'] or counts['TransformLink']:selected='mixed_authoritative_with_fallback'
        else:selected='ModelTransformsFallback'
        self.bind_global_matrices=globals_by_name;self.bind_local_matrices=locals_by_name
        self.bind_source_by_bone=source_by_name;self.bind_source=selected
        self.bind_coverage={**counts,'authoritative':counts['Pose::BindPose']+counts['TransformLink'],'total':len(self.limb_models)}
        self.bind_warnings=[]
        if self.bind_coverage['authoritative']<len(self.limb_models):self.bind_warnings.append('Authoritative bind coverage is incomplete; unanimated Model transforms are used only for uncovered bones.')
        if conflicts:self.bind_warnings.append('Conflicting TransformLink matrices were found for: '+', '.join(sorted(set(conflicts))))
        if pose_link_conflicts:self.bind_warnings.append('Pose::BindPose and TransformLink matrices disagree for: '+', '.join(sorted(set(pose_link_conflicts))))
        self.bind_conflicts=tuple(sorted(set(conflicts)))
        self.pose_transform_conflicts=tuple(sorted(set(pose_link_conflicts)))
        normalized={}
        duplicates=[]
        for name in self.limb_models:
            key=unicodedata.normalize('NFKC',name).casefold()
            if key in normalized and normalized[key]!=name:duplicates.append((normalized[key],name))
            normalized[key]=name
        self.normalized_name_collisions=tuple(duplicates)
    def bind_diagnostics(self):
        return {'selected_bind_source':self.bind_source,'bind_coverage':dict(self.bind_coverage),'per_bone_source':dict(self.bind_source_by_bone),'warnings':list(self.bind_warnings),'conflicting_transform_links':list(self.bind_conflicts),'conflicting_pose_transform_links':list(self.pose_transform_conflicts)}

AnimationStack=FbxAnimationStack
def _decompose_basis(matrix):
    from .smd_bind_pose import quaternion_wxyz_from_matrix
    value=np.asarray(matrix,dtype=float);translation=value[:3,3].copy();linear=value[:3,:3].copy()
    scales=np.linalg.norm(linear,axis=0);scales=np.where(scales<1e-12,1.0,scales);normalized=linear/scales
    u,_singular,vt=np.linalg.svd(normalized);rotation=u@vt
    if np.linalg.det(rotation)<0.0:u[:,-1]*=-1.0;rotation=u@vt;scales[-1]*=-1.0
    return translation,quaternion_wxyz_from_matrix(rotation),scales
__all__=['FBX_TICKS_PER_SECOND','FbxAnimationStack','AnimationStack','FbxNode','_FbxDocument','_properties70','_child_value','_clean_name','_sample_curve','_axis_rotation','_euler_matrix','_decompose_basis']
