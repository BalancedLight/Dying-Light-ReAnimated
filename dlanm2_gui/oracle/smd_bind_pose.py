from __future__ import annotations
from dataclasses import dataclass
from pathlib import Path
import math,re
import numpy as np
from ..trackmap import dl_name_hash
@dataclass(frozen=True,slots=True)
class SmdBone:
 index:int; name:str; parent_index:int; translation:tuple[float,float,float]; rotation_xyz:tuple[float,float,float]
 @property
 def euler_xyz_radians(self): return self.rotation_xyz
@dataclass(frozen=True,slots=True)
class SmdPose:
 bones:tuple[SmdBone,...]
 @property
 def by_index(self): return {b.index:b for b in self.bones}
def _rot(v):
 x,y,z=v; cx,sx=math.cos(x),math.sin(x); cy,sy=math.cos(y),math.sin(y); cz,sz=math.cos(z),math.sin(z)
 rx=np.array(((1,0,0),(0,cx,-sx),(0,sx,cx)),float); ry=np.array(((cy,0,sy),(0,1,0),(-sy,0,cy)),float); rz=np.array(((cz,-sz,0),(sz,cz,0),(0,0,1)),float)
 return rz@ry@rx
def parse_smd_bind_pose(path):
 lines=Path(path).read_text(encoding='utf-8',errors='replace').splitlines(); names={}; parents={}; rows={}; section=''
 for line in lines:
  t=line.strip()
  if t in {'nodes','skeleton','triangles'}: section=t; continue
  if t=='end': section=''; continue
  if section=='nodes':
   m=re.match(r'(-?\d+)\s+"(.*)"\s+(-?\d+)',t)
   if m: i=int(m.group(1)); names[i]=m.group(2); parents[i]=int(m.group(3))
  elif section=='skeleton':
   p=t.split()
   if len(p)>=7 and p[0].lstrip('-').isdigit(): rows[int(p[0])]=tuple(map(float,p[1:7]))
 bones=[]
 for i in sorted(names):
  r=rows.get(i,(0,0,0,0,0,0)); bones.append(SmdBone(i,names[i],parents.get(i,-1),tuple(r[:3]),tuple(r[3:6])))
 return SmdPose(tuple(bones))
def smd_local_matrices(pose):
 out={}
 for b in pose.bones:
  m=np.eye(4); m[:3,:3]=_rot(b.rotation_xyz); m[:3,3]=b.translation; out[b.name]=m
 return out
def smd_global_matrices(pose):
 local=smd_local_matrices(pose); out={}; by=pose.by_index
 for b in pose.bones: out[b.name]=local[b.name] if b.parent_index<0 else out[by[b.parent_index].name]@local[b.name]
 return out
def quaternion_wxyz_from_matrix(matrix):
 m=np.asarray(matrix,float)[:3,:3]; t=np.trace(m)
 if t>0:
  s=math.sqrt(t+1)*2; q=(.25*s,(m[2,1]-m[1,2])/s,(m[0,2]-m[2,0])/s,(m[1,0]-m[0,1])/s)
 else:
  i=int(np.argmax(np.diag(m)))
  if i==0:
   s=math.sqrt(max(0,1+m[0,0]-m[1,1]-m[2,2]))*2; q=((m[2,1]-m[1,2])/s,.25*s,(m[0,1]+m[1,0])/s,(m[0,2]+m[2,0])/s)
  elif i==1:
   s=math.sqrt(max(0,1+m[1,1]-m[0,0]-m[2,2]))*2; q=((m[0,2]-m[2,0])/s,(m[0,1]+m[1,0])/s,.25*s,(m[1,2]+m[2,1])/s)
  else:
   s=math.sqrt(max(0,1+m[2,2]-m[0,0]-m[1,1]))*2; q=((m[1,0]-m[0,1])/s,(m[0,2]+m[2,0])/s,(m[1,2]+m[2,1])/s,.25*s)
 q=np.asarray(q,float); q/=max(np.linalg.norm(q),1e-12); return q
def anm2_cayley_vector_from_quaternion(q):
 q=np.asarray(q,float); q/=max(np.linalg.norm(q),1e-12)
 if q[0]<0:q=-q
 d=1+q[0]
 return np.zeros(3) if abs(d)<1e-12 else q[1:4]/d
def bind_track_values(pose,descriptors,fallback):
 local=smd_local_matrices(pose); byhash={dl_name_hash(b.name):b.name for b in pose.bones}; rows=[]; names={}; fb=[]
 for i,d in enumerate(descriptors):
  n=byhash.get(d)
  if n:
   m=local[n]; v=anm2_cayley_vector_from_quaternion(quaternion_wxyz_from_matrix(m)); rows.append([*map(float,v),*map(float,m[:3,3]),1,1,1]); names[d]=n
  else: rows.append(list(fallback[i])); fb.append(d)
 return rows,names,fb


def smd_extrinsic_xyz_matrix(euler_xyz_radians):
 return _rot(euler_xyz_radians)
