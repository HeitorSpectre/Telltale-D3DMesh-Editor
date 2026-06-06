# Telltale D3DMesh Editor

A tool to **view, edit, export, and reimport** 3D mesh files from **Telltale Games** titles.

It helps modders inspect models, meshes, textures, and skeletons, and modify the game's original assets. With it you can preview a model, export it to GLB/GLTF, edit it in your favorite 3D software, and put it back into the game.

## What you can do

- **View** game models, meshes, textures, and skeletons.
- **Export** assets to standard formats (GLB / GLTF + separate files) so you can open them anywhere.
- **Edit** any existing model — tweak it, replace it, or import a brand new model from the internet.
- **Reimport** your edited model back into the game.

In short: it's now possible to **edit any existing model, import models from the internet into the game, and modify the game's original models too.**

## How to use

There are two ways to load assets.

### Option A — Open an archive (recommended)

You can open the game's container files directly, without unpacking them with an external tool first.

1. Click **Open Archive…** in the toolbar.
2. Select one or more `.ttarch` / `.ttarch2` files (hold **Ctrl** to pick several at once).
3. The tool extracts only the relevant assets (`.d3dmesh`, `.d3dtx` and `.skl`) and loads them automatically.

Because games like *The Wolf Among Us* split models and their textures across separate archives (for example a `…_mesh.ttarch2` and a `…_tx.ttarch2`), you can select both at the same time so the models show up with their correct textures. Each archive is extracted into its own folder, while the viewer still shows everything together.

### Option B — Open a folder (manual)

If you already have the files extracted, or want to load custom assets:

1. Extract the **texture** (`.d3dtx`), the **model** (`.d3dmesh`) and, optionally, the **skeleton** (`.skl`) from the game.
2. Put them inside a **single folder**.
3. Click **Open Folder…** and select it — the tool will load and display everything for you.

## Safe, isolated textures

The tool **does not replace any texture already used by the game.** Instead, it assigns the new textures to your model and makes the game accept them properly.

So if the original model uses 10 textures, the tool can still take a custom model with, say, 15 textures and put it into the game **without replacing the original ones.** This avoids issues like bugs, wrong colors, or texture changes leaking into other characters or objects. The tool creates **exclusive textures for that specific model**, keeping everything separated and safer.

## Supported games

| Platform | Game | Status |
|----------|------|--------|
| PC | The Walking Dead: Season 2 | ✅ Working |
| PC | The Wolf Among Us | ✅ Working |

## Help with testing

I need your help with testing. If you find any kind of bug, please submit an [issue](https://github.com/HeitorSpectre/Telltale-D3DMesh-Editor/issues) so it can be fixed.

I've already done several tests, covering many different possible scenarios, but something may still have slipped through.

As we move forward, we'll keep adding improvements, fixing issues, and adding support for more games over time.

## Credits

Made by [Heitor Spectre](https://github.com/HeitorSpectre).

Special thanks to:
- Telltale ToolKit [iMrShadow](https://github.com/iMrShadow)
- D3DMeshUtilities [Gamma_02](https://github.com/gamma-02)
- D3DMESH-Converter [David Matos](https://github.com/frostbone25)
- TelltaleGames_D3DMesh [RandomTBush](https://github.com/RandomTBush)
- For testing [Aabii/Arizzble](https://github.com/Arizzble)
