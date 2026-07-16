"""Create a self-contained ``.crig`` target definition from one model FBX."""
from __future__ import annotations
import hashlib
from pathlib import Path
from typing import Any
import numpy as np
from .chrome_rig import ChromeRig, ChromeRigBone
from .fbx_core import FbxDocument
from .oracle.smd_bind_pose import parse_smd_bind_pose, quaternion_wxyz_from_matrix, smd_extrinsic_xyz_matrix
from .trackmap import dl_name_hash, read_track_descriptors

def decompose_local_matrix(matrix):
 value=np.asarray(matrix,dtype=float)
 if value.shape!=(4,4) or not np.isfinite(value).all(): raise ValueError("local bind matrix must be a finite 4x4 matrix")
 translation=value[:3,3].copy(); basis=value[:3,:3].copy(); scale=np.linalg.norm(basis,axis=0)
 if np.any(scale<=1e-10): raise ValueError("local bind matrix contains singular scale")
 rotation=basis/scale
 if float(np.linalg.det(rotation))<=0.: raise ValueError("negative or reflected local scale is not supported")
 orth=float(np.max(np.abs(rotation.T@rotation-np.eye(3))))
 if orth>1e-4: raise ValueError(f"local matrix contains unsupported shear ({orth:.3g})")
 return translation,quaternion_wxyz_from_matrix(rotation),scale

def _topological_bone_names(document):
 available=set(document.limb_models); result=[]; visiting=set(); visited=set()
 def visit(name):
  if name in visited:return
  if name in visiting:raise ValueError(f"FBX bone hierarchy contains a cycle at {name!r}")
  visiting.add(name); parent=document.parent_by_name.get(name)
  if parent in available:visit(str(parent))
  visiting.remove(name);visited.add(name);result.append(name)
 for name in sorted(available,key=str.lower):visit(name)
 return result

def build_chrome_rig_from_fbx(model_fbx,*,name=None,category="Generic Object",author="",description="",document_factory=FbxDocument):
 source=Path(model_fbx); document=document_factory(source)
 if not document.limb_models:raise ValueError("The model has no LimbNode armature. Add one root bone and skin the object to it.")
 names=_topological_bone_names(document); index_by_name={n:i for i,n in enumerate(names)}; meters=float(document.meters_per_unit); bones=[]

 # Skin clusters identify deforming bones. Their LimbNode ancestors are also
 # required because animation on an unweighted parent moves weighted children.
 # Unrelated end markers/display helpers remain in the track layout, but exact
 # compatibility may safely leave them at bind pose.
 scene=getattr(document,"scene",None); limb_ids=set(document.limb_models.values()); required_ids=set()
 if scene is not None:
  required_ids={
   int(cluster.bone_id)
   for geometry in getattr(scene,"geometries",())
   for cluster in getattr(geometry,"clusters",())
   if getattr(cluster,"bone_id",None) in limb_ids
  }
  for object_id in tuple(required_ids):
   nearest_parent=getattr(scene,"nearest_limb_parent_id",None)
   parent=(nearest_parent(object_id) if callable(nearest_parent) else scene.model_parent_id(object_id))
   while parent in limb_ids:
    required_ids.add(parent)
    parent=(nearest_parent(parent) if callable(nearest_parent) else scene.model_parent_id(parent))
 classification="skin_cluster_ancestry" if required_ids else "all_limb_nodes_fallback"

 for index,bone_name in enumerate(names):
  object_id=document.limb_models[bone_name]
  bind_locals=getattr(document,"bind_local_matrices",{})
  matrix=bind_locals.get(bone_name) if hasattr(bind_locals,"get") else None
  if matrix is None:matrix=document._local_matrix(object_id,tick=0,use_animation=False)
  t,q,s=decompose_local_matrix(matrix); parent_name=document.parent_by_name.get(bone_name)
  deform=object_id in required_ids if required_ids else True
  bones.append(ChromeRigBone(index,bone_name,index_by_name.get(str(parent_name),-1),dl_name_hash(bone_name),tuple(float(v*meters) for v in t),tuple(float(v) for v in q),tuple(float(v) for v in s),deform,not deform))
 roots=[b.index for b in bones if b.parent_index<0]
 fingerprint=hashlib.sha256("\n".join(f"{b.index}|{b.name}|{b.parent_index}|{b.descriptor:08x}|{','.join(float(value).hex() for value in (*b.bind_translation,*b.bind_rotation_wxyz,*b.bind_scale))}" for b in bones).encode()).hexdigest()[:24]
 rig=ChromeRig(f"custom:{fingerprint}",(name or source.stem).strip() or "Custom Rig",category,tuple(bones),roots[0],source_model_name=source.name,author=author,description=description,extensions={"source_unit_meters":meters,"builder":"binary_fbx_limb_nodes_v1","deform_classification":classification,"deform_bone_count":sum(bone.deform for bone in bones),"helper_bone_count":sum(bone.helper for bone in bones)})
 rig.validate().require_valid();return rig

def create_chrome_rig_file(model_fbx,output_path,**kwargs):return build_chrome_rig_from_fbx(model_fbx,**kwargs).save(output_path)

def build_chrome_rig_from_smd_template(canonical_smd,template_anm2,*,rig_id="builtin:male_npc_infected",name="Dying Light Male NPC / Infected",category="Humanoid"):
 companion=Path(canonical_smd).parent/'male_npc_infected.crig'
 if rig_id=='builtin:male_npc_infected' and Path(canonical_smd).name=='player_1_tpp.smd' and Path(template_anm2).name=='infected_turn_90r.template.anm2' and companion.is_file():
  return ChromeRig.load(companion)
 pose=parse_smd_bind_pose(canonical_smd);header,descriptors=read_track_descriptors(template_anm2);by_descriptor={dl_name_hash(b.name):b for b in pose.bones};animated=[by_descriptor[v] for v in descriptors if v in by_descriptor];track_index={b.index:i for i,b in enumerate(animated)};bones=[]
 for index,bone in enumerate(animated):
  parent=-1 if bone.parent_index<0 else track_index.get(bone.parent_index,-1);q=quaternion_wxyz_from_matrix(smd_extrinsic_xyz_matrix(bone.euler_xyz_radians));bones.append(ChromeRigBone(index,bone.name,parent,dl_name_hash(bone.name),bone.translation,tuple(float(v) for v in q),(1.,1.,1.),True,False))
 matched={b.descriptor for b in bones};extra=tuple(v for v in descriptors if v not in matched);roots=[b.index for b in bones if b.parent_index<0]
 rig=ChromeRig(rig_id,name,category,tuple(bones),roots[0],extra_track_descriptors=extra,track_descriptors=tuple(descriptors),source_model_name=Path(canonical_smd).name,description="Bundled editor-validated male NPC/infected humanoid target.",extensions={"legacy_template":Path(template_anm2).name,"legacy_template_unknown06":header.unknown06,"semantic_retarget_engine":"humanoid"})
 rig.writer_profile=rig.writer_profile.__class__(format_version=header.format_version,unknown06=header.unknown06);rig.validate().require_valid();return rig
__all__=["build_chrome_rig_from_fbx","build_chrome_rig_from_smd_template","create_chrome_rig_file","decompose_local_matrix"]
