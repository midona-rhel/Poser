# Bone link catalog

## Purpose

`BoneLinkCatalog` defines canonical bone-name groups that receive the same
manual transform delta. Current groups cover both eyes and the mutually
exclusive Viera ear-variant chains.

Linked expansion happens before gesture capture. Every concrete linked
`BoneId` is therefore an explicit command target with its own baseline,
rollback state, and history state. The native adapter disables legacy implicit
propagation so a linked bone cannot receive the same delta twice.

Missing variants are ignored by resolving names against the current
pointer-free skeleton snapshot.
