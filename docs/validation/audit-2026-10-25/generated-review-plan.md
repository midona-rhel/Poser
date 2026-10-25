# Generated-source comment review

Head: `04b09fd7272de82035c207e51e13a428595612bd`.

All comments in the five assigned files were inspected, including inline CSS token names and the handwritten tablet glyph comment. NativeRegistry.TryResolve and NativeOffsets/NativeData.TryResolve were read as executable logic. ColorTokens was read in full. Large signature strings, registry metadata rows, field-check arrays, and SVG path strings were classified as declarative data rather than individually verified native evidence or rendered artwork. No current offset, signature, native ABI, or icon rendering correctness is claimed by this review.

The native headers identify external `reverse-engineer-ffxiv` generators: `poser_offset_map.py` reads `patchcheck/native-fields.tsv`, `gen_registry_cs.py` reads `patchcheck/poser-registry.tsv`, and `gen_csharp.py` reads `patchcheck/manifest.tsv`. These scripts and inputs were not found in this checkout, and the sibling repository path is absent. This patch corrects generated-output comments only; it does not pretend to update those generators. Carry the corrected summaries into their templates before the next regeneration, or regeneration will overwrite them.

The NativeFields summary now includes strides, patch positions, and managed layouts instead of claiming every entry is a game field. NativeRegistry.TryResolve dispatches by Kind and pattern lookup; Status does not gate it. NativeOffsets.TryResolve extracts a signed displacement but does not prove a changed displacement is safe. NativeHealthCheck.CheckField separately reports mismatches as Moved. These distinctions are now explicit.

ColorTokens names `Picto/src/shared/styles/tokens.css` as its source. The sibling Picto file exists, but its LF-normalized SHA-256 is `8ed0242340749f5ca0add6207cf813879a02355187a801fe2f0ba45e5bc09112`, while the committed generation snapshot records `37518f5c861e68a298e1c3757c34ef447827803b1e0eefabfc38b8f970c0ad8c`. The comment now describes a recorded snapshot, not current source equality. No color values or hash constants were changed. A followup should establish a repository-owned generator and compare its output against the intended Picto revision before updating values.

TablerSvgSources has no generator header or generation command. PoserIconSources mentions a fetch script, but no such script was found in the checkout. Tabler.Get checks custom Poser glyphs before the Tabler dictionary, confirming the precedence comment. The tablet entry contains an outline, bottom bar, and crossed strokes without a browser toolbar, matching its handwritten comment. No comment change was needed there. A future icon import should record the source version, fetch command, and which entries are hand-maintained.

Validation: scoped whitespace check passed; all five files retain identical non-comment source lines. No generator, build, deployment, or runtime validation was run.
