# Cameras

GPose always has the game's own camera, which Poser lists as the main camera. You can add more cameras from the **Cameras** tab of the spawn browser. **Camera** adds one that works like the normal GPose camera and circles around a point. **Free camera** adds one you can fly anywhere: move it with WASD and turn it by dragging with the right mouse button. You can also load a camera you saved before with **Camera from library** or **Camera from file**.

The game shows whichever camera is live. To look through a camera, select it in the sidebar and turn on **Live** in the **General** section of its **Camera** tab. If you turn on **Look through a selected camera** in Settings › Camera, selecting a camera in the sidebar makes it live.

## Orbit cameras

An orbit camera circles around a point. **Zoom** moves it closer to or further from that point, and **Angle X** and **Angle Y** move it around it. **Pan** and **Tilt** turn the view without moving the camera. **FoV** changes the field of view, and **Roll** tilts the picture sideways.

In the **Target** section, **Follow actor** picks the actor the camera circles around, and **Recenter** puts that actor back in the middle. The switch next to it locks the camera onto the actor.

In the inspector, **Offset** moves the camera away from the point it circles. **Pin position** keeps the camera at its current spot in the world, and while it's pinned you can type in the exact position.

Under **Limits**, **Collision** lets walls push the camera like they do in the normal game, and **Delimit** removes the game's limits on zoom and angle.

## Free cameras

A free camera flies while it's live and **Movement** is on. **Speed** sets how fast it flies, and the mouse wheel changes the speed while you fly. **Sensitivity** sets how far the view turns when you drag with the right mouse button. **Lateral** keeps it flying level, and **Delimit angle** lets you look past straight up or down.

**Yaw** and **Pitch** set the direction the camera faces, and **Position** in the inspector sets exactly where it is.

## Both kinds

**Portrait** turns the picture a quarter turn for portrait shots. **Orthographic** turns off perspective, and **Ortho zoom** sets how much of the scene fits in the view.

**Tracking** in the inspector keeps chosen bones in view. Pick the bones with **Select bones**, or press the crosshair and click them in the game view. **Mode** sets whether the camera follows the bones, turns to face them, or both.

You can save a camera to a file or to the Library from the **File** section, and copy it with **Clone**. **Destroy** removes it. The main camera can't be removed.

## Other ways to move the camera

**Center camera on actor** in an actor's right-click menu points the live camera at that actor without changing the zoom or angle.

With **Update camera orbit with actor position** on in Settings › Camera, moving an actor also moves the point the camera circles around.

When you move the camera with the game's own controls, Poser adds a **Move camera** step to the undo history once the camera has been still for five seconds.

## Cameras in scenes

A saved scene brings its cameras back where they were. If the scene saved the main camera, it comes back as a new camera instead.
