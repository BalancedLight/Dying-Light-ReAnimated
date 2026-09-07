# Speech preview

Load the versioned DyingAudio JSON exchange, or configure a Python environment containing DyingAudio and load an SPB directly. Audio follows the editor timeline; seeking or looping also seeks the audio. Presets substitute their authored speech-safe weights while speech is active. Keying remains explicit, and Follow animation releases a temporary face pose.

The Windows DevTools Player preview resolver checks each original ASCII speech label against morph names in stored inventory order, using case-insensitive prefix matching. The **first** match wins. For example, a label `W` resolves to `w` when the inventory is `[w, wide]`, and to `wide` when it is `[wide, w]`. Diagnostics retain the literal source label, resolved name, inventory index and other prefix candidates. Explicit per-label mappings use exact target-name matching and are labeled as overrides of native lookup.

Entries exceeding 15 speech tracks are rejected rather than truncated. Missing targets and multiple labels resolving to one target also block preview. Nonzero track flags are preserved and reported; the preview does not claim their native blending behavior.

Samples linearly interpolate at the bank's frame step. Values use `minimumWeight + (sample / 254) * (maximumWeight - minimumWeight)`. This math and prefix resolver have supporting static Windows Player evidence. That evidence does **not** establish retail Game equivalence, compiled model compatibility or live speech playback. The review layer adds a neutral endpoint; native speech-manager stop/fade behavior remains separate. No module hashes, addresses or private assets are embedded in this implementation.
