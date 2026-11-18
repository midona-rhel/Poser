# Lights

Poser can add lights of its own to the scene, and take over lights that are already in the game world.

## Adding a light

Press **+** in the sidebar and open the **Lights** tab of the spawn menu. There are four kinds of light:

- **Spot light** shines a cone in one direction, like a stage light.
- **Point light** shines in every direction from one spot, like a bulb.
- **Area light** shines from a flat panel, like a softbox.
- **Directional light** shines from far away in one direction, like the sun.

**Light from library** and **Light from file** add a light you saved before. New lights appear in the sidebar, and you place and aim them with the gizmo like anything else.

## Taking over a light from the world

The second of the four small buttons at the bottom of the sidebar, the light bulb, marks the lights around you in the game view, such as lamps and torches. Click a marker to add that light to your scene. Poser hides the original and puts an editable copy in its place, so the game can't change your settings on it. **Release**, in the copy's **Actions** section, removes the copy and shows the original light again, and leaving GPose does the same.

## The Light tab

Select a light in the sidebar and the middle of the main window shows its **Light** tab.

**General** has **Enabled**, which switches the light off without losing its settings, and **Reflections**, which lets it show up in reflective surfaces. **Name** is the name the light has in the sidebar, and **Type** switches between the four kinds of light.

**Light** has the light itself. **Color** can go brighter than white for a stronger light. **Intensity** is how much light it gives off, and **Range** how far it reaches. **Falloff type** and **Falloff** decide how the light fades towards the edge of its reach. A spot light also has **Cone angle**, how wide the cone opens, and **Falloff angle**, how soft its edge is. An area light has **Skew X** and **Skew Y**, which tilt where the light falls without turning the panel itself.

**Gobo**, in the same section, projects a pattern through the light, such as leaves or a window frame. **Clear** removes it again.

**Shadows** decides what the light casts shadows from. **Dynamic** makes the shadows follow things as they move. **Characters** and **Objects** switch shadows from characters and from scenery on or off. **Character range** is how far away character shadows are still drawn, and **Shadow near** and **Shadow far** are the closest and furthest distances at which shadows appear.

**Attach** makes the light follow something else in the scene, or a bone of an actor, so a light held in a hand moves with the hand.

**File** saves the light to a file or to the Library. **Actions** has **Move to camera**, which puts the light 1 yalm in front of the camera, pointing where the camera looks, as well as **Clone** and **Destroy**. **Destroy all lights…** removes every light Poser added.

## Lights in furniture

Some furniture has lights built in, such as lamps and candles. Each of those lights has its own switch, **Light 1**, **Light 2** and so on, on the furniture's tab and in its right-click menu. See [Objects](objects.md).
