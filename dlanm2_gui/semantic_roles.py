"""Unicode-safe bone-name evidence for automatic skeleton analysis.

Names are deliberately treated as evidence rather than identity.  This module
preserves the original display name, builds deterministic comparison forms,
and exposes a versioned multilingual anatomy lexicon.  Topology and bind-space
geometry are consumed by :mod:`dlanm2_gui.skeleton_analysis` before any
archetype is accepted.
"""

from __future__ import annotations

from dataclasses import dataclass
import re
import unicodedata
from typing import Any, Iterable, Mapping


SEMANTIC_LEXICON_VERSION = "dlr-multilingual-anatomy-v1"
NAME_NORMALIZER_VERSION = "dlr-unicode-bone-name-v1"


# The values are data, not language-specific branches in the analyzer.  The
# vocabulary intentionally covers anchors and major chains; topology resolves
# subdivisions and optional/terminal bones.
MULTILINGUAL_ANATOMY_LEXICON: Mapping[str, Mapping[str, tuple[str, ...]]] = {
    "root": {
        "en": ("root", "motion root", "armature root"),
        "es": ("raiz", "raíz"),
        "fr": ("racine",),
        "de": ("wurzel",),
        "pl": ("korzen", "korzeń"),
        "pt": ("raiz",),
        "it": ("radice",),
        "ru": ("корень",),
        "uk": ("корінь",),
        "zh": ("根", "根骨"),
        "ja": ("ルート", "根"),
        "ko": ("루트", "뿌리"),
    },
    "pelvis": {
        "en": ("pelvis", "hips", "hip"),
        "es": ("pelvis", "caderas", "cadera"),
        "fr": ("bassin", "hanches", "hanche"),
        "de": ("becken", "hüfte", "huefte"),
        "pl": ("miednica", "biodra", "biodro"),
        "pt": ("pelve", "quadril", "ancas"),
        "it": ("bacino", "fianchi", "fianco"),
        "ru": ("таз", "бедра"),
        "uk": ("таз", "стегна"),
        "zh": ("骨盆", "髋", "髖"),
        "ja": ("骨盤", "腰"),
        "ko": ("골반", "엉덩이"),
    },
    "spine": {
        "en": ("spine", "backbone", "waist"),
        "es": ("columna", "espina"),
        "fr": ("colonne", "dos"),
        "de": ("wirbelsäule", "wirbelsaeule", "rücken", "ruecken"),
        "pl": ("kręgosłup", "kregoslup"),
        "pt": ("coluna",),
        "it": ("colonna", "schiena"),
        "ru": ("позвоночник", "спина"),
        "uk": ("хребет", "спина"),
        "zh": ("脊柱", "脊椎"),
        "ja": ("背骨", "脊椎"),
        "ko": ("척추", "등뼈"),
    },
    "chest": {
        "en": ("chest", "upper chest", "thorax"),
        "es": ("pecho", "torax", "tórax"),
        "fr": ("poitrine", "thorax"),
        "de": ("brust", "thorax"),
        "pl": ("klatka", "pierś", "piers"),
        "pt": ("peito", "tórax", "torax"),
        "it": ("torace", "petto"),
        "ru": ("грудь", "грудная клетка"),
        "uk": ("груди", "грудна клітка"),
        "zh": ("胸", "胸部"),
        "ja": ("胸", "胸部"),
        "ko": ("가슴", "흉부"),
    },
    "neck": {
        "en": ("neck",),
        "es": ("cuello",),
        "fr": ("cou",),
        "de": ("hals", "nacken"),
        "pl": ("szyja",),
        "pt": ("pescoço", "pescoco"),
        "it": ("collo",),
        "ru": ("шея",),
        "uk": ("шия",),
        "zh": ("颈", "頸", "脖子"),
        "ja": ("首", "頸"),
        "ko": ("목",),
    },
    "head": {
        "en": ("head", "skull"),
        "es": ("cabeza", "craneo", "cráneo"),
        "fr": ("tête", "tete", "crâne", "crane"),
        "de": ("kopf", "schädel", "schaedel"),
        "pl": ("głowa", "glowa", "czaszka"),
        "pt": ("cabeça", "cabeca", "crânio", "cranio"),
        "it": ("testa", "capo", "cranio"),
        "ru": ("голова", "череп"),
        "uk": ("голова", "череп"),
        "zh": ("头", "頭", "头部", "頭部"),
        "ja": ("頭", "頭部"),
        "ko": ("머리", "두개골"),
    },
    "clavicle": {
        "en": ("clavicle", "collarbone", "shoulder"),
        "es": ("clavicula", "clavícula", "hombro"),
        "fr": ("clavicule", "épaule", "epaule"),
        "de": ("schlüsselbein", "schluesselbein", "schulter"),
        "pl": ("obojczyk", "bark"),
        "pt": ("clavícula", "clavicula", "ombro"),
        "it": ("clavicola", "spalla"),
        "ru": ("ключица", "плечо"),
        "uk": ("ключиця", "плече"),
        "zh": ("锁骨", "鎖骨", "肩", "肩膀"),
        "ja": ("鎖骨", "肩"),
        "ko": ("쇄골", "어깨"),
    },
    "upper_arm": {
        "en": ("upper arm", "upperarm", "arm"),
        "es": ("brazo superior", "brazo"),
        "fr": ("bras supérieur", "bras superieur", "bras"),
        "de": ("oberarm", "arm"),
        "pl": ("ramię", "ramie"),
        "pt": ("braço superior", "braco superior", "braço", "braco"),
        "it": ("braccio superiore", "braccio"),
        "ru": ("верхняя рука", "плечевая кость"),
        "uk": ("верхня рука", "плечова кістка"),
        "zh": ("上臂", "大臂"),
        "ja": ("上腕",),
        "ko": ("위팔", "상완"),
    },
    "forearm": {
        "en": ("forearm", "fore arm", "lower arm", "lowerarm"),
        "es": ("antebrazo",),
        "fr": ("avant bras", "avant-bras"),
        "de": ("unterarm",),
        "pl": ("przedramię", "przedramie"),
        "pt": ("antebraço", "antebraco"),
        "it": ("avambraccio",),
        "ru": ("предплечье",),
        "uk": ("передпліччя",),
        "zh": ("前臂", "小臂"),
        "ja": ("前腕",),
        "ko": ("아래팔", "전완"),
    },
    "hand": {
        "en": ("hand", "wrist"),
        "es": ("mano", "muñeca", "muneca"),
        "fr": ("main", "poignet"),
        "de": ("hand", "handgelenk"),
        "pl": ("dłoń", "dlon", "nadgarstek"),
        "pt": ("mão", "mao", "pulso"),
        "it": ("mano", "polso"),
        "ru": ("кисть", "запястье"),
        "uk": ("кисть", "зап'ястя"),
        "zh": ("手", "手腕"),
        "ja": ("手", "手首"),
        "ko": ("손", "손목"),
    },
    "thigh": {
        "en": ("thigh", "upper leg", "upperleg", "upleg"),
        "es": ("muslo", "pierna superior"),
        "fr": ("cuisse", "haut de jambe"),
        "de": ("oberschenkel",),
        "pl": ("udo",),
        "pt": ("coxa",),
        "it": ("coscia",),
        "ru": ("бедро",),
        "uk": ("стегно",),
        "zh": ("大腿", "上腿"),
        "ja": ("太もも", "大腿"),
        "ko": ("허벅지", "대퇴"),
    },
    "calf": {
        "en": ("calf", "shin", "lower leg", "lowerleg", "leg"),
        "es": ("pantorrilla", "espinilla", "pierna"),
        "fr": ("mollet", "jambe"),
        "de": ("unterschenkel", "wade"),
        "pl": ("łydka", "lydka", "podudzie"),
        "pt": ("panturrilha", "canela"),
        "it": ("polpaccio", "gamba"),
        "ru": ("голень", "икра"),
        "uk": ("гомілка", "литка"),
        "zh": ("小腿", "下腿"),
        "ja": ("ふくらはぎ", "下腿"),
        "ko": ("종아리", "하퇴"),
    },
    "foot": {
        "en": ("foot", "ankle"),
        "es": ("pie", "tobillo"),
        "fr": ("pied", "cheville"),
        "de": ("fuß", "fuss", "knöchel", "knoechel"),
        "pl": ("stopa", "kostka"),
        "pt": ("pé", "pe", "tornozelo"),
        "it": ("piede", "caviglia"),
        "ru": ("стопа", "лодыжка"),
        "uk": ("стопа", "щиколотка"),
        "zh": ("脚", "腳", "足", "脚踝"),
        "ja": ("足", "足首"),
        "ko": ("발", "발목"),
    },
    "toe": {
        "en": ("toe", "toes", "toe base", "ball"),
        "es": ("dedo del pie", "dedos del pie"),
        "fr": ("orteil", "orteils"),
        "de": ("zeh", "zehe", "zehen"),
        "pl": ("palec stopy", "palce stopy"),
        "pt": ("dedo do pé", "dedo do pe"),
        "it": ("dito del piede", "alluce"),
        "ru": ("палец ноги", "носок"),
        "uk": ("палець ноги", "носок"),
        "zh": ("脚趾", "腳趾", "趾"),
        "ja": ("つま先", "足指"),
        "ko": ("발가락", "발끝"),
    },
    "finger": {
        "en": ("finger", "thumb", "index", "middle", "ring", "pinky"),
        "es": ("dedo", "pulgar", "indice", "índice", "meñique", "menique"),
        "fr": ("doigt", "pouce", "index", "auriculaire"),
        "de": ("finger", "daumen", "zeigefinger", "kleiner finger"),
        "pl": ("palec", "kciuk"),
        "pt": ("dedo", "polegar"),
        "it": ("dito", "pollice"),
        "ru": ("палец", "большой палец"),
        "uk": ("палець", "великий палець"),
        "zh": ("手指", "拇指", "食指"),
        "ja": ("指", "親指"),
        "ko": ("손가락", "엄지"),
    },
    "face": {
        "en": ("face", "facial", "jaw", "brow", "eye", "lip", "tongue"),
        "es": ("cara", "mandibula", "mandíbula", "ceja", "ojo", "labio"),
        "fr": ("visage", "mâchoire", "machoire", "sourcil", "oeil", "lèvre"),
        "de": ("gesicht", "kiefer", "augenbraue", "auge", "lippe"),
        "pl": ("twarz", "szczęka", "szczeka", "brew", "oko", "warga"),
        "pt": ("rosto", "mandíbula", "mandibula", "sobrancelha", "olho", "lábio"),
        "it": ("viso", "mascella", "sopracciglio", "occhio", "labbro"),
        "ru": ("лицо", "челюсть", "бровь", "глаз", "губа"),
        "uk": ("обличчя", "щелепа", "брова", "око", "губа"),
        "zh": ("脸", "臉", "下巴", "眉", "眼", "唇"),
        "ja": ("顔", "顎", "眉", "目", "唇"),
        "ko": ("얼굴", "턱", "눈썹", "눈", "입술"),
    },
}


