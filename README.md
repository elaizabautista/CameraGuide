# Camera Guide

A small stand-alone Windows tool for setting up the camera. It has the same look as the PrecisionSkin scan screen and shows the live camera picture with the same **face oval**, so the camera **distance and height** can be checked (for example for the 3D model of the prototype).

It contains **no AI, no detectors, no database and no login**. It only shows the camera.

## What it does
- **Live Cam** starts the camera; the picture is shown as the centre square, exactly like the scan screen (a checkbox switches that off to see the whole frame).
- The face oval in the same position and proportions as the scan screen, with optional centre lines (horizontal = eye height, vertical = left / right).
- The oval is fixed at 105%.
- **Snap Now** saves the picture that is on the screen to `Pictures\CameraGuide`, so a set-up can be kept for reference.
- **File Upload** shows any picture inside the same frame and oval.
- **Front / Left / Right** steps, the same as the scan screen. Touch a step to see its instruction.
- The **sharpest picture** the camera can give is used automatically (for example 1920 × 1080), or choose another size. Choose the camera in the same box; the one that worked last time is remembered, and a camera that sends no picture is skipped.
- Zoom: pinch with two fingers, use the mouse wheel, or the + / − / reset buttons. Drag to move a zoomed picture. Zoom only changes what is shown.
- Mirror the picture if that is easier to line up.
- Works with touch and with mouse, in landscape and portrait.

## Run it (no install)
Download `CameraGuide.zip` from the Releases page (or build it, below), unzip it, double-click `CameraGuide.exe`, and press **Live Cam**.

Needs Windows 10 or 11 (the .NET Framework 4.7.2 that it uses is already part of Windows). If the picture does not appear, close any other program that is using the camera (Teams, Zoom, the Camera app).

## Build it
Open `CameraGuide.csproj` in Visual Studio and press Run, or run:

```
msbuild CameraGuide.csproj -restore -p:Configuration=Release
```

The camera library (AForge.NET, LGPL) is included in the `lib` folder, so nothing needs to be downloaded.

## Project layout
| File | Purpose |
|---|---|
| `MainWindow.xaml` / `.cs` | the window: camera, oval, buttons, stepper |
| `TouchZoom.cs` | pinch / drag / wheel zoom of the picture (display only) |
| `App.xaml` | the LUMYVUE look (colours, buttons, fonts), copied from PrecisionSkin |
| `lib/` | AForge camera libraries |
