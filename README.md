# CapsulePath

CapsulePath is a client-side BepInEx plugin for **Content Warning** that draws a route from your current player position to the capsule.

- Steam Workshop: https://steamcommunity.com/sharedfiles/filedetails/?id=3710143685
- Thunderstore: https://thunderstore.io/c/content-warning/p/Largo/CapsulePath/

## Features

- NavMesh route from your current avatar position to the recorded capsule anchor
- Animated chevron arrows along a smooth, surface-hugging spline (centripetal Catmull-Rom)
- Rebindable keys in Settings -> MODS (defaults: `K` recalculate, `O` toggle)
- Hidden from in-game camera footage and the camcorder viewfinder (toggleable in Settings -> MODS)
- Vanilla-compatible: does not affect random/public matchmaking
- Client-side only: no RPCs, no network traffic, nothing changes for other players

## Building

Requires the game's assemblies next to the project (the repo lives in
`<game>/CapsulePath`; references point at `../Content Warning_Data/Managed`
and `../BepInEx/core`):

```
dotnet build -c Release
```

The DLL is emitted straight into `../BepInEx/plugins/`.

## How it works

- The capsule anchor is latched once per scene: the first time monsters register
  in `BotHandler.bots`, the local camera position (players are still at the
  capsule then) is recorded.
- `NavMesh.CalculatePath` from the player to the anchor; funnel corners are
  densified every 0.75 m and re-projected onto the NavMesh so the line follows
  stairs and slopes instead of cutting through them, then smoothed with a
  centripetal Catmull-Rom spline (loop-free by construction).
- The path is hidden while any `VideoCamera` lens renders
  (`RenderPipelineManager.beginCameraRendering`), so it never leaks into
  recorded clips.
