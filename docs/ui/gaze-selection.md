# Gaze selection

`GazeState` has one shared target mode (`Off`, `Forward`, `Camera`, or actor)
and a flag set describing which of Eyes, Head, and Body are driven. The Pose
inspector mirrors that model directly:

- one Mode segmented control changes the shared target source;
- three part switches add or remove driven parts;
- each part retains an independent position-lock action;
- actor mode uses the same configured display names as the scene tree.

Presenting a separate mode on each part is intentionally avoided because the
backend and game controller cannot represent different simultaneous target
modes for Eyes, Head, and Body.
