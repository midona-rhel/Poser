# Bone metadata

## Purpose

`BoneInfoService` is the retained static catalog that maps native bone names to
an English label, broad `BoneCategory`, and optional `BoneSubcategory`. The
sidebar and Matrix pane use it for display and grouping; pose mutation does not
depend on the catalog.

The catalog is compiled from the focused registration files under
`PosingCore/Core/BoneInfo/Categories`. The former second XML category model was
unreachable and has been removed.

## API

- `Initialize` rebuilds the catalog and attaches the diagnostic logger.
- `GetTranslation`, `GetDisplayName`, and `GetBoneData` project labels.
- `GetCategory` and `GetSubcategory` project grouping.
- `GetCategoryRootBone` and `GetSubcategoryRootBone` provide optional selection
  roots for known groups.

Unknown names remain visible using their native name, are grouped under
`Other`, and produce at most one warning per session.

## Ownership

`BoneInfoService` owns descriptive metadata only. Native bone identity comes
from the skeleton and `StableBindingRegistry`; graphical Body/Face coordinates
come from `GraphicalBoneReader`. These sources must not be merged into a second
mutable bone model.

## Brio reference

Brio uses an embedded category/localization dataset. Poser currently keeps the
smaller compiled English catalog because the retained UI needs deterministic
grouping and has no localization workflow. A future data-driven replacement
must preserve the same lookup API and receive a focused live catalog
diagnostic; it must not become a general game-data service.

## Risks and validation

Registration order is last-writer-wins for duplicate names, and modded
skeletons may expose unknown bones. Real coverage is observed in game through
the sidebar/Matrix projection and the live harness snapshots. Catalog integrity
may be added as a narrow diagnostic but is not part of the seven transform
acceptance contracts.
