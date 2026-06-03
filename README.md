# Telltale D3DMesh Editor

A tool to **view, edit, export, and reimport** 3D mesh files from **Telltale Games** titles.

It helps modders inspect models, meshes, textures, and skeletons, and modify the game's original assets. With it you can preview a model, export it to GLB/GLTF, edit it in your favorite 3D software, and put it back into the game.

## What you can do

- **View** game models, meshes, textures, and skeletons.
- **Export** assets to standard formats (GLB / GLTF + separate files) so you can open them anywhere.
- **Edit** any existing model — tweak it, replace it, or import a brand new model from the internet.
- **Reimport** your edited model back into the game.

In short: it's now possible to **edit any existing model, import models from the internet into the game, and modify the game's original models too.**

## Safe, isolated textures

The tool **does not replace any texture already used by the game.** Instead, it assigns the new textures to your model and makes the game accept them properly.

So if the original model uses 10 textures, the tool can still take a custom model with, say, 15 textures and put it into the game **without replacing the original ones.** This avoids issues like bugs, wrong colors, or texture changes leaking into other characters or objects. The tool creates **exclusive textures for that specific model**, keeping everything separated and safer.

## Supported games

| Platform | Game | Status |
|----------|------|--------|
| PC | The Wolf Among Us | ✅ Working |

## A note about skeletons and animations

There's one important thing to keep in mind. Animation issues mostly apply to models that use a **skeleton**, like character models.

Even though you can import models from the internet, the animations and movements may end up looking buggy or slightly off. For example, if a character is supposed to grab a bottle, their hand might not line up perfectly with it — it could be a little too far to the left, too low, or in a completely wrong position.

This happens because we're **not editing the character's original skeleton.** The model can work visually, but the animations may not always match perfectly.

For **props and other objects that don't rely on a skeleton**, things should work normally. Those can usually be imported or edited without causing this kind of animation problem.

The reason is that I still haven't been able to find a proper solution for converting the game's **SKL** files to GLB/GLTF, rigging a custom model properly, and making the animations work with it. I tried several approaches, but unfortunately couldn't get it working.

**If anyone has information about this** — especially for older Telltale games like The Wolf Among Us and others — or wants to help, feel free to reach out. Everything is uploaded here so people can take a look.

## Help with testing

I need your help with testing. If you find any kind of bug, please submit an issue so it can be fixed.

I've already done several tests, covering many different possible scenarios, but something may still have slipped through.

As we move forward, we'll keep adding improvements, fixing issues, and adding support for more games over time.

## Credits

Made by **Heitor Spectre**.

Special thanks to:
- [iMrShadow](https://github.com/iMrShadow)
- [Gamma_02](https://github.com/gamma-02)
- [David Matos](https://github.com/frostbone25)
- [RandomTBush](https://github.com/RandomTBush)

Without their analysis and the documentation available in their repositories, none of this would have happened. Their work made it possible to build an editor capable of both extracting assets from the game and reinserting them back into it.
