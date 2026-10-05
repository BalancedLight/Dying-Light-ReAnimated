# Player FPP and TPP resource assignment

Building both model packages produces distinct `<resource>_fpp` and
`<resource>_tpp` meshes. The first-person package omits the reviewed head
geometry; the third-person package retains the full character. Building or
deploying these resources alone does not select them for the playable character.

The native player appearance script assigns those resources with `MeshFpp`,
`MeshTpp`, and `Skin`. Camera/helper declarations and `AnimScriptAlias` serve
different purposes and do not select a player mesh.

In the model setup's Export step, expand **Assign the pair to a Player
appearance**, open the current script, choose one character/outfit, and save a
new assigned script. The displayed summary lists the generated FPP/TPP resource
names and skin. It requires an explicit outfit selection, detects source changes
before export, and preserves existing files.

The same authoring operation is available through `bind-player-appearance`:

```text
DLReAnimated bind-player-appearance source.scr bound.scr hero outfit character_fpp.msh character_tpp.msh default
```

The character and appearance IDs must match one existing `Character` and
`Appearance` record. Only its FPP mesh, TPP mesh, and skin arguments change.
The command preserves the other appearances, comments, unknown calls, unlock
records, default/availability flags, line endings, and UTF-8 byte-order mark.
Missing or ambiguous bindings are rejected. The source remains intact, and an
existing output is never overwritten. The JSON result records both file hashes.

The Export selector also reports source-derived availability evidence for the
chosen record. It lists direct `AvailableOnStart`, `AvailableOnPrologue`, and
`Default` calls, plus syntactic condition calls inside `sub unlock()` whose
ordered quoted arguments identify the same character and appearance. This
includes conditions such as `PlayerLevel`, `Chapter`, and `Item`; unknown
condition names with the same source identity are shown as well. These are
source conditions shown for review; they do not establish native unlock
semantics and do not select or equip an outfit. Unknown calls remain in the
source and are preserved by the three-binding writer.

Review the output, then deploy it to the intended project's
`data/scripts/playerappearances.scr` with the normal project backup/rollback
process. When the project already overrides that script, use its current script
as the source so its existing changes are retained. Compiled model resources,
skin selections, animation-bank redirects, and the native appearance selection
must refer to the same pair. Resource names ending in `.msh` identify compiled
mesh resources; they are not paths to the portable `.dlrmodel` files.

The command authors bindings; it does not install model packages, change the
player's selected outfit, or prove a loaded resource identity. Live acceptance
still requires a fresh Player actor, both perspective resources, camera/head
visibility, animation and helper behavior, and the applicable movement tests.
Compiler retention and a successful script rewrite remain separate results.
