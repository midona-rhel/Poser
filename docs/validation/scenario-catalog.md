# Rewrite scenario contracts

## Selection

`selection.actor-bone-clear` resolves the controlled actor, selects it as the
sole primary target, resolves and selects a concrete bone as the sole primary
target, then clears the session.

## Actor transforms and history

`transform.actor-components` applies translation, rotation, and scale through
the clean gesture facade. Every operation is submitted twice as the same
absolute gesture update to prove it does not compound per frame. Each assertion
also proves the two untouched components stayed unchanged.

`transform.actor-undo-redo` commits one translation patch and proves exact
before/after reproduction.

## Bone pose layers

`posing.bone-components` resets one stable body bone before each translation,
rotation, and scale gesture. It inspects the resulting pose layer and proves
only the requested component changed.

`posing.animation-interference` keeps animation unfrozen, applies one persistent
rotation layer, and collects at least twelve observations from the native
post-animation pose hook. The baseline must move, the layer must remain stable,
and every evaluated transform must equal baseline composed with that layer.

`posing.reset-region` creates a real layer and proves an all-region application
command removes it.

`posing.copy-paste-pose` creates one isolated rotation, captures a portable pose,
resets the skeleton, reapplies the portable pose, and proves the stable target
received the same layer.

## Harness-owned contracts

Every iteration creates exactly one controlled clone and skeleton. Cleanup
stops its test animation, clears actor overrides, destroys the clone, and
restores the user's original selection. Actor-count or selection drift fails
`setup.cleanup`.
