# Animation-script targets (`_ANIMATION_SCR_`)

ANM2 bytes are registered for use through an animation-script resource. DL ReAnimated lets the project choose that `_ANIMATION_SCR_` resource globally or per clip.

## Built-in targets

| GUI name | Resource | Intended family | Behavior |
|---|---|---|---|
| Male NPC / infected (DLC60 additive) | `anims_man_all_DLC60` | Male NPC/infected | Known-working additive test route; safest default for new standalone packs |
| Male NPC / infected (base override) | `anims_man_all` | Male NPC/infected | Base script override/import target |
| Male player (DLC60 additive) | `anims_player_dlc60` | Male player | Player DLC60 script target; requires a matching player target rig/template |
| Female NPC | `anims_woman_all` | Female NPC | Female script target; requires a compatible female target rig/template |

The resource field is editable. Custom projects can use names such as:

```
anims_my_character_all
anims_custom_boss_all
```

## Project default and per-clip override

The Project tab defines a default. In the Animations table each clip can:

- inherit the project default;
- select another built-in target;
- type a custom resource name.

One RPack may contain several `_ANIMATION_SCR_` resources. For example, a single project can register one animation in `anims_player_dlc60` and another in `anims_woman_all`.

## Additive versus base override

`anims_man_all_DLC60` is the known working additive DLC-style route used by the development packs.

Base names such as `anims_man_all` or `anims_woman_all` are override/import targets. A **new** pack creates a minimal script containing only project sequences. Whether that resource supplements or shadows stock content depends on the game/editor resource route.

For larger override projects, prefer appending to a tool-owned pack that already contains every sequence you want to preserve.

## Sequence contents

For each clip the builder writes sequence metadata including:

```
sequence/resource name
ANM2 resource name
start frame
end frame
FPS
enabled/blend defaults
```

The same resource name is used for the `_ANIMATION_` entry and sequence registration.

## Target skeleton warning

An animation-script resource chooses a registration namespace/family. It does **not** convert the animation to that family’s skeleton. The target SMD/template/reference files must be compatible with the character that consumes the script.

## Custom target presets

The project format reserves `user_script_targets` for named presets with:

```
target_id
display_name
resource_name
description
mode
family
default_pack_name
```

The resource name itself remains the only value required by the RPack builder, so future presets do not lock projects to a fixed internal list.
