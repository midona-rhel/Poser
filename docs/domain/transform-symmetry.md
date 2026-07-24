# Transform symmetry

## Purpose

`TransformDeltaMode` describes how one explicit gesture target consumes the
gesture's total delta:

- `Direct` applies the delta unchanged;
- `Mirrored` reflects translation across the sagittal X plane, conjugates the
  rotation delta, and preserves scale factor.

The game facade resolves `_l`/`_r` canonical partner names before `Begin` and
adds partners as explicit targets. A partner already selected directly is not
added or mirrored a second time.

Symmetry is therefore an application-level target mapping, not a hidden native
write after the primary transform.
