# SpaceMouse for BrickLink Studio 2.0

A plugin that lets you control the camera of **BrickLink Studio 2.0** with a **3Dconnexion
SpaceMouse** (SpaceNavigator, SpaceMouse Compact, Wireless, Pro, Enterprise, ...).

- Rotate, tilt, pan and zoom **smoothly and at the same time**, as fluid as with the mouse.
- The model turns **in place around its center**, the way 3Dconnexion devices work in CAD programs.
- The mouse pointer stays free: navigate with one hand, place bricks with the other.
- **No separate program.** Studio loads the plugin itself on every start.
- Works alongside 3Dconnexion's driver (3DxWare).
- Configurable axes, speeds and buttons. Changes apply instantly, without restarting Studio.

## Requirements

- Windows 10 or 11
- BrickLink Studio 2.0 (tested with version 2.26.10)
- A 3Dconnexion SpaceMouse (tested with a SpaceNavigator). 3DxWare may be installed, but is not required.

## Installation

1. Close Studio (or restart it after the installation).
2. Double-click **`Install.bat`** and confirm the Windows prompt (administrator rights).
3. Start Studio.

To remove the plugin, run **`Uninstall.bat`** (close Studio first).

### What the installer changes

| Location | Change |
|---|---|
| `%LOCALAPPDATA%\SpaceMouseStudio\` | the plugin `SpaceMouseStudio.dll`, settings `config.ini`, log `log.txt` |
| `C:\Program Files\Studio 2.0\Studio_Data\Managed\` | a small loader, `SpaceMouseStudio.Loader.dll` |
| `...\Studio_Data\ScriptingAssemblies.json` | one entry for the loader (original backed up as `.orig`) |
| `...\Studio_Data\RuntimeInitializeOnLoads.json` | one entry for the loader (original backed up as `.orig`) |

Studio's own program files are not modified. The two JSON lists tell Unity (the engine Studio is
built on) which additional assemblies to load at startup. The loader then loads the plugin from
`%LOCALAPPDATA%`.

**After a Studio update** the two lists are usually replaced. Simply run `Install.bat` again.

## Usage

Following the 3Dconnexion convention, **the cap moves the model**; the camera moves the opposite
way. Rotations turn the model around **its center**, in place, even when it is not in the middle
of the view. When you are zoomed in so far that the center is out of view, the model turns around
the point in the middle of the view instead.

| SpaceMouse | Model |
|---|---|
| Twist the cap | turns around the vertical axis |
| Tilt the cap forward/back | tilts forward/back |
| Push the cap left/right | moves left/right |
| Pull the cap up / push it down | moves up/down |
| Push the cap forward / pull it back | moves away / comes closer |
| Button 1 (left) | fit the whole model into the view (like Ctrl+0) |
| Button 2 (right) | mode: all -> rotate only -> pan + zoom only |

The camera only moves while Studio is the active window. Short messages (e.g. when switching the
mode) appear at the mouse pointer.

### Smooth motion

Studio normally renders at no more than 30 frames per second (setting under *Edit > Preferences >
Appearance*). That makes even rotations look jerky. While you move the SpaceMouse, the plugin
therefore turns on VSync: Studio then renders in sync with the monitor (e.g. 165 Hz). About one
second after you let go, Studio's own setting applies again, so the graphics card can rest while
you are not navigating. See `motion_frame_rate`.

The plugin also smooths the SpaceMouse values slightly (`smoothing`, in seconds).

3DxWare does not know Studio and sends mouse wheel steps when the cap is tilted. Studio would zoom
in 10 % jumps on top of the plugin's motion. The plugin therefore ignores the mouse wheel in Studio
while the cap is deflected and for 0.3 seconds after. See `block_mouse_wheel`.

## Settings

The settings live in `%LOCALAPPDATA%\SpaceMouseStudio\config.ini` (type the path into the Explorer
address bar, or put the action `settings` on a SpaceMouse button). The file is commented.
**Changes apply as soon as the file is saved**, without restarting Studio. Delete the file to
restore the defaults.

### [General]

| Key | Default | Meaning |
|---|---|---|
| `enabled` | `yes` | Turn SpaceMouse control on or off. |
| `sensitivity` | `1.0` | Overall speed multiplier. |
| `deadzone` | `0.08` | Range around the rest position that is ignored, as a fraction of full deflection. Increase it if the camera drifts. |
| `response_curve` | `1.8` | `1` = linear; higher values give finer control for small movements. |
| `dominant_axis` | `no` | `yes` uses only the strongest movement at any time. By default all axes work together. |
| `smoothing` | `0.05` | Smoothing time in seconds. Higher is softer but less direct, `0` turns it off. |
| `pivot` | `model` | `model`: the model turns in place around its center. `target`: like the mouse, around Studio's camera target. |
| `block_mouse_wheel` | `yes` | Ignore 3DxWare's mouse wheel steps while the cap is deflected. The real mouse wheel works normally once the cap is released. |
| `full_scale` | `350` | Raw device value at full deflection. Only needed for devices with a different range. |

### [Speed]

All speeds apply at full deflection and sensitivity 1.

| Key | Default | Meaning |
|---|---|---|
| `rotate` | `150` | Degrees per second. |
| `pan` | `700` | Screen pixels per second, like dragging with the mouse. |
| `zoom` | `1.2` | `1.0` means a factor of about 2.7 per second. |

### [Display]

| Key | Default | Meaning |
|---|---|---|
| `motion_frame_rate` | `monitor` | Frame rate while moving: `monitor` (VSync, smoothest), a number such as `60` or `120`, or `off` to leave Studio's setting unchanged. Try `60` on a slow graphics card. |

### [Mapping]

Which SpaceMouse axis drives which camera function:

```
tx = push cap left/right           rx = tilt cap forward/back
ty = push cap forward/back         ry = tilt cap left/right
tz = pull cap up/push down         rz = twist cap
```

| Key | Default |
|---|---|
| `rotate_horizontal` | `-rz` |
| `rotate_vertical` | `rx` |
| `pan_horizontal` | `tx` |
| `pan_vertical` | `tz` |
| `zoom` | `ty` |

A leading `-` reverses the direction (**direction wrong?** add or remove it), `off` disables the
function, and an optional factor scales it, e.g. `rotate_vertical = rx * 0.5`.

### [Buttons]

`button1`, `button2`, ... (more for devices with more buttons, e.g. the SpaceMouse Pro), each set
to one of these actions:

| Action | Effect |
|---|---|
| `fit_all` | fit the whole model into the view (like Ctrl+0) |
| `mode` | cycle: all -> rotate only -> pan + zoom only |
| `toggle` | turn SpaceMouse control on/off |
| `dominant` | strongest movement only, on/off |
| `faster` / `slower` | increase / decrease the sensitivity |
| `settings` | open config.ini |
| `none` | nothing |

Tip: 3DxWare also assigns the SpaceMouse buttons itself (for example, the left button opens the
3Dconnexion menu). Open the 3Dconnexion settings **while Studio is in the foreground**; 3DxWare then
creates a profile for Studio in which you can turn those button assignments off.

### [Diagnostics]

| Key | Default | Meaning |
|---|---|---|
| `record` | `no` | `yes` records 15 seconds from the next movement, see [Troubleshooting](#troubleshooting). |

## Troubleshooting

- Look into `%LOCALAPPDATA%\SpaceMouseStudio\log.txt` first. After Studio starts, it should contain
  `Control running` and `SpaceMouse found`, and while moving, `Motion: ... FPS` every 3 seconds.
- **No `log.txt` after Studio starts:** Studio did not load the loader, usually after a Studio
  update. Run `Install.bat` again.
- **Studio no longer starts:** run `Uninstall.bat`, or copy the two `*.orig` files in `Studio_Data`
  back over the originals.
- **Still not smooth:** check the frame rate in the log. On a slow graphics card try
  `motion_frame_rate = 60`; for a softer start try `smoothing = 0.1`.
- **Diagnostics:** with `[Diagnostics] record = yes`, the plugin records 15 seconds from the next
  movement: every raw device report (`diagnostics_device.csv`) and, per frame, the raw values, the
  computed channels and the resulting camera position (`diagnostics_frames.csv`). Comparing the
  camera with the channels shows whether something other than the plugin moves the camera.
- **Test mode:** while a file `simulation.txt` with six numbers (`tx ty tz rx ry rz`, full
  deflection about 350) exists in the plugin folder, it replaces the SpaceMouse.

## How it works

**Loading.** Studio is a Unity application. At startup, Unity reads
`Studio_Data\ScriptingAssemblies.json` (which assemblies to load) and
`RuntimeInitializeOnLoads.json` (which static methods to call). The installer adds the loader to
both. The loader reads `SpaceMouseStudio.dll` from `%LOCALAPPDATA%` as a byte array and starts it,
so the plugin can be updated without administrator rights.

**Input.** The plugin reads the SpaceMouse through the Windows Raw Input API on its own thread,
independently of 3DxWare. 3Dconnexion devices report translation, rotation and buttons as HID
reports about every 16 ms.

**Camera.** Studio's camera is controlled by its class `OrbitControl`, which stores the view as an
azimuth, an elevation and a distance around a target point. With the mouse, 1 pixel equals
1 degree and one wheel step equals 10 % zoom, which is why emulating the mouse from outside Studio
can only move in visible steps. Running inside Studio, the plugin calls the same functions as the
mouse every frame, just with fractions:

- **Rotate:** like dragging with the right mouse button, e.g. 0.37 "pixels" per frame
- **Pan:** `OrbitControl.pan()`, like the middle mouse button
- **Zoom:** `OrbitControl.ZoomDistance`, like Studio's own zoom, but continuous

**Pivot.** `OrbitControl` always orbits its target point, which is rarely the model center. After
each rotation step the plugin shifts that target so that the camera has effectively orbited the
model center instead, which keeps the model in place on screen.

**Frame rate and mouse wheel.** See [Smooth motion](#smooth-motion). The mouse wheel filter replaces
`UICamera.GetAxis`, the only place where Studio reads the mouse wheel.

## Building

With the [.NET SDK](https://dotnet.microsoft.com/download) installed, run `Build.bat`, then
`Install.bat`. The plugin becomes active the next time Studio starts. Administrator rights are only
needed when the loader has changed.

The build references Studio's own assemblies from its installation folder (they are not part of
this repository). If Studio is installed somewhere else:

```
dotnet build plugin\Plugin\SpaceMouseStudio.csproj -c Release -p:StudioDir="D:\Studio 2.0"
```

### Project layout

```
plugin/Loader/           SpaceMouseStudio.Loader.dll - called by Unity at startup, loads the plugin
plugin/Plugin/           SpaceMouseStudio.dll
  Plugin.cs              entry point, hooks into Unity's frame loop
  Controller.cs          camera movement, pivot, frame rate, mouse wheel filter, buttons, diagnostics
  SpaceMouseReader.cs    reads the SpaceMouse through Raw Input
  Settings.cs            reads config.ini (including the commented default file)
  Native.cs              Win32 declarations
  Log.cs                 log.txt
installer/install.ps1    installation / removal
bin/                     prebuilt DLLs used by Install.bat
```

## Disclaimer

This is an unofficial community project. It is not affiliated with, endorsed or supported by
BrickLink, the LEGO Group or 3Dconnexion. BrickLink Studio, SpaceMouse and 3Dconnexion are
trademarks of their respective owners. The installer adds entries to two configuration files of
your Studio installation (backups are kept as `*.orig`). Use at your own risk.
