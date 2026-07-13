"""Animation-script target presets used by the release GUI and project files.

The value that ultimately matters to RP6L is the `_ANIMATION_SCR_` resource
name. Presets are conveniences only: the GUI always leaves the resource name
editable so projects are not locked to the small built-in list.
"""
from __future__ import annotations
from dataclasses import asdict, dataclass
import json
from pathlib import Path
from typing import Iterable
@dataclass(frozen=True, slots=True)
class AnimationScriptTarget:
    target_id: str; display_name: str; resource_name: str; description: str
    mode: str = "additive"; family: str = "custom"; default_pack_name: str = "common_anims_sp_pc.rpack"
    def to_dict(self)->dict[str,str]: return asdict(self)
BUILTIN_SCRIPT_TARGETS=(
 AnimationScriptTarget("npc_male_dlc60","Male NPC / infected (DLC60 additive)","anims_man_all_DLC60","Known-working additive animation-script resource used by the editor test packs. Recommended for new standalone packs.","additive","npc_male"),
 AnimationScriptTarget("npc_male_base","Male NPC / infected (base override)","anims_man_all","Overrides/imports into the base male NPC animation script. Use append mode with a preserved script whenever possible.","override","npc_male"),
 AnimationScriptTarget("player_male","Male player (DLC60 additive)","anims_player_dlc60","Male player DLC60 animation script. The target rig/template must also match the player skeleton selected in the project.","additive","player_male"),
 AnimationScriptTarget("npc_female","Female NPC","anims_woman_all","Female NPC animation script. Select a compatible female target rig or mapping profile before export.","override","npc_female"),
)
class ScriptTargetRegistry:
    def __init__(self,targets:Iterable[AnimationScriptTarget]=()):
        self._targets={target.target_id:target for target in BUILTIN_SCRIPT_TARGETS}
        for target in targets:self._targets[target.target_id]=target
    @property
    def targets(self):return tuple(self._targets.values())
    def by_id(self,target_id):return self._targets.get(target_id)
    def by_resource_name(self,resource_name):
        lowered=resource_name.strip().lower();return next((target for target in self._targets.values() if target.resource_name.lower()==lowered),None)
    def resolve_resource_name(self,value):
        target=self.by_id(value);return target.resource_name if target is not None else value.strip()
    def add(self,target):
        if not target.target_id.strip():raise ValueError("script target id cannot be empty")
        if not target.resource_name.strip():raise ValueError("animation script resource name cannot be empty")
        self._targets[target.target_id]=target
    @classmethod
    def load(cls,path):
        source=Path(path)
        if not source.exists():return cls()
        payload=json.loads(source.read_text(encoding='utf-8'));rows=payload.get('targets',payload) if isinstance(payload,dict) else payload
        if not isinstance(rows,list):raise ValueError('script target file must contain a list of targets')
        return cls(AnimationScriptTarget(**row) for row in rows)
    def save_user_targets(self,path):
        destination=Path(path);destination.parent.mkdir(parents=True,exist_ok=True);builtins={target.target_id for target in BUILTIN_SCRIPT_TARGETS};rows=[target.to_dict() for target in self._targets.values() if target.target_id not in builtins];destination.write_text(json.dumps({'schema_version':1,'targets':rows},indent=2)+'\n',encoding='utf-8');return destination
DEFAULT_SCRIPT_TARGET_ID='npc_male_dlc60'
__all__=['AnimationScriptTarget','BUILTIN_SCRIPT_TARGETS','DEFAULT_SCRIPT_TARGET_ID','ScriptTargetRegistry']
