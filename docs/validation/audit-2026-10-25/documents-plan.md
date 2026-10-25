# Documents comment audit follow-up

Scope: all 77 tracked C# files in `Poser.Documents` at
`04b09fd7272de82035c207e51e13a428595612bd`. The per-file record is
[`documents.csv`](documents.csv). No file declares itself generated, so no
generated-source exemption was used.

## Implementation sequence

1. **Remove legacy pose autosave writing with #635.** Split the remaining
   read/root lookup needs from PoseAutoSaveStore before removing its writing,
   retention and capture dependencies. Keep browsing old pose backups and reading
   old configuration paths. Remove the whole class only if retained readers have
   moved to an existing read-only owner. Validate scene autosaves and legacy
   browsing; deleting all legacy storage/UI is not the intended behavior.

2. **Resolve health-file ownership before changing read failures.** `AutoSaveHealthStore.Read`
   currently returns null for a missing file, malformed JSON, excessive size,
   and every I/O exception. First trace consumers after #635 removes legacy
   writing. Delete unused health storage instead of improving it. If scene
   recovery still needs it, distinguish missing from unreadable through the
   existing recovery result and UI. Check one missing and one unreadable file;
   do not expand the test plan into arbitrary corruption permutations.
   **Certainty:** high about the return behavior; need to establish retained use.

3. **Use recorded capture time for retention where available.** Legacy pose
   retention orders by filesystem last-write time, which copying or syncing can
   change. The legacy path should be deleted by #635 rather than expanded. For
   scene snapshots, keep the document capture timestamp as the display date and
   use filesystem time only for documents that predate it. Validate copied and
   synchronized files plus old documents without capture metadata. **Certainty:**
   high.

4. **Strengthen autosave metadata cache identity only if stale reads reproduce.**
   `Library/AutoSaveLibraryReader.cs` reuses metadata when length and last-write
   time match. If a real filesystem can replace content while preserving both,
   add the existing file digest to the cache key or invalidate on explicit
   refresh. Do not add hashing to every scan without reproduction because it
   would defeat the cache. Validate same-size replacement with preserved mtime
   before changing code. **Certainty:** medium; current weakness is possible but
   not reproduced in this audit.

5. **Clarify preview request equality in code.** `PosePreviewRequest` avoids
   work using its key and the same options object. If callers construct equal
   option values as new objects, the preview rebuilds. Keep this behavior unless
   profiling identifies unnecessary rebuilds; then introduce a stable options
   value key and delete reference-identity comparisons. Validate file and
   in-memory requests with repeated, changed, and equivalent options.
   **Certainty:** medium.

6. **Consolidate legacy `.cmp` read error reporting.**
   `CMToolPoseFile.Load` currently reads the complete file into a string and
   turns every I/O or JSON failure into `null`. Trace its callers and reuse the
   existing pose-file read result so UI can explain a failed read. Delete any
   second file read/error translation. Do not add an arbitrary file-size guard
   merely because the file is legacy; a limit needs a supported-file requirement
   or an observed allocation problem. Validate a real CMP and a failed read.
   **Certainty:** high about null behavior; duplication requires caller tracing.

7. **Investigate MCDF hash lookup input before adding validation.**
   `McdfHashIndex.Find` checks only the digest length, so a 64-character
   non-hex value can trigger hashing every package even though it can never
   match a SHA-256 result. Trace whether production input can contain such a
   value. If it comes only from Poser's own digest output, no additional guard
   is justified by this observation. If shared scene data supplies it, decide
   validation at that file boundary rather than spreading checks across callers.
   **Certainty:** high about the length-only check, no user failure reproduced.

8. **Define whether library traversal follows directory reparse points.**
   `LibraryScanner` recursively follows every enumerated directory and relies
   on folder/depth limits to stop cycles. Decide the product policy explicitly;
   do not silently disable linked mod/library folders. Keep current bounded
   traversal unless an actual duplicate/cycle problem demonstrates a need for
   identity-based deduplication. **Certainty:** medium; current
   traversal is bounded, but its containment behavior is implicit.

Keep wire-format constants, property names, numeric limits, and compatibility
aliases beside the serializers that enforce them. Update the existing files and
the current normative file-transfer documentation when behavior changes; do not
create class-specific documents.