SIDE_LEXICON: Mapping[str, Mapping[str, tuple[str, ...]]] = {
    "left": {
        "en": ("left", "l"), "es": ("izquierda", "izquierdo"),
        "fr": ("gauche",), "de": ("links", "linke", "linker"),
        "pl": ("lewy", "lewa", "lewe"), "pt": ("esquerda", "esquerdo"),
        "it": ("sinistra", "sinistro"), "ru": ("левый", "левая", "лев"),
        "uk": ("лівий", "ліва", "лів"), "zh": ("左",),
        "ja": ("左",), "ko": ("왼쪽", "좌"),
    },
    "right": {
        "en": ("right", "r"), "es": ("derecha", "derecho"),
        "fr": ("droite", "droit"), "de": ("rechts", "rechte", "rechter"),
        "pl": ("prawy", "prawa", "prawe"), "pt": ("direita", "direito"),
        "it": ("destra", "destro"), "ru": ("правый", "правая", "прав"),
        "uk": ("правий", "права", "прав"), "zh": ("右",),
        "ja": ("右",), "ko": ("오른쪽", "우"),
    },
}


FINGER_LEXICON: Mapping[str, tuple[str, ...]] = {
    "thumb": (
        "thumb", "pollex", "pulgar", "pouce", "daumen", "kciuk", "polegar",
        "pollice", "большой палец", "великий палець", "拇指", "親指", "엄지",
    ),
    "index": (
        "index", "index finger", "indice", "índice", "zeigefinger",
        "wskazujący", "wskazujacy", "indicador", "указательный", "вказівний",
        "食指", "人差し指", "검지",
    ),
    "middle": (
        "middle", "mid", "middle finger", "majeur", "mittelfinger", "środkowy",
        "srodkowy", "medio", "средний", "середній", "中指", "가운데",
    ),
    "ring": (
        "ring", "ring finger", "annulaire", "ringfinger", "serdeczny", "anular",
        "anulare", "безымянный", "безіменний", "无名指", "無名指", "薬指", "약지",
    ),
    "pinky": (
        "pinky", "pinkie", "little finger", "small finger", "auriculaire",
        "kleiner finger", "mały palec", "maly palec", "mindinho", "mignolo",
        "мизинец", "мізинець", "小指", "새끼",
    ),
}


