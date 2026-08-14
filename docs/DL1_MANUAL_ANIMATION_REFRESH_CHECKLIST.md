# DL1 manual animation refresh checklist

Manual testing begins only after the offline-validated build is handed off. ReAnimated never launches, attaches to, injects into, or controls a Dying Light process. Offline compiler, package, request, and loader receipts are not live playback proof.

## Local loader switch

- [ ] Confirm `[AnimationRefresh] Enabled=1`; this is the shipped default.
- [ ] Keep `[AnimationRefresh] Route=ProjectRPack` for the normal test.
- [ ] Deploy from ReAnimated and confirm it automatically writes the bounded refresh request; use **Refresh / retry animations** only when retrying or changing route.
- [ ] Set `Enabled=0` only if you intentionally want to disable automatic refresh.

## Developer Tools Editor

- [ ] Start the Editor yourself and open the intended project.
- [ ] Check selected-model match, runtime-pack registration, script-bank resolution, bind creation, clip resolution, and inspector rebuild stages.
- [ ] Confirm the Refresh menu contains **Reload Animations (Selected Model)**.
- [ ] Press Shift+Alt+A and confirm it uses the selected-model reload without invoking the stock global reload.
- [ ] Record the visible animation row count.
- [ ] Select two clips and confirm that each visibly changes the pose.
- [ ] Deploy one new clip, use **Refresh Developer Tools animations**, and confirm the row count increases.
- [ ] To compare the advanced Editor-only route, set the loader's local `[AnimationRefresh] Route=RawLoose`, select `RawLoose` in ReAnimated, and write a new refresh request. Restore both selections to `ProjectRPack` afterward.

## ReAnimated model-first workflow

- [ ] In an untitled project, import a custom FBX and confirm the model appears immediately in **Project models** without changing the active animation target.
- [ ] Confirm every valid embedded stack appears immediately under **Animations** with a direct same-rig variant.
- [ ] Confirm **Back to project models** returns to the combined model browser.
- [ ] Confirm model/package/compiler/deployment controls are under **Export**, not the custom-model authoring view.
- [ ] Save the project and confirm the recovery-staged `.dlrmodel` materializes without changing model, source, or variant identities.

## DyingLightPlayer (optional manual check)

- [ ] Start DyingLightPlayer yourself; do not use DyingLightGame for this checklist.
- [ ] Test `ProjectRPack` and record whether the intended clip visibly plays.
- [ ] `RawLoose` is Editor-only; a Player request for it must report `Unsupported`.
- [ ] Confirm the tested valid animation reports no invalid-bind suppression.

## Handoff

- [ ] Collect the diagnostic bundle whenever useful. Collection only reads existing project receipts and loader logs; it does not inspect, launch, attach to, inject into, or control a host process.
- [ ] Include the selected route, visible row count, first failed step, and diagnostic bundle when reporting a failure.
