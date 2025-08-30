# Mugi (Mini Udon Game Interface)

> **Work in Progress** - This package is under active development for VRChat Game Jam 2025. APIs and structure may change.

Mugi is a framework for creating multiplayer minigames as prefabs in VRChat worlds. It provides lobby management, game lifecycle handling, scoring systems, and player tracking to simplify the development of 5-minute multiplayer VR experiences.

Originally developed for [VRChat Game Jam 2025](https://jam.vrg.party), Mugi is designed to be reusable for games beyond the jam.

See https://jam.vrg.party/guides/tutorial for a tutorial to this package.

### Why is the Csharp namespace `Hiinaspace.Mugi` instead of `Space.Hiina.Mugi`?

Csharp apparently resolves namespace names before any local names, including parent namespaces. So `Space.Hiina.Mugi` creates a namspace called just `Space` that shadows anything else called `Space`, namely Unity's [`Space` enum](https://docs.unity3d.com/ScriptReference/Space.html). So if you have a namespace called `Space.Hiina.Mugi`, code like `transform.rotate(Vector3.up, Space.World) will break with errors
about `World` not being found in the `Space` namespace.

`Hiinaspace` is unlikely to collide with anything, so I just use that instead.