_RIG_PREFIX_TOKENS = frozenset({
    "armature", "skeleton", "rig", "mixamorig", "def", "org", "mch",
    "jnt", "joint", "bn", "b", "bone", "drv", "deform",
})
_RIG_SUFFIX_TOKENS = frozenset({"jnt", "joint", "bone", "def", "drv"})
_HELPER_TOKENS = frozenset({
    "helper", "control", "ctrl", "controller", "ik", "fk", "pole", "target",
    "socket", "holder", "attachment", "camera", "cam", "end", "tip", "nub",
    "twist", "roll", "mch", "org", "mechanism", "effector",
})


_CYRILLIC_TRANSLITERATION = str.maketrans({
    "а": "a", "б": "b", "в": "v", "г": "g", "ґ": "g", "д": "d",
    "е": "e", "ё": "yo", "є": "ye", "ж": "zh", "з": "z", "и": "i",
    "і": "i", "ї": "yi", "й": "y", "к": "k", "л": "l", "м": "m",
    "н": "n", "о": "o", "п": "p", "р": "r", "с": "s", "т": "t",
    "у": "u", "ф": "f", "х": "kh", "ц": "ts", "ч": "ch", "ш": "sh",
    "щ": "shch", "ъ": "", "ы": "y", "ь": "", "э": "e", "ю": "yu",
    "я": "ya",
})


