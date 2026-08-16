# Animation

Poser keeps animation state for the current actor. Playback follows the game's
sequencer and slot routing. Poser does not create a second base-animation or
blend system. Multiple animations can layer per slot, and a held slot keeps its
speed override.

The expression picker acts immediately: one choice plays and pins the facial
layer. Changing a held expression does not recapture the restore point.
Releasing it restores the captured facial timeline. Baking turns the previewed
face into one ordinary pose-history patch and leaves body animation alone.

Looping runs on framework ticks. When a timeline ends, Poser replays an armed
slot. It does not use the unsupported forced-timeline field. Loop state belongs
to the session and the slot.

Before Poser changes an animation aspect, it captures that aspect once.
Restore gives control back only after native restore succeeds; a failure stays
owned and retryable. GPose exit, disposal, and actor reconciliation use this
same restore path. Stance restoration releases base state and loops first.

Speed uses the supported hooks and range and is cleared only when Poser owns
it. Replay releases a Poser-owned pause before playing again. Physics freeze
is one change that rolls back on partial failure. The UI shows the shared
physics state.

Stance changes use the supported native transition. Scrubbing freezes at the
start of the gesture, clamps to the captured duration, and cancels if the
skeleton changes. A pending facial bake or transform recovery blocks another
mutation. Controls show only state that the session owns.
