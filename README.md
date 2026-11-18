# Poser

Poser is a GPose plugin for FFXIV, like Ktisis and Brio. It's still in beta, so expect bugs. If you hit one, please report it.

## Features

- **Posing**
  - Gizmo, bone overlay, and body and face maps
  - IK and mirroring
  - Import and export `.pose` files (same format as Brio)
- **Animation**
  - Play any emote or animation, up to five layers at once
  - Speed, looping, and scrubbing frame by frame
  - Expressions and gaze
- **Scenes**
  - Spawn actors, props, furniture, lights, cameras and effects
  - Borrow objects and lights from the map
  - Weather and time of day
  - Save and load the whole scene
- **Appearance**
  - Gear, dyes, body and face, plus Penumbra collections, Glamourer designs and Customize+ profiles (needs those plugins)
  - Load and save MCDFs
  - Tint, fade and wetness, which work without any other plugin
- **Library** for your poses, scenes, MCDFs and objects

## Installing

1. Type `/xlsettings` in chat and go to the **Experimental** tab.
2. Under **Custom Plugin Repositories**, add this and click **Save and Close**:

   ```
   https://raw.githubusercontent.com/midona-rhel/Poser/main/repo.json
   ```

3. Type `/xlplugins`, search for **Poser** and install it.

Poser opens when you enter GPose. You can also type `/poser`.

## Found a bug?

Open Poser's menu and pick **Report an issue**. It saves a zip with your logs and settings. Attach it to a [new issue](https://github.com/midona-rhel/Poser/issues/new).

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