def _script_names(value: str) -> tuple[str, ...]:
    scripts: set[str] = set()
    for character in value:
        if not character.isalpha():
            continue
        code = ord(character)
        if 0x0400 <= code <= 0x052F:
            scripts.add("Cyrillic")
        elif 0x4E00 <= code <= 0x9FFF or 0x3400 <= code <= 0x4DBF:
            scripts.add("Han")
        elif 0x3040 <= code <= 0x309F:
            scripts.add("Hiragana")
        elif 0x30A0 <= code <= 0x30FF:
            scripts.add("Katakana")
        elif 0xAC00 <= code <= 0xD7AF or 0x1100 <= code <= 0x11FF:
            scripts.add("Hangul")
        elif "LATIN" in unicodedata.name(character, ""):
            scripts.add("Latin")
        else:
            scripts.add("Other")
    return tuple(sorted(scripts))


def safe_transliterate(value: str) -> str | None:
    """Return a comparison-only transliteration when it is not lossy fiction.

    Latin diacritics and the bundled Russian/Ukrainian Cyrillic range are
    handled offline.  Han, Kana, and Hangul are deliberately left without a
    made-up Latin form; their original Unicode comparison text remains active.
    """

    normalized = _split_camel_and_digits(
        unicodedata.normalize("NFKC", value)
    ).casefold()
    scripts = set(_script_names(normalized))
    if scripts & {"Han", "Hiragana", "Katakana", "Hangul", "Other"}:
        return None
    translated = normalized.translate(_CYRILLIC_TRANSLITERATION)
    decomposed = unicodedata.normalize("NFKD", translated)
    result = "".join(
        character
        for character in decomposed
        if unicodedata.category(character) != "Mn" and ord(character) < 128
    )
    result = re.sub(r"[^a-z0-9]+", " ", result).strip()
    return result or None


