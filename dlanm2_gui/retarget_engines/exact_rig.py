from __future__ import annotations
from pathlib import Path
from .mapped_rig import build_mapped_rig_anm2
from ..bone_maps import GenericBoneMap,BoneMapPair,skeleton_signature
from ..oracle.binary_fbx_mixamo import _FbxDocument

def build_exact_rig_anm2(animation_fbx,rig,*,fps=None,animation_stack=None,document_factory=_FbxDocument):
    doc=document_factory(Path(animation_fbx))
    if animation_stack:doc.select_animation_stack(animation_stack)
    source=set(doc.limb_models); target={b.name for b in rig.bones}; missing=sorted(target-source); extra=sorted(source-target); errors=[]
    if missing:errors.append('missing target bones: '+', '.join(missing[:20]))
    if extra:errors.append('source has extra bones: '+', '.join(extra[:20]))
    for b in rig.bones:
        expected=None if b.parent_index<0 else rig.bones[b.parent_index].name
        actual=doc.parent_by_name.get(b.name)
        if b.name in source and actual!=expected:errors.append(f'parent mismatch for {b.name!r}: expected {expected!r}, found {actual!r}')
    if errors:raise ValueError('Exact-rig skeleton mismatch:\n- '+'\n- '.join(errors)+'\n\nUse Root & .crig Mapping to create a reviewed cross-rig map.')
    source_hash=skeleton_signature((n,doc.parent_by_name.get(n)) for n in sorted(source))
    p=GenericBoneMap.create('Exact identity map',rig.skeleton_hash,source_hash,source_rig_ref=rig.rig_id)
    p.pairs=[BoneMapPair(b.descriptor,b.name,b.name,1.0,'exact') for b in rig.bones]
    result=build_mapped_rig_anm2(animation_fbx,rig,p,fps=fps,animation_stack=animation_stack,document_factory=document_factory)
    result.report['retarget_mode']='exact'; result.report['engine']='ExactRigRetargetEngine'; return result
