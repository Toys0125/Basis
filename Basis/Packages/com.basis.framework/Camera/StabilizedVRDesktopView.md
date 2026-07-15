# Stabilized VR Desktop View

The stabilized spectator camera replaces Unity's normal XR eye mirror in the desktop game window while leaving headset rendering unchanged.

## Vendored URP patch

Unity's public SRP callbacks can draw over the XR mirror, but they cannot prevent `XRSystem.RenderMirrorView` from running first. Basis therefore carries a small patch in:

`Packages/com.unity.render-pipelines.universal/Runtime/UniversalRenderPipeline.cs`

The patch adds the following public registration methods:

- `UniversalRenderPipeline.RegisterXRMirrorViewRenderFilter`
- `UniversalRenderPipeline.UnregisterXRMirrorViewRenderFilter`

Every registered filter must return `true` for the native XR mirror to render. The stabilized desktop driver returns `false` while its presentation camera, or the handheld recording camera, owns display zero.

When updating the embedded URP package, retain the block marked `BASIS PATCH` and the `ShouldRenderXRMirrorView(baseCamera)` guard around `XRSystem.RenderMirrorView`. The framework directly references the registration methods, so losing the patch causes compilation to fail instead of silently restoring the stock mirror path.

## Renderer contract

`Modified - Desktop.asset` reserves renderer index `2` for `DesktopRendererStabilizedView`. That renderer contains only `BasisVRDesktopViewBlitFeature`; it must not contain world-rendering features. The runtime driver validates that index before taking ownership of the desktop output.

## Output ordering

The generated cameras intentionally render in this order:

1. Stabilized capture camera
2. Main XR camera
3. Stabilized presentation camera

The capture camera uses the normal first-person head crop. The presentation camera renders no geometry and only blits the cached capture texture. A handheld recording camera has higher output priority and suppresses both the stabilized presenter and the native XR mirror.
