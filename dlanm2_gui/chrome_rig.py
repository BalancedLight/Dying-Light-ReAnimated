"""Portable, declarative Chrome Rig (``.crig``) target packages.

Chrome rigs contain only the skeleton and writer metadata needed to target an
existing in-game model.  They deliberately do not contain executable code or
attempt to compile an FBX mesh into Dying Light model resources.
"""

from __future__ import annotations

from dataclasses import asdict, dataclass, field
import hashlib
import io
import json
import math
import os
from pathlib import Path, PurePosixPath
import tempfile
from typing import Any, Mapping
import zipfile

from . import anm2
from .anm2_writer import build_payload_from_values
from .trackmap import dl_name_hash

CRIG_EXTENSION = ".crig"
CRIG_FORMAT = "dl-reanimated-chrome-rig"
CRIG_SCHEMA_VERSION = 1
MAX_MEMBER_BYTES = 8 * 1024 * 1024
MAX_PACKAGE_BYTES = 24 * 1024 * 1024
MAX_MEMBER_COUNT = 32
_REQUIRED_MEMBERS = frozenset({"manifest.json", "skeleton.json", "writer_profile.json", "validation.json"})
_OPTIONAL_MEMBERS = frozenset({"aliases.json", "semantic_profile.json", "preview.png", "README.md", "LICENSE.txt"})
_EXECUTABLE_SUFFIXES = frozenset({".bat", ".cmd", ".com", ".dll", ".exe", ".js", ".msi", ".ps1", ".py", ".sh"})


def _json_bytes(value: Any) -> bytes:
    return (json.dumps(value, indent=2, ensure_ascii=False, sort_keys=True) + "\n").encode("utf-8")


@dataclass(frozen=True, slots=True)
class ChromeRigBone:
    index: int
    name: str
    parent_index: int
    descriptor: int
    bind_translation: tuple[float, float, float]
    bind_rotation_wxyz: tuple[float, float, float, float]
    bind_scale: tuple[float, float, float] = (1.0, 1.0, 1.0)
    deform: bool = True
    helper: bool = False
    aliases: tuple[str, ...] = ()
    tags: tuple[str, ...] = ()

    @classmethod
    def from_dict(cls, row: Mapping[str, Any]) -> "ChromeRigBone":
        return cls(
            index=int(row["index"]), name=str(row["name"]), parent_index=int(row["parent_index"]),
            descriptor=int(row["descriptor"]),
            bind_translation=tuple(float(v) for v in row["bind_translation"]),
            bind_rotation_wxyz=tuple(float(v) for v in row["bind_rotation_wxyz"]),
            bind_scale=tuple(float(v) for v in row.get("bind_scale", (1, 1, 1))),
            deform=bool(row.get("deform", True)), helper=bool(row.get("helper", False)),
            aliases=tuple(str(v) for v in row.get("aliases", ())),
            tags=tuple(str(v) for v in row.get("tags", ())),
        )


@dataclass(frozen=True, slots=True)
class Anm2WriterProfile:
    format_version: int = anm2.FORMAT_VERSION
    unknown06: int = 1
    rotation_encoding: str = "cayley_xyz"
    component_order: tuple[str, ...] = ("rx", "ry", "rz", "tx", "ty", "tz", "sx", "sy", "sz")
    coordinate_convention: str = "fbx_local_column_vectors_translation_meters"
    default_fps: int = 30
    default_root_policy: str = "exact"

    @classmethod
    def from_dict(cls, row: Mapping[str, Any]) -> "Anm2WriterProfile":
        return cls(
            format_version=int(row.get("format_version", anm2.FORMAT_VERSION)),
            unknown06=int(row.get("unknown06", 1)), rotation_encoding=str(row.get("rotation_encoding", "cayley_xyz")),
            component_order=tuple(str(v) for v in row.get("component_order", ())),
            coordinate_convention=str(row.get("coordinate_convention", "")),
            default_fps=int(row.get("default_fps", 30)), default_root_policy=str(row.get("default_root_policy", "exact")),
        )


@dataclass(frozen=True, slots=True)
class ChromeRigValidation:
    errors: tuple[str, ...] = ()
    warnings: tuple[str, ...] = ()
    @property
    def ok(self) -> bool: return not self.errors
    def require_valid(self) -> None:
        if self.errors: raise ValueError("Invalid Chrome Rig:\n- " + "\n- ".join(self.errors))


