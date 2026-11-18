# Poser

Poser is a plugin for FINAL FANTASY XIV. It lets you pose characters and set up whole scenes in group pose, for screenshots.

It runs through [Dalamud](https://github.com/goatcorp/Dalamud), the plugin loader that comes with [XIVLauncher](https://goatcorp.github.io/).

Poser is in beta. Expect rough edges, and update when a new version comes out.

## What you can do

- **Pose characters.** Click a bone on the character, or on a diagram of the body, and move, turn or scale it. Pull a hand or a foot and the arm or leg follows. Mirror a pose, reset parts of it, and undo any step.
- **Play animations.** Play any emote or animation, loop it, slow it down, or drag through it frame by frame. Set a facial expression and where the eyes look.
- **Build a scene.** Add characters, props, lights and cameras. Move or hide furniture and other things that are already on the map. Change the time of day and the weather.
- **Save your work.** Save a pose, one character or the whole scene, and load it again later. Poser also saves the scene by itself while you work.
- **Keep a library.** Browse your saved poses and scenes with previews, tags and search.
- **Change how characters look.** Fade, tint or wet a character. With Penumbra, Glamourer and Customize+ installed you can also change gear, looks, mods and body shape, and load or save character files (MCDF).

## Installing

1. In game, type `/xlsettings` and open the **Experimental** tab.
2. Find **Custom Plugin Repositories**, paste this address into the empty box, press **+**, then press **Save and Close**:

   ```
   https://raw.githubusercontent.com/midona-rhel/Poser/main/repo.json
   ```

3. Type `/xlplugins`, search for **Poser** and press **Install**.

New versions show up in the same plugin list.

Penumbra, Glamourer and Customize+ are optional. Poser works without them. The features that need one of them tell you so.

## Using it

1. Enter group pose with `/gpose`.
2. Type `/poser` to open Poser.
3. Pick a character in Poser's list, then click one of the dots on its body to move that bone.

Poser only does anything inside group pose.

## Help

- [The guide](docs/) explains each part of Poser.
- [The release notes](docs/release/CHANGELOG.md) list what changed in each version.
- Found a bug? In Poser, open the menu and choose **Report an issue**. Poser saves a file with the details on your computer and sends nothing anywhere. Attach that file to a [new issue](https://github.com/midona-rhel/Poser/issues/new).

## For developers

Poser is written in C# for .NET 10. To build it and run the tests:

```
dotnet build Poser.slnx -c Release
dotnet test Poser.slnx -c Release
```

[How Poser is built](docs/README.md) is described with the rest of the guide.

## Credits

Poser is based on Anamnesis, Ktisis, and Brio. The contributors behind these tools are the people that figured out 99% of the solutions in this repository.

| Project | Repository | People |
|---|---|---|
| Anamnesis | https://github.com/imchillin/Anamnesis | ergoxiv and chirpxiv, after Yuki, Luminiari, Peebs-miqo and AsgardXIV |
| Ktisis | https://github.com/ktisis-tools/Ktisis | Chirp, Cazzar, Bwuny and contributors |
| Brio | https://github.com/Etheirys/Brio | Minmoose, Asgard and contributors |

Poser was written with the help of AI.

## License

Poser is licensed under GPL-3.0-only. See [LICENSE](LICENSE), and [THIRD-PARTY-LICENSES.md](THIRD-PARTY-LICENSES.md) for the software it uses.