def _split_camel_and_digits(value: str) -> str:
    value = re.sub(r"(?<=[a-z0-9])(?=[A-Z])", " ", value)
    value = re.sub(r"(?<=[A-Za-z])(?=\d)|(?<=\d)(?=[A-Za-z])", " ", value)
    return value


def _comparison_key(value: str) -> str:
    value = _split_camel_and_digits(unicodedata.normalize("NFKC", value))
    value = value.casefold()
    value = value.replace("_", " ")
    value = re.sub(r"[^\w]+", " ", value, flags=re.UNICODE)
    return re.sub(r"\s+", " ", value).strip()


def _alias_matches(alias: str, key: str, tokens: tuple[str, ...]) -> bool:
    candidate = _comparison_key(alias)
    if not candidate:
        return False
    candidate_tokens = tuple(candidate.split())
    if len(candidate_tokens) == 1:
        token = candidate_tokens[0]
        if token in tokens:
            return True
        # East-Asian bone names are frequently concatenated without a word
        # separator (for example ``左上臂``).
        if any(ord(character) > 127 for character in token):
            return token in key
        return False
    width = len(candidate_tokens)
    return any(tokens[index:index + width] == candidate_tokens for index in range(len(tokens) - width + 1))


@dataclass(frozen=True, slots=True)
class NormalizedBoneName:
    original_name: str
    normalized_unicode_name: str
    namespace: str
    comparison_name: str
    transliterated_name: str | None
    semantic_tokens: tuple[str, ...]
    anatomical_roles: tuple[str, ...]
    finger: str
    side: str
    side_evidence: tuple[str, ...]
    helper_tokens: tuple[str, ...]
    ordinal: int | None
    scripts: tuple[str, ...]
    languages: tuple[str, ...]

    @property
    def likely_helper(self) -> bool:
        return bool(self.helper_tokens)

    def to_dict(self) -> dict[str, Any]:
        return {
            "original_name": self.original_name,
            "normalized_unicode_name": self.normalized_unicode_name,
            "namespace": self.namespace,
            "comparison_name": self.comparison_name,
            "transliterated_name": self.transliterated_name,
            "semantic_tokens": list(self.semantic_tokens),
            "anatomical_roles": list(self.anatomical_roles),
            "finger": self.finger,
            "side": self.side,
            "side_evidence": list(self.side_evidence),
            "helper_tokens": list(self.helper_tokens),
            "ordinal": self.ordinal,
            "scripts": list(self.scripts),
            "languages": list(self.languages),
            "normalizer_version": NAME_NORMALIZER_VERSION,
            "lexicon_version": SEMANTIC_LEXICON_VERSION,
        }