@dataclass(slots=True)
class ChromeRig:
    rig_id: str
    name: str
    category: str
    bones: tuple[ChromeRigBone, ...]
    root_index: int
    writer_profile: Anm2WriterProfile = field(default_factory=Anm2WriterProfile)
    extra_track_descriptors: tuple[int, ...] = ()
    track_descriptors: tuple[int, ...] = ()
    description: str = ""
    author: str = ""
    license: str = ""
    source_model_name: str = ""
    extensions: dict[str, Any] = field(default_factory=dict)

    @property
    def descriptors(self) -> list[int]:
        return list(self.track_descriptors) if self.track_descriptors else [bone.descriptor for bone in self.bones] + list(self.extra_track_descriptors)

    @property
    def skeleton_hash(self) -> str: return hashlib.sha256(_json_bytes(self._skeleton_payload())).hexdigest()

    def _skeleton_payload(self) -> dict[str, Any]:
        return {"bones": [asdict(bone) for bone in self.bones], "extra_track_descriptors": list(self.extra_track_descriptors), "track_descriptors": list(self.descriptors), "root_index": self.root_index}

    def validate(self, *, test_writer_capacity: bool = True) -> ChromeRigValidation:
        errors: list[str] = []; warnings: list[str] = []
        if not self.name.strip(): errors.append("Rig name cannot be empty.")
        if not self.bones:
            errors.append("The rig has no bones. Add at least one skinned root bone.")
            return ChromeRigValidation(tuple(errors), tuple(warnings))
        indices = [bone.index for bone in self.bones]
        if indices != list(range(len(self.bones))): errors.append("Bone indices must be contiguous and follow track order.")
        names = [bone.name for bone in self.bones]
        if any(not name for name in names): errors.append("Bone names cannot be empty.")
        if len(set(names)) != len(names): errors.append("Bone names must be unique.")
        roots = [bone.index for bone in self.bones if bone.parent_index < 0]
        if self.root_index not in indices or self.root_index not in roots: errors.append("root_index must identify a root bone.")
        if len(roots) > 1: warnings.append(f"Rig contains {len(roots)} root bones; {self.root_index} is primary.")
        for bone in self.bones:
            if bone.parent_index >= len(self.bones) or bone.parent_index == bone.index: errors.append(f"Bone {bone.name!r} has invalid parent index {bone.parent_index}.")
            values = (*bone.bind_translation, *bone.bind_rotation_wxyz, *bone.bind_scale)
            if not all(math.isfinite(value) for value in values): errors.append(f"Bone {bone.name!r} has non-finite bind values.")
            norm = math.sqrt(sum(value * value for value in bone.bind_rotation_wxyz))
            if not math.isfinite(norm) or abs(norm - 1.0) > 1.0e-4: errors.append(f"Bone {bone.name!r} has a non-unit bind quaternion.")
            if any(value <= 0.0 for value in bone.bind_scale): errors.append(f"Bone {bone.name!r} has zero or negative bind scale.")
            if max(bone.bind_scale) - min(bone.bind_scale) > 1.0e-5: warnings.append(f"Bone {bone.name!r} uses non-uniform bind scale.")
            if not bone.name.isascii(): warnings.append(f"Bone {bone.name!r} is non-ASCII; its explicit .crig descriptor will be used.")
        parents = {bone.index: bone.parent_index for bone in self.bones}
        for bone in self.bones:
            seen: set[int] = set(); cursor = bone.index
            while cursor >= 0 and cursor in parents:
                if cursor in seen:
                    errors.append(f"Bone hierarchy contains a cycle at {bone.name!r}."); break
                seen.add(cursor); cursor = parents[cursor]
        descriptor_names: dict[int, str] = {}
        for bone in self.bones:
            if bone.name.isascii():
                expected = dl_name_hash(bone.name)
                if bone.descriptor != expected: warnings.append(f"Bone {bone.name!r} uses descriptor 0x{bone.descriptor:08X}; the generated name hash is 0x{expected:08X}.")
            else:
                warnings.append(
                    f"Bone {bone.name!r} is non-ASCII and uses the explicit descriptor "
                    f"0x{bone.descriptor:08X} from this .crig."
                )
            previous = descriptor_names.get(bone.descriptor)
            if previous is not None and previous != bone.name: errors.append(f"Descriptor collision: {previous!r} and {bone.name!r} both hash to 0x{bone.descriptor:08X}.")
            descriptor_names[bone.descriptor] = bone.name
        all_descriptors = self.descriptors
        if len(set(all_descriptors)) != len(all_descriptors): errors.append("Track descriptors must be unique, including extra tracks.")
        declared_descriptors = {bone.descriptor for bone in self.bones}.union(self.extra_track_descriptors)
        if set(all_descriptors) != declared_descriptors: errors.append("Track order must contain every bone and extra descriptor exactly once.")
        profile = self.writer_profile
        if profile.format_version != anm2.FORMAT_VERSION: errors.append(f"Unsupported ANM2 format version {profile.format_version}.")
        if profile.rotation_encoding != "cayley_xyz": errors.append(f"Unsupported rotation encoding {profile.rotation_encoding!r}.")
        if profile.component_order != ("rx", "ry", "rz", "tx", "ty", "tz", "sx", "sy", "sz"): errors.append("Unsupported ANM2 component order.")
        if not 1 <= profile.default_fps <= 240: errors.append("Default FPS must be between 1 and 240.")
        if test_writer_capacity and not errors:
            try:
                header = self.make_header(frame_count=2); rows = [self.bind_track_values() for _ in range(2)]
                build_payload_from_values(header, all_descriptors, rows, [[False] * 9 for _ in all_descriptors])
            except ValueError as exc: errors.append(f"Rig cannot fit the ANM2 writer layout: {exc}")
        return ChromeRigValidation(tuple(dict.fromkeys(errors)), tuple(dict.fromkeys(warnings)))

    def make_header(self, *, frame_count: int) -> anm2.Anm2Header:
        return anm2.Anm2Header(self.writer_profile.format_version, self.writer_profile.unknown06, int(frame_count), len(self.descriptors), 0, 0, 0, 0, 0, 0)

    def bind_track_values(self) -> list[list[float]]:
        from .oracle.smd_bind_pose import anm2_cayley_vector_from_quaternion
        rows=[]; by_descriptor={bone.descriptor: bone for bone in self.bones}
        for descriptor in self.descriptors:
            bone=by_descriptor.get(descriptor)
            if bone is None: rows.append([0.,0.,0.,0.,0.,0.,1.,1.,1.]); continue
            rotation=anm2_cayley_vector_from_quaternion(bone.bind_rotation_wxyz)
            rows.append([*map(float, rotation), *map(float, bone.bind_translation), *map(float, bone.bind_scale)])
        return rows

    def to_bytes(self, *, optional_members: Mapping[str, bytes] | None = None) -> bytes:
        validation=self.validate(); validation.require_valid(); skeleton=self._skeleton_payload(); writer=asdict(self.writer_profile)
        manifest={"format":CRIG_FORMAT,"schema_version":CRIG_SCHEMA_VERSION,"rig_id":self.rig_id,"name":self.name,"category":self.category,"description":self.description,"author":self.author,"license":self.license,"source_model_name":self.source_model_name,"bone_count":len(self.bones),"track_count":len(self.descriptors),"skeleton_sha256":hashlib.sha256(_json_bytes(skeleton)).hexdigest(),"writer_profile_sha256":hashlib.sha256(_json_bytes(writer)).hexdigest(),"extensions":self.extensions}
        members={"manifest.json":_json_bytes(manifest),"skeleton.json":_json_bytes(skeleton),"writer_profile.json":_json_bytes(writer),"validation.json":_json_bytes(asdict(validation))}
        for name,value in dict(optional_members or {}).items():
            if name not in _OPTIONAL_MEMBERS: raise ValueError(f"Unsupported optional Chrome Rig member: {name}")
            members[name]=bytes(value)
        output=io.BytesIO()
        with zipfile.ZipFile(output,"w",compression=zipfile.ZIP_STORED) as archive:
            for name in sorted(members):
                info=zipfile.ZipInfo(name,date_time=(1980,1,1,0,0,0)); info.compress_type=zipfile.ZIP_STORED; info.create_system=0; info.external_attr=0o600<<16; archive.writestr(info,members[name])
        return output.getvalue()

    def save(self, path: str | Path, *, optional_members: Mapping[str, bytes] | None = None) -> Path:
        destination=Path(path)
        if destination.suffix.lower()!=CRIG_EXTENSION: destination=destination.with_suffix(CRIG_EXTENSION)
        destination.parent.mkdir(parents=True,exist_ok=True); payload=self.to_bytes(optional_members=optional_members)
        handle,temporary=tempfile.mkstemp(prefix=destination.name+".",suffix=".tmp",dir=destination.parent)
        try:
            with os.fdopen(handle,"wb") as stream: stream.write(payload); stream.flush(); os.fsync(stream.fileno())
            os.replace(temporary,destination)
        finally:
            if os.path.exists(temporary): os.unlink(temporary)
        return destination

    @classmethod
    def load(cls,path:str|Path)->"ChromeRig":
        source=Path(path); return cls.from_bytes(source.read_bytes(),source_name=str(source))

    @classmethod
    def from_bytes(cls,payload:bytes,*,source_name:str="<memory>")->"ChromeRig":
        if len(payload)>MAX_PACKAGE_BYTES: raise ValueError(f"Chrome Rig package is too large: {source_name}")
        try: archive=zipfile.ZipFile(io.BytesIO(payload))
        except zipfile.BadZipFile as exc: raise ValueError(f"Chrome Rig is not a valid ZIP container: {source_name}") from exc
        with archive:
            infos=archive.infolist()
            if len(infos)>MAX_MEMBER_COUNT: raise ValueError("Chrome Rig contains too many members")
            names=set(); total=0
            for info in infos:
                path=PurePosixPath(info.filename)
                if path.is_absolute() or ".." in path.parts or len(path.parts)!=1: raise ValueError(f"Unsafe Chrome Rig member path: {info.filename!r}")
                if info.filename in names: raise ValueError(f"Duplicate Chrome Rig member: {info.filename}")
                names.add(info.filename); total+=int(info.file_size)
                if info.file_size>MAX_MEMBER_BYTES or total>MAX_PACKAGE_BYTES: raise ValueError("Chrome Rig member size limit exceeded")
                if path.suffix.lower() in _EXECUTABLE_SUFFIXES: raise ValueError(f"Executable content is not allowed in Chrome Rigs: {info.filename}")
            missing=sorted(_REQUIRED_MEMBERS-names)
            if missing: raise ValueError(f"Chrome Rig is missing required members: {', '.join(missing)}")
            unknown=sorted(names-_REQUIRED_MEMBERS-_OPTIONAL_MEMBERS)
            if unknown: raise ValueError(f"Chrome Rig contains unsupported members: {', '.join(unknown)}")
            manifest=json.loads(archive.read("manifest.json")); skeleton=json.loads(archive.read("skeleton.json")); writer=json.loads(archive.read("writer_profile.json"))
        if not isinstance(manifest,dict) or not isinstance(skeleton,dict) or not isinstance(writer,dict): raise ValueError("Chrome Rig core JSON members must contain objects")
        if manifest.get("format")!=CRIG_FORMAT: raise ValueError(f"Unsupported Chrome Rig format: {manifest.get('format')!r}")
        version=int(manifest.get("schema_version",0))
        if version!=CRIG_SCHEMA_VERSION: raise ValueError(f"Unsupported Chrome Rig schema version {version}")
        if hashlib.sha256(_json_bytes(skeleton)).hexdigest()!=manifest.get("skeleton_sha256"): raise ValueError("Chrome Rig skeleton hash does not match its manifest")
        if hashlib.sha256(_json_bytes(writer)).hexdigest()!=manifest.get("writer_profile_sha256"): raise ValueError("Chrome Rig writer profile hash does not match its manifest")
        rig=cls(rig_id=str(manifest.get("rig_id","")),name=str(manifest.get("name","")),category=str(manifest.get("category","Other")),bones=tuple(ChromeRigBone.from_dict(row) for row in skeleton.get("bones",())),root_index=int(skeleton.get("root_index",-1)),writer_profile=Anm2WriterProfile.from_dict(writer),extra_track_descriptors=tuple(int(value) for value in skeleton.get("extra_track_descriptors",())),track_descriptors=tuple(int(value) for value in skeleton.get("track_descriptors",())),description=str(manifest.get("description","")),author=str(manifest.get("author","")),license=str(manifest.get("license","")),source_model_name=str(manifest.get("source_model_name","")),extensions=dict(manifest.get("extensions",{})))
        rig.validate().require_valid()
        if int(manifest.get("bone_count",-1))!=len(rig.bones): raise ValueError("Chrome Rig bone count does not match its manifest")
        if int(manifest.get("track_count",-1))!=len(rig.descriptors): raise ValueError("Chrome Rig track count does not match its manifest")
        return rig

__all__=["Anm2WriterProfile","CRIG_EXTENSION","CRIG_FORMAT","CRIG_SCHEMA_VERSION","ChromeRig","ChromeRigBone","ChromeRigValidation"]
