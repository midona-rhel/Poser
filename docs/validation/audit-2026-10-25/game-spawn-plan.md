# Spawn review follow-up — 2026-10-25

Head: `04b09fd7272de82035c207e51e13a428595612bd`. Full source reads cover the 15 pending files recorded in `game-spawn.csv`; the requested count of 16 did not match the pending inventory. Existing reviewed spawn records were not overwritten.

This pass changes comments only. No build, deployment, native mutation test, or commit was performed. These are review findings and proposed checks, not observed game failures.

## Deferred clone source (concrete missing check)

`SceneCreation.DuplicateActor` calls `ActorSpawnService.CloneActor`, then `SpawnTransaction.SpawnCloneFrom` schedules `PrepareDraw` one tick later. The callback checks the new actor's descriptor, then calls `InheritSourceCollection` with the old source address. `PenumbraIpc.InheritCollection` passes through `IntegrationActorResolution.AddressPair`, which checks only thread and nonzero addresses; `GPoseObjectTable.IndexOf` dereferences the source. The saved `appearanceSource` descriptor is checked only afterward for the appearance copy.

Proposed fix: validate the saved source descriptor before deferred collection inheritance as well as appearance copying. Keep destination validation. Regression case: remove or replace the source between spawn and the deferred callback; assert no collection read or source dereference is attempted for the stale identity. A test with source address reuse should distinguish the replacement from the original. No runtime symptom or severity is asserted without that check.

## Model redraw with unavailable lifetime hook (concrete ordering)

`ModelIdRuntimePort.Write` delegates to `ActorSpawnService.SetModelCharaId`, which checks thread but not lifetime-hook authority. `ActorModelControl` writes the model and disables drawing through the native adapter, then calls `SpawnFramePoller.PollUntil`. The poller refuses registration if the hook is unavailable. The port's immediate model-id readback can still report success.

Proposed check: with a resolving actor and unavailable lifetime authority, invoke the model-id port and record whether DisableDraw occurs without a subsequent EnableDraw. Decide whether to refuse before mutation or provide an independently safe redraw path. The game may have other redraw behavior; permanent invisibility has not been observed or claimed.

## Identity and cleanup questions requiring more evidence

- CloneActor checks that the source address currently resolves; it does not compare the wrapper to a saved lifetime. Normal SceneCreation uses the current scene wrapper. Exercise stale-wrapper/address-reuse input before treating this broader API weakness as a product defect.
- ActorManager uses address plus GameObjectId/slot to retain wrappers. Same-address, same-id replacement is not distinguished here; trace registry/finalize invalidation before proposing another generation mechanism.
- Cleanup correctly keeps unresolved records. `DeleteAll` returning true can mean retirement or pending-create resolution, not necessarily a native delete; corrected the comment rather than changing behavior.
- Production EventBus catches subscriber exceptions. Therefore hypothetical PropListChanged subscriber throws were not promoted into a spawn rollback bug. Native allocation/respawn exception paths remain untested.

## Validation

All 15 files were read fully in bounded, untruncated output, with per-file evidence in the ledger. Comment corrections address actual mismatches and unnecessary historical prose. Scope-only diff inspection checks for changed noncomment lines and whitespace errors; no runtime validation is implied.