def normalize_bone_name(name: str) -> NormalizedBoneName:
    """Build deterministic Unicode and semantic comparison forms for *name*."""

    original = str(name)
    normalized_unicode = unicodedata.normalize("NFKC", original).casefold()
    namespace_parts = re.split(r"[|:]", unicodedata.normalize("NFKC", original))
    namespace = "|".join(namespace_parts[:-1]) if len(namespace_parts) > 1 else ""
    leaf = namespace_parts[-1]
    key = _comparison_key(leaf)
    raw_tokens = list(key.split())
    stripped_prefix_tokens: list[str] = []
    stripped_suffix_tokens: list[str] = []
    while raw_tokens and raw_tokens[0] in _RIG_PREFIX_TOKENS:
        stripped_prefix_tokens.append(raw_tokens.pop(0))
    while raw_tokens and raw_tokens[-1] in _RIG_SUFFIX_TOKENS:
        stripped_suffix_tokens.append(raw_tokens.pop())
    tokens = tuple(raw_tokens)
    comparison = " ".join(tokens)

    matched_roles: list[tuple[int, str]] = []
    matched_languages: set[str] = set()
    for role, language_rows in MULTILINGUAL_ANATOMY_LEXICON.items():
        best = 0
        for language, aliases in language_rows.items():
            for alias in aliases:
                if _alias_matches(alias, comparison, tokens):
                    best = max(best, len(_comparison_key(alias)))
                    matched_languages.add(language)
        if best:
            matched_roles.append((best, role))
    matched_roles.sort(key=lambda row: (-row[0], row[1]))
    roles = tuple(dict.fromkeys(role for _score, role in matched_roles))

    side_rows: list[tuple[str, str]] = []
    for side, language_rows in SIDE_LEXICON.items():
        for language, aliases in language_rows.items():
            for alias in aliases:
                if _alias_matches(alias, comparison, tokens):
                    side_rows.append((side, f"{language}:{alias}"))
                    matched_languages.add(language)
    distinct_sides = {side for side, _reason in side_rows}
    side = next(iter(distinct_sides)) if len(distinct_sides) == 1 else ""
    side_evidence = tuple(reason for candidate, reason in side_rows if candidate == side)

    helper_tokens = tuple(
        dict.fromkeys(
            token
            for token in (
                *stripped_prefix_tokens,
                *tokens,
                *reversed(stripped_suffix_tokens),
            )
            if token in _HELPER_TOKENS
        )
    )
    finger_matches = [
        digit
        for digit, aliases in FINGER_LEXICON.items()
        if any(_alias_matches(alias, comparison, tokens) for alias in aliases)
    ]
    finger = finger_matches[0] if len(finger_matches) == 1 else ""
    ordinals = [int(value) for value in re.findall(r"\d+", _comparison_key(leaf))]
    semantic_tokens = tuple(dict.fromkeys((*tokens, *roles, *([finger] if finger else []))))
    return NormalizedBoneName(
        original_name=original,
        normalized_unicode_name=normalized_unicode,
        namespace=namespace,
        comparison_name=comparison,
        transliterated_name=safe_transliterate(leaf),
        semantic_tokens=semantic_tokens,
        anatomical_roles=roles,
        finger=finger,
        side=side,
        side_evidence=side_evidence,
        helper_tokens=helper_tokens,
        ordinal=ordinals[-1] if ordinals else None,
        scripts=_script_names(original),
        languages=tuple(sorted(matched_languages)),
    )


def normalize_bone_names(names: Iterable[str]) -> tuple[NormalizedBoneName, ...]:
    return tuple(normalize_bone_name(name) for name in names)


def preferred_anatomical_role(value: NormalizedBoneName | str) -> str:
    evidence = value if isinstance(value, NormalizedBoneName) else normalize_bone_name(value)
    # The matching list is ordered by longest alias first, preventing ``arm``
    # from winning over ``forearm`` and ``leg`` over ``upper leg``.
    return evidence.anatomical_roles[0] if evidence.anatomical_roles else ""


__all__ = [
    "MULTILINGUAL_ANATOMY_LEXICON",
    "FINGER_LEXICON",
    "NAME_NORMALIZER_VERSION",
    "NormalizedBoneName",
    "SEMANTIC_LEXICON_VERSION",
    "SIDE_LEXICON",
    "normalize_bone_name",
    "normalize_bone_names",
    "preferred_anatomical_role",
    "safe_transliterate",
]
