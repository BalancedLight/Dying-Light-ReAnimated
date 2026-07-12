from __future__ import annotations
from dataclasses import dataclass
import struct
from typing import Mapping,Sequence
RP6L_MAGIC=b'RP6L'; BUILDER_INFORMATION_TYPE=-32257; ANIMATION_PAYLOAD_TYPE=320; ANIMATION_SCR_PAYLOAD_TYPE=322
@dataclass(frozen=True)
class Chunk: flags:int; unknown0:int; data:bytes; packed_size:int=0; unknown1:int=1; unknown2:int=2
@dataclass(frozen=True)
class Item: chunk_index:int; flags:int; unknown0:int; offset:int; size_or_hash:int; unknown1:int=0
@dataclass(frozen=True)
class Resource: item_count:int; resource_type:int; name_index:int; first_item_index:int
@dataclass(frozen=True)
class Parsed: version:int; chunks:tuple[Chunk,...]; items:tuple[Item,...]; resources:tuple[Resource,...]; names:tuple[str,...]
@dataclass(frozen=True)
class AnimationLibrary: animations:dict[str,bytes]; animation_scripts:dict[str,tuple[bytes,bytes]]
def pad16(b):return b+b'\0'*((-len(b))%16)
def build_rp6l(chunks,items,resources,names):
 offs=[]; nb=bytearray()
 for n in names:offs.append(len(nb));nb.extend(n.encode()+b'\0')
 table=36+20*len(chunks)+16*len(items)+12*len(resources)+4*len(offs)+len(nb); co=[];cur=table
 for c in chunks:co.append(cur);cur+=len(c.data)
 out=bytearray(b'RP6L'+struct.pack('<iiiiiiii',1,0,len(items),len(chunks),len(resources),len(nb),len(offs),1))
 for c,o in zip(chunks,co):out+=struct.pack('<HHIIiHH',c.flags,c.unknown0,o,len(c.data),c.packed_size,c.unknown1,c.unknown2)
 for i in items:out+=struct.pack('<BBhIii',i.chunk_index,i.flags,i.unknown0,i.offset,i.size_or_hash,i.unknown1)
 for r in resources:out+=struct.pack('<hhii',r.item_count,r.resource_type,r.name_index,r.first_item_index)
 for o in offs:out+=struct.pack('<i',o)
 out+=nb
 for c in chunks:out+=c.data
 return bytes(out)
def build_animation_library_rpack(*,animation_resources,animation_scripts):
 ar=list(animation_resources.items()) if isinstance(animation_resources,Mapping) else list(animation_resources); sn=list(animation_scripts)
 names=['_ANIMATION_','_ANIMATION_SCR_',*[n for n,_ in ar],*sn]; ix={n:i for i,n in enumerate(names)}; ab=pad16(''.join('+'+n+'\n' for n,_ in ar).encode()); sb=pad16(''.join('+'+n+'\n' for n in sn).encode()); chunks=[Chunk(64,2,d) for _,d in ar]; sci={}
 for n in sn:
  a,b=animation_scripts[n]; k=len(chunks);chunks += [Chunk(66,2,a),Chunk(67,2,b)];sci[n]=(k,k+1)
 bi=len(chunks);chunks.append(Chunk(255,4,ab+sb,unknown2=1));items=[Item(bi,0,ix['_ANIMATION_'],0,len(ab.rstrip(b'\0'))),Item(bi,0,ix['_ANIMATION_SCR_'],len(ab),len(sb.rstrip(b'\0')))]; ai={}
 for ci,(n,d) in enumerate(ar):ai[n]=len(items);items.append(Item(ci,0,ix[n],0,len(d)))
 si={}
 for n in sn:
  a,b=animation_scripts[n];x,y=sci[n];si[n]=len(items);items += [Item(x,0,ix[n],0,len(a)),Item(y,0,ix[n],0,len(b))]
 res=[Resource(1,BUILDER_INFORMATION_TYPE,ix['_ANIMATION_'],0),Resource(1,BUILDER_INFORMATION_TYPE,ix['_ANIMATION_SCR_'],1)]
 res += [Resource(1,ANIMATION_PAYLOAD_TYPE,ix[n],ai[n]) for n,_ in ar]; res += [Resource(2,ANIMATION_SCR_PAYLOAD_TYPE,ix[n],si[n]) for n in sn]
 return build_rp6l(chunks,items,res,names)
def build_common_anims_multi_probe_rpack(*,animation_resources,animation_script_resource_name,animation_script_sections):return build_animation_library_rpack(animation_resources=animation_resources,animation_scripts={animation_script_resource_name:animation_script_sections})
def parse_rp6l(data):
 if data[:4]!=b'RP6L':raise ValueError('not an RP6L file')
 ver,u,ic,cc,rc,ns,nc,u2=struct.unpack_from('<iiiiiiii',data,4);cur=36; cm=[]
 for _ in range(cc):cm.append(struct.unpack_from('<HHIIiHH',data,cur));cur+=20
 items=[]
 for _ in range(ic):items.append(Item(*struct.unpack_from('<BBhIii',data,cur)));cur+=16
 resources=[]
 for _ in range(rc):resources.append(Resource(*struct.unpack_from('<hhii',data,cur)));cur+=12
 no=[]
 for _ in range(nc):no.append(struct.unpack_from('<i',data,cur)[0]);cur+=4
 blob=data[cur:cur+ns]; names=[]
 for o in no:
  e=blob.find(b'\0',o);names.append(blob[o:e].decode())
 chunks=[]
 for f,u0,o,s,p,u1,u2 in cm:
  if o+s>len(data):raise ValueError('chunk out of range')
  chunks.append(Chunk(f,u0,data[o:o+s],p,u1,u2))
 return Parsed(ver,tuple(chunks),tuple(items),tuple(resources),tuple(names))
def extract_animation_library(data):
 p=parse_rp6l(data);a={};s={}
 for r in p.resources:
  n=p.names[r.name_index]; its=p.items[r.first_item_index:r.first_item_index+r.item_count]
  vals=[]
  for i in its:
   c=p.chunks[i.chunk_index].data;vals.append(c[i.offset:i.offset+i.size_or_hash])
  if r.resource_type==ANIMATION_PAYLOAD_TYPE and vals:a[n]=vals[0]
  elif r.resource_type==ANIMATION_SCR_PAYLOAD_TYPE and len(vals)>=2:s[n]=(vals[0],vals[1])
 return AnimationLibrary(a,s)
