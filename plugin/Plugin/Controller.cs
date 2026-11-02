using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using Script.UI_Script.Viewport;
using UnityEngine;

namespace SpaceMouseStudio
{
    /// <summary>
    /// Moves Studio's camera (<c>OrbitControl</c>) every frame according to the SpaceMouse.
    ///
    /// Pipeline per frame (<see cref="Step"/>):
    ///   1. read the six raw axes (tx ty tz rx ry rz) from <see cref="SpaceMouseReader"/>
    ///   2. shape them: dead zone, response curve, sensitivity       -> <see cref="ComputeChannels"/>
    ///   3. map them to five camera channels: rotate h/v, pan h/v, zoom
    ///   4. low-pass filter the channels                              -> <see cref="Smooth"/>
    ///   5. apply them to the camera, scaled by the frame time        -> <see cref="Move"/>
    ///
    /// The camera is moved through the same code paths the mouse uses, just with fractions of a pixel:
    ///   rotate -> theta/fai + setTransform(), like OrbitControl.OnMouseMove (1 mouse pixel = 1 degree)
    ///   pan    -> OrbitControl.pan(), like dragging with the middle mouse button
    ///   zoom   -> OrbitControl.ZoomDistance, like Studio's own zoom in/out, but continuous
    ///
    /// Three Studio-specific problems are handled here as well:
    ///   - Studio renders at 30 FPS without VSync by default, so even motion looks jerky.
    ///     While the SpaceMouse moves, VSync is switched on (<see cref="UpdateFrameRate"/>).
    ///   - Studio orbits around its camera target, which is usually not the model. The camera
    ///     target is shifted along so that the model turns in place (<see cref="Rotate"/>).
    ///   - 3DxWare sends mouse wheel steps when the cap is tilted, which zoom in 10 % jumps.
    ///     Those are filtered out (<see cref="FilteredGetAxis"/>).
    ///
    /// Everything runs on Unity's main thread, called from <see cref="Driver"/>.Update.
    /// </summary>
    internal sealed class Controller
    {
        private static readonly string[] ModeNames = { "all", "rotate only", "pan + zoom only" };

        private readonly string _configPath;
        private readonly string _simulationPath;
        private readonly SpaceMouseReader _reader = new SpaceMouseReader();
        private readonly short[] _raw = new short[6];        // raw device values this frame
        private readonly float[] _shaped = new float[6];     // after dead zone, curve and sensitivity, about -1..1
        private readonly short[] _simulation = new short[6]; // values from simulation.txt (test mode)
        private readonly float[] _target = new float[5];     // camera channels: rotate h/v, pan h/v, zoom
        private readonly float[] _smoothed = new float[5];   // the same after the low-pass filter
        private readonly Action<OrbitControl> _setTransform; // OrbitControl.setTransform() is private

        private Settings _cfg = new Settings();
        private DateTime _configStamp;
        private float _nextConfigCheck;
        private float _nextSimulationCheck;
        private bool _simulationActive;

        // State changed by SpaceMouse buttons (applies on top of config.ini, not saved)
        private int _mode;                  // index into ModeNames
        private bool _paused;
        private bool _dominantToggled;      // inverts the dominant_axis setting
        private float _sensitivityScale = 1f;

        private uint _prevButtons;
        private int _errorsInRow;
        private bool _lockHintShown;
        private int _lastDeviceCount = -1;

        // Frame rate boost while moving
        private bool _boosted;
        private int _savedVSync;
        private int _savedTargetFps;
        private int _appliedVSync;
        private int _appliedTargetFps;
        private float _lastMotionTime;

        // Pivot, determined once at the start of each movement
        private readonly FieldInfo _loadedFileServiceField;
        private bool _pivotResolved;
        private bool _hasPivot;
        private Vector3 _pivot;
        private bool _pivotCostLogged;

        // Mouse wheel steps that 3DxWare sends to Studio when the cap is tilted (each step = 10 % zoom)
        private const string ScrollAxisName = "Mouse ScrollWheel"; // UICamera.scrollAxisName in Studio
        private const int WheelBlockThreshold = 3;    // raw value above which the cap counts as deflected
        private const float WheelBlockHold = 0.3f;    // seconds after release; late wheel messages may still arrive
        private readonly short[] _wheelCheck = new short[6];
        private readonly UICamera.GetAxisFunc _filteredGetAxis;
        private UICamera.GetAxisFunc _studioGetAxis;
        private float _wheelBlockUntil;
        private int _blockedWheelSteps;
        private bool _wheelErrorLogged;

        // Diagnostics recording
        private const int DiagFrames = 4000;
        private const int DiagColumns = 23; // dt, 6 raw values, 5 target values, 5 smoothed values, 6 camera values
        private bool _diagRecording;
        private bool _diagDone;
        private long _diagStart;
        private int _diagCount;
        private long[] _diagTime;
        private float[] _diagData;

        // Frame rate statistics for the log
        private int _statFrames;
        private float _statStart;
        private float _statMin;
        private float _statMax;

        /// <param name="dir">Plugin folder in %LOCALAPPDATA% with config.ini, log.txt etc.</param>
        public Controller(string dir)
        {
            _configPath = Path.Combine(dir, "config.ini");
            _simulationPath = Path.Combine(dir, "simulation.txt");
            LoadConfig(true);

            // Studio internals that are not public. If a Studio update renames them, the plugin
            // falls back to a simpler behavior instead of failing.
            MethodInfo setTransform = typeof(OrbitControl).GetMethod("setTransform",
                BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (setTransform != null)
                _setTransform = (Action<OrbitControl>)Delegate.CreateDelegate(typeof(Action<OrbitControl>), setTransform, false);
            if (_setTransform == null)
                Log.Info("Note: OrbitControl.setTransform not found, using fallback");
            _loadedFileServiceField = typeof(Viewport).GetField("_loadedFileService", BindingFlags.Instance | BindingFlags.NonPublic);
            if (_loadedFileServiceField == null)
                Log.Info("Note: model not accessible, pivot is the point in the middle of the view");

            // keep one delegate instance so it can be recognized in UICamera.GetAxis later
            _filteredGetAxis = FilteredGetAxis;
            _reader.Start();
        }

        /// <summary>Called when Studio quits. Gives the mouse wheel back to Studio and stops the reader thread.</summary>
        public void Shutdown()
        {
            RemoveWheelFilter();
            _reader.Stop();
        }

        /// <summary>
        /// Runs one frame. Exceptions are caught so that a problem in the plugin never breaks Studio.
        /// Returns false after 100 failing frames in a row; the caller then stops calling.
        /// </summary>
        public bool Tick()
        {
            try
            {
                Step();
                _errorsInRow = 0;
                return true;
            }
            catch (Exception e)
            {
                _errorsInRow++;
                if (_errorsInRow <= 3)
                    Log.Info("Error: " + e);
                if (_errorsInRow < 100)
                    return true;
                RemoveWheelFilter();
                return false;
            }
        }

        private void Step()
        {
            float now = Time.unscaledTime;

            // Once per second: reload config.ini if it changed, report device changes, and
            // (re)install the mouse wheel filter in case it is not in place yet.
            if (now >= _nextConfigCheck)
            {
                _nextConfigCheck = now + 1f;
                LoadConfig(false);
                int devices = _reader.DeviceCount;
                if (devices != _lastDeviceCount)
                {
                    _lastDeviceCount = devices;
                    Log.Info("Connected SpaceMouse devices: " + devices);
                }
                InstallWheelFilter();
            }
            HandleButtons();

            // Only move the camera while Studio is the active window. The reader receives input
            // in the background as well (RIDEV_INPUTSINK).
            if (!_cfg.Enabled || _paused || !Application.isFocused)
            {
                Array.Clear(_smoothed, 0, _smoothed.Length);
                EndBoost();
                return;
            }

            _reader.ReadAxes(_raw);
            if (IsDeflected(_raw))
                _wheelBlockUntil = now + WheelBlockHold;
            ApplySimulation(now);

            // A long frame (e.g. while Studio loads something) must not cause a big jump.
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            ComputeChannels();
            bool moving = Smooth(dt);
            UpdateFrameRate(moving, now);

            OrbitControl oc = null;
            if (moving)
            {
                oc = Move(now, dt);
            }
            else
            {
                // movement finished: the next one determines its pivot anew
                _lockHintShown = false;
                _pivotResolved = false;
                _statFrames = 0;
                if (_blockedWheelSteps > 0 && now > _wheelBlockUntil)
                {
                    Log.Info("Mouse wheel steps from 3DxWare suppressed: " + _blockedWheelSteps);
                    _blockedWheelSteps = 0;
                }
            }
            if (_cfg.Diagnose && !_diagDone)
                RecordDiagnose(moving, dt, oc);
        }

        /// <summary>Moves the camera by this frame's smoothed values. Returns the camera, or null if no viewport is active.</summary>
        private OrbitControl Move(float now, float dt)
        {
            Viewport viewport;
            ViewportCameraRig rig;
            OrbitControl oc = FindOrbitControl(out viewport, out rig);
            if (oc == null)
                return null;
            RecordFrame(now, dt);

            // channel values are speeds at full deflection; multiplying by dt makes the motion
            // independent of the frame rate
            float rotH = _smoothed[0], rotV = _smoothed[1], panH = _smoothed[2], panV = _smoothed[3], zoom = _smoothed[4];
            if (rotH != 0f || rotV != 0f)
            {
                if (!_pivotResolved)
                {
                    _pivotResolved = true;
                    _pivotKind = "Studio's camera target";
                    _hasPivot = _cfg.PivotOnModel && TryFindPivot(rig, viewport, out _pivot);
                    if (_pivotLogs < 5)
                    {
                        _pivotLogs++;
                        Log.Info("Pivot: " + _pivotKind + (_hasPivot ? " " + _pivot.ToString("F1") : ""));
                    }
                }
                Rotate(oc, rotH * _cfg.RotateSpeed * dt, rotV * _cfg.RotateSpeed * dt);
            }
            if ((panH != 0f || panV != 0f) && oc.isAllowPanning)
                Pan(oc, panH * _cfg.PanSpeed * dt, panV * _cfg.PanSpeed * dt);
            if (zoom != 0f)
            {
                // exponential, so the zoom feels equally fast close to and far from the model
                oc.ZoomDistance = oc.ZoomDistance * Mathf.Exp(-zoom * _cfg.ZoomSpeed * dt);
            }
            return oc;
        }

        // --- Mouse wheel ---

        /// <summary>
        /// 3DxWare treats Studio as an application without SpaceMouse support and sends mouse wheel
        /// steps when the cap is tilted forward/back. Studio zooms by 10 % per step, which shows up as
        /// jumps on top of the plugin's smooth motion.
        ///
        /// Studio reads the mouse wheel only through NGUI's <c>UICamera.GetAxis</c>, a public static
        /// delegate. It is replaced by <see cref="FilteredGetAxis"/>, which reports no wheel movement
        /// while the cap is deflected. This works no matter how 3DxWare injects the wheel messages.
        /// </summary>
        private void InstallWheelFilter()
        {
            if (UICamera.GetAxis == null || UICamera.GetAxis == _filteredGetAxis)
                return;
            _studioGetAxis = UICamera.GetAxis;
            UICamera.GetAxis = _filteredGetAxis;
            Log.Info("Mouse wheel filter active");
        }

        /// <summary>Restores Studio's original axis function.</summary>
        private void RemoveWheelFilter()
        {
            if (_studioGetAxis != null && UICamera.GetAxis == _filteredGetAxis)
                UICamera.GetAxis = _studioGetAxis;
        }

        /// <summary>
        /// Replacement for <c>UICamera.GetAxis</c>. Called by Studio's UI for all axes every frame,
        /// so it must be cheap and must never throw.
        /// </summary>
        private float FilteredGetAxis(string name)
        {
            float value = _studioGetAxis(name);
            if (value == 0f || name != ScrollAxisName || !_cfg.BlockWheel || !_cfg.Enabled || _paused)
                return value;
            try
            {
                // Check the device directly instead of relying on Step(): UICamera may run before
                // this plugin in the same frame.
                _reader.ReadAxes(_wheelCheck);
                if (Time.unscaledTime < _wheelBlockUntil || IsDeflected(_wheelCheck))
                {
                    _blockedWheelSteps++;
                    return 0f;
                }
            }
            catch (Exception e)
            {
                LogOnce(ref _wheelErrorLogged, "Mouse wheel filter: " + e.Message);
            }
            return value;
        }

        /// <summary>True if any axis is clearly away from the rest position.</summary>
        private static bool IsDeflected(short[] raw)
        {
            for (int i = 0; i < 6; i++)
                if (raw[i] > WheelBlockThreshold || raw[i] < -WheelBlockThreshold)
                    return true;
            return false;
        }

        // --- Signal processing ---

        /// <summary>Turns the raw axes into the five camera channels in <see cref="_target"/>.</summary>
        private void ComputeChannels()
        {
            // Shape each axis: normalize to -1..1, cut the dead zone and rescale the rest to 0..1
            // (so there is no step at the edge of the dead zone), then apply the response curve.
            // A curve exponent > 1 gives fine control near the rest position and full speed at
            // full deflection.
            float dz = _cfg.Deadzone;
            float gain = _cfg.Sensitivity * _sensitivityScale;
            for (int i = 0; i < 6; i++)
            {
                float v = Mathf.Clamp(_raw[i] / _cfg.AxisMax, -1f, 1f);
                float a = Mathf.Abs(v);
                _shaped[i] = a <= dz ? 0f : Mathf.Sign(v) * Mathf.Pow((a - dz) / (1f - dz), _cfg.Curve) * gain;
            }

            _target[0] = _cfg.RotateH.Apply(_shaped);
            _target[1] = _cfg.RotateV.Apply(_shaped);
            _target[2] = _cfg.PanH.Apply(_shaped);
            _target[3] = _cfg.PanV.Apply(_shaped);
            _target[4] = _cfg.Zoom.Apply(_shaped);

            // button "mode": rotate only / pan + zoom only
            if (_mode == 1)
                _target[2] = _target[3] = _target[4] = 0f;
            else if (_mode == 2)
                _target[0] = _target[1] = 0f;

            // Optional: keep only the strongest channel. Off by default; all axes work together.
            if (_cfg.DominantAxis != _dominantToggled)
            {
                int strongest = 0;
                for (int i = 1; i < _target.Length; i++)
                    if (Mathf.Abs(_target[i]) > Mathf.Abs(_target[strongest]))
                        strongest = i;
                for (int i = 0; i < _target.Length; i++)
                    if (i != strongest)
                        _target[i] = 0f;
            }
        }

        /// <summary>
        /// First-order low-pass filter on the channels. The device sends new values only every 16 ms
        /// while Studio renders up to 165 FPS or more; without smoothing the speed would change in
        /// steps. Returns whether anything is still moving.
        /// </summary>
        private bool Smooth(float dt)
        {
            // frame-rate independent blend factor for the time constant _cfg.Smoothing
            float k = _cfg.Smoothing > 0f ? 1f - Mathf.Exp(-dt / _cfg.Smoothing) : 1f;
            bool moving = false;
            for (int i = 0; i < _target.Length; i++)
            {
                float v = _smoothed[i] + (_target[i] - _smoothed[i]) * k;
                // snap to zero at the end, otherwise the exponential tail would never reach zero
                if (_target[i] == 0f && Mathf.Abs(v) < 0.002f)
                    v = 0f;
                _smoothed[i] = v;
                moving |= v != 0f;
            }
            return moving;
        }

        // --- Frame rate ---

        /// <summary>
        /// Studio limits itself to 30 (or 60) FPS without VSync. While the SpaceMouse moves, VSync is
        /// switched on (or a fixed frame rate set) so the motion is as smooth as the monitor allows.
        /// One second after the movement ends, Studio's own setting is restored.
        /// </summary>
        private void UpdateFrameRate(bool moving, float now)
        {
            int mode = _cfg.MotionFrameRate;
            if (moving && mode != 0)
            {
                _lastMotionTime = now;
                if (!_boosted)
                {
                    _boosted = true;
                    _savedVSync = QualitySettings.vSyncCount;
                    _savedTargetFps = Application.targetFrameRate;
                }
                // Set every frame: Studio resets these values itself, e.g. on focus changes.
                _appliedVSync = mode < 0 ? 1 : 0;
                _appliedTargetFps = mode < 0 ? _savedTargetFps : mode;
                if (QualitySettings.vSyncCount != _appliedVSync)
                    QualitySettings.vSyncCount = _appliedVSync;
                if (Application.targetFrameRate != _appliedTargetFps)
                    Application.targetFrameRate = _appliedTargetFps;
            }
            else if (_boosted && (mode == 0 || now - _lastMotionTime > 1f))
            {
                EndBoost();
            }
        }

        /// <summary>Hands the frame rate back to Studio.</summary>
        private void EndBoost()
        {
            if (!_boosted)
                return;
            _boosted = false;
            // Studio applies its limit (30/60 FPS) only on its first focus change and runs uncapped
            // before that. Restoring the saved values could therefore leave it uncapped at several
            // hundred FPS. Ask Studio to apply its own setting instead.
            if (Application.isFocused && Preferences.Instance != null)
            {
                Preferences.Instance.ConfigureTargetFrameRate(true);
                return;
            }
            // Not focused: Studio has already set its background frame rate. Restore only values
            // that are still ours.
            if (QualitySettings.vSyncCount == _appliedVSync)
                QualitySettings.vSyncCount = _savedVSync;
            if (Application.targetFrameRate == _appliedTargetFps)
                Application.targetFrameRate = _savedTargetFps;
        }

        // --- Diagnostics ---

        /// <summary>
        /// With <c>[Diagnostics] record = yes</c>: records up to 15 seconds from the next movement.
        /// Per frame: frame time, raw values, target and smoothed channels, and the resulting camera
        /// state. The reader records every device report with a timestamp in parallel. Comparing the
        /// camera state with the channels shows whether anything other than the plugin moves the
        /// camera (this is how the 3DxWare mouse wheel steps were found).
        /// </summary>
        private void RecordDiagnose(bool moving, float dt, OrbitControl oc)
        {
            if (!_diagRecording)
            {
                if (!moving)
                    return;
                _diagTime = _diagTime ?? new long[DiagFrames];
                _diagData = _diagData ?? new float[DiagFrames * DiagColumns];
                _diagCount = 0;
                _diagStart = Stopwatch.GetTimestamp();
                _diagRecording = true;
                _reader.StartRecording();
                Show("SpaceMouse: diagnostics running (15 s)");
            }
            if (_diagCount < DiagFrames)
            {
                _diagTime[_diagCount] = Stopwatch.GetTimestamp();
                int o = _diagCount * DiagColumns;
                _diagData[o] = dt;
                for (int i = 0; i < 6; i++)
                    _diagData[o + 1 + i] = _raw[i];
                for (int i = 0; i < 5; i++)
                {
                    _diagData[o + 7 + i] = _target[i];
                    _diagData[o + 12 + i] = _smoothed[i];
                }
                // camera after this frame's movement (angles in degrees; all 0 without an active camera)
                if (oc != null)
                {
                    Vector3 p = oc.transform.position;
                    _diagData[o + 17] = oc.theta * Mathf.Rad2Deg;
                    _diagData[o + 18] = oc.fai * Mathf.Rad2Deg;
                    _diagData[o + 19] = oc.ZoomDistance;
                    _diagData[o + 20] = p.x;
                    _diagData[o + 21] = p.y;
                    _diagData[o + 22] = p.z;
                }
                else
                {
                    Array.Clear(_diagData, o + 17, 6);
                }
                _diagCount++;
            }
            if (_diagCount >= DiagFrames || (Stopwatch.GetTimestamp() - _diagStart) > 15 * Stopwatch.Frequency)
                WriteDiagnose();
        }

        /// <summary>Writes diagnostics_device.csv (one row per device report) and diagnostics_frames.csv (one row per frame).</summary>
        private void WriteDiagnose()
        {
            _diagRecording = false;
            _diagDone = true;
            long[] repTime;
            byte[] repId;
            short[] repValues;
            int reports = _reader.StopRecording(out repTime, out repId, out repValues);
            string dir = Path.GetDirectoryName(_configPath);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            double toMs = 1000.0 / Stopwatch.Frequency;
            try
            {
                // report 1 = translation (v1..v3), report 2 = rotation (v1..v3), report 3 = buttons;
                // newer devices send all six axes in report 1
                var sb = new StringBuilder("time_ms;report;v1;v2;v3;v4;v5;v6\n");
                for (int r = 0; r < reports; r++)
                {
                    sb.Append(((repTime[r] - _diagStart) * toMs).ToString("0.000", inv)).Append(';').Append(repId[r]);
                    for (int i = 0; i < 6; i++)
                        sb.Append(';').Append(repValues[r * 6 + i]);
                    sb.Append('\n');
                }
                File.WriteAllText(Path.Combine(dir, "diagnostics_device.csv"), sb.ToString());

                sb = new StringBuilder("time_ms;dt_ms;tx;ty;tz;rx;ry;rz;" +
                    "target_rot_h;target_rot_v;target_pan_h;target_pan_v;target_zoom;" +
                    "smooth_rot_h;smooth_rot_v;smooth_pan_h;smooth_pan_v;smooth_zoom;" +
                    "cam_theta;cam_fai;cam_distance;cam_x;cam_y;cam_z\n");
                for (int f = 0; f < _diagCount; f++)
                {
                    int o = f * DiagColumns;
                    sb.Append(((_diagTime[f] - _diagStart) * toMs).ToString("0.000", inv));
                    sb.Append(';').Append((_diagData[o] * 1000f).ToString("0.000", inv));
                    for (int i = 1; i < DiagColumns; i++)
                        sb.Append(';').Append(_diagData[o + i].ToString(i < 7 ? "0" : i < 17 ? "0.0000" : "0.000", inv));
                    sb.Append('\n');
                }
                File.WriteAllText(Path.Combine(dir, "diagnostics_frames.csv"), sb.ToString());
                Log.Info("Diagnostics saved: " + reports + " device reports, " + _diagCount + " frames");
                Show("SpaceMouse: diagnostics saved");
            }
            catch (Exception e)
            {
                Log.Info("Diagnostics could not be saved: " + e.Message);
            }
        }

        /// <summary>Logs the achieved frame rate every 3 seconds while moving.</summary>
        private void RecordFrame(float now, float dt)
        {
            if (_statFrames == 0)
            {
                _statStart = now;
                _statMin = float.MaxValue;
                _statMax = 0f;
            }
            _statFrames++;
            _statMin = Mathf.Min(_statMin, dt);
            _statMax = Mathf.Max(_statMax, dt);
            float span = now - _statStart;
            if (span >= 3f)
            {
                Log.Info(string.Format("Motion: {0:0} FPS, frames {1:0.0} to {2:0.0} ms, VSync {3}",
                    _statFrames / span, _statMin * 1000f, _statMax * 1000f, QualitySettings.vSyncCount));
                _statFrames = 0;
            }
        }

        // --- Camera ---

        /// <summary>
        /// Finds the camera to move: the instruction editor's viewport if it is open, otherwise the
        /// build viewport under the mouse, otherwise the current one (Studio can show up to four).
        /// </summary>
        private static OrbitControl FindOrbitControl(out Viewport viewport, out ViewportCameraRig rig)
        {
            viewport = null;
            rig = null;
            Ins_StepEditor_Viewport instructions = Ins_StepEditor_Viewport.Current;
            if (instructions != null && instructions.isActiveAndEnabled)
            {
                rig = instructions.ViewportCameraRig;
                OrbitControl oc = OrbitOf(rig);
                if (oc != null)
                    return oc;
            }
            ViewportsManager manager = ViewportsManager.Instance;
            if (manager == null)
                return null;
            viewport = manager.HoverViewport;
            if (viewport == null || !viewport.isActiveAndEnabled)
                viewport = manager.CurrentViewport;
            rig = viewport != null ? viewport.ViewportCameraRig : null;
            return OrbitOf(rig);
        }

        /// <summary>
        /// Pivot for "the model turns, not the camera": the model center (computed like Studio's
        /// "Zoom to fit") if it is in view, otherwise the point of the model in the middle of the
        /// view. Returns false if neither exists; the camera then orbits Studio's camera target.
        /// </summary>
        private bool TryFindPivot(ViewportCameraRig rig, Viewport viewport, out Vector3 pivot)
        {
            pivot = Vector3.zero;
            Camera cam = rig != null ? rig.RaycastCamera : null;
            if (cam == null)
            {
                LogOnce(ref _pivotNoCameraLogged, "Pivot: no camera found, rotating around Studio's camera target");
                return false;
            }

            // model center, if it is visible (with a small margin)
            Vector3 center;
            if (viewport != null && TryModelCenter(viewport, out center))
            {
                Vector3 vp = cam.WorldToViewportPoint(center);
                if (vp.z > 0f && vp.x > 0.02f && vp.x < 0.98f && vp.y > 0.02f && vp.y < 0.98f)
                {
                    pivot = center;
                    _pivotKind = "model center";
                    return true;
                }
            }

            // zoomed in on a detail: the point of the model in the middle of the view
            // (same layers that Studio's OrbitControl raycasts against)
            int mask = (1 << LayerMask.NameToLayer("GameWorld")) | (1 << LayerMask.NameToLayer("Default")) | (1 << LayerMask.NameToLayer("Ignore Raycast"));
            RaycastHit hit;
            if (Physics.Raycast(cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f)), out hit, float.PositiveInfinity, mask))
            {
                pivot = hit.point;
                _pivotKind = "model point in the middle of the view";
                return true;
            }
            return false;
        }

        private string _pivotKind;
        private int _pivotLogs;
        private bool _pivotNoCameraLogged;
        private bool _pivotNoModelLogged;

        private static void LogOnce(ref bool done, string message)
        {
            if (done)
                return;
            done = true;
            Log.Info(message);
        }

        /// <summary>Computes the center of the active model in world coordinates (takes about 1 ms).</summary>
        private bool TryModelCenter(Viewport viewport, out Vector3 center)
        {
            center = Vector3.zero;
            if (_loadedFileServiceField == null)
                return false;
            var service = _loadedFileServiceField.GetValue(viewport) as Script.Services.ILoadedFileService;
            LDrawFile file = service != null ? service.LoadedFile : null;
            LDrawModel model = file != null ? file.ActiveModel : null;
            if (model == null)
            {
                LogOnce(ref _pivotNoModelLogged, "Pivot: no active model found");
                return false;
            }
            Stopwatch watch = Stopwatch.StartNew();
            // same calculation as Viewport.zoomToFit_Co: wrap the model in a temporary part
            var part = new LDrawPart();
            part.SetModelWithoutNotification(model);
            if (part.AllSubBricks.Count == 0)
                return false;
            center = part.getCenter();
            if (!_pivotCostLogged)
            {
                _pivotCostLogged = true;
                Log.Info(string.Format("Model center computed in {0:0.0} ms", watch.Elapsed.TotalMilliseconds));
            }
            return true;
        }

        /// <summary>
        /// Rotation that takes the direction "target -> camera" from its initial state to the current
        /// one. Matches OrbitControl.setTransform: RotateAround(up, theta), then RotateAround(right, -fai).
        /// </summary>
        private static Quaternion OrbitRotation(OrbitControl oc)
        {
            return Quaternion.Euler(-oc.fai * Mathf.Rad2Deg, oc.theta * Mathf.Rad2Deg, 0f);
        }

        private static OrbitControl OrbitOf(ViewportCameraRig rig)
        {
            if (rig == null)
                return null;
            OrbitControl oc = rig.OrbitControl;
            return oc != null && oc.isActiveAndEnabled ? oc : null;
        }

        /// <summary>
        /// Rotates like dragging with the right mouse button. dx/dy are in mouse pixels (= degrees),
        /// right/down positive.
        ///
        /// OrbitControl stores the camera as spherical coordinates around its target point:
        /// <c>theta</c> (azimuth around the world up axis), <c>fai</c> (elevation) and
        /// <c>ZoomDistance</c> (radius). Changing theta/fai always orbits that target. To make the
        /// model turn in place instead, the target is shifted afterwards so the camera has effectively
        /// orbited the pivot.
        /// </summary>
        private void Rotate(OrbitControl oc, float dx, float dy)
        {
            if (!oc.isAllowRotation)
            {
                // Studio's rotation lock (viewport toolbar): show Studio's own message once per movement
                if (!_lockHintShown)
                {
                    _lockHintShown = true;
                    Show("The viewport rotation lock is on.");
                }
                return;
            }

            // same as OrbitControl.OnMouseMove (f0Dir = -delta) followed by setTransform()
            Quaternion before = OrbitRotation(oc);
            oc.theta -= dx * Mathf.Deg2Rad;
            oc.fai -= dy * Mathf.Deg2Rad;
            if (_setTransform != null)
            {
                _setTransform(oc);
                if (_hasPivot)
                {
                    // q is this frame's rotation of the camera around the target T. Orbiting the pivot P
                    // by the same rotation instead differs only by a translation:
                    //   camera_new = P + q (camera - P)  versus  T + q (camera - T)
                    // The difference is (P - T) - q (P - T). Shifting the target by that amount (and
                    // the camera with it, since the camera is defined relative to the target) keeps
                    // the pivot fixed on screen while the view turns.
                    Quaternion q = OrbitRotation(oc) * Quaternion.Inverse(before);
                    Vector3 offset = _pivot - oc.targetPos;
                    Vector3 shift = offset - q * offset;
                    if (shift.sqrMagnitude > 1e-12f)
                    {
                        oc.targetPos += shift;
                        _setTransform(oc);
                    }
                }
            }
            else
            {
                // fallback if setTransform could not be found: let OrbitControl.LateUpdate apply it
                oc.IsNeedToSetTransform = true;
            }

            // notify Studio like a mouse rotation does (view cube, camera orientation, overlays)
            if (oc.onChangeRotation != null)
                oc.onChangeRotation();
            if (oc.onChangeRotationByMouse != null)
                oc.onChangeRotationByMouse();
            if (oc.onChangePosition != null)
                oc.onChangePosition();
        }

        /// <summary>
        /// Pans like dragging with the middle mouse button. dx/dy are in mouse pixels, right/down
        /// positive. OrbitControl.pan() scales by the zoom distance and notifies Studio itself.
        /// </summary>
        private static void Pan(OrbitControl oc, float dx, float dy)
        {
            oc.pan(new Vector2(-dx, dy));
        }

        // --- Buttons ---

        /// <summary>Runs the configured action for every button that was pressed since the last frame.</summary>
        private void HandleButtons()
        {
            uint mask;
            while (_reader.TryGetButtonEvent(out mask))
            {
                uint pressed = mask & ~_prevButtons;
                _prevButtons = mask;
                if (!Application.isFocused)
                    continue;
                for (int bit = 0; bit < 32; bit++)
                {
                    string action;
                    if ((pressed >> bit & 1) != 0 && _cfg.Buttons.TryGetValue(bit + 1, out action))
                        RunAction(action);
                }
            }
        }

        private void RunAction(string action)
        {
            switch (action)
            {
                case "":
                case "none":
                    return;
                case "fit_all":
                    // same as View > Zoom to Fit (Ctrl+0)
                    if (MenuHandler.Current != null)
                        MenuHandler.Current.HandleZoomToFit(null, false);
                    return;
                case "mode":
                    _mode = (_mode + 1) % ModeNames.Length;
                    Show("SpaceMouse: " + ModeNames[_mode]);
                    return;
                case "toggle":
                    _paused = !_paused;
                    Show(_paused ? "SpaceMouse: off" : "SpaceMouse: on");
                    return;
                case "dominant":
                    _dominantToggled = !_dominantToggled;
                    Show("SpaceMouse: strongest movement only " + (_cfg.DominantAxis != _dominantToggled ? "on" : "off"));
                    return;
                case "faster":
                    _sensitivityScale = Mathf.Min(_sensitivityScale * 1.25f, 8f);
                    Show("SpaceMouse: sensitivity " + (_cfg.Sensitivity * _sensitivityScale).ToString("0.00"));
                    return;
                case "slower":
                    _sensitivityScale = Mathf.Max(_sensitivityScale / 1.25f, 0.05f);
                    Show("SpaceMouse: sensitivity " + (_cfg.Sensitivity * _sensitivityScale).ToString("0.00"));
                    return;
                case "settings":
                    Process.Start(_configPath);
                    return;
                default:
                    Show("SpaceMouse: unknown action '" + action + "'");
                    return;
            }
        }

        /// <summary>Shows a short message at the mouse cursor, using Studio's own toast.</summary>
        private static void Show(string text)
        {
            try
            {
                ProgressMsg.Show(text, 1.5f);
            }
            catch (Exception e)
            {
                Log.Info("Message could not be shown: " + e.Message);
            }
        }

        // --- Settings ---

        /// <summary>
        /// Loads config.ini. Creates it with the commented defaults if it does not exist. After the
        /// first call, reloads only when the file's modification time changed.
        /// </summary>
        private void LoadConfig(bool initial)
        {
            try
            {
                if (!File.Exists(_configPath))
                    File.WriteAllText(_configPath, Settings.DefaultText, new UTF8Encoding(true));
                DateTime stamp = File.GetLastWriteTimeUtc(_configPath);
                if (!initial && stamp == _configStamp)
                    return;
                var warnings = new List<string>();
                Settings cfg = Settings.Parse(File.ReadAllText(_configPath, Encoding.UTF8), warnings);
                _configStamp = stamp;
                // switching "record" from no to yes starts a new recording
                if (cfg.Diagnose && !_cfg.Diagnose)
                    _diagDone = false;
                _cfg = cfg;
                foreach (string w in warnings)
                    Log.Info("config.ini, " + w);
                Log.Info("Settings loaded" + (warnings.Count > 0 ? " (" + warnings.Count + " errors)" : ""));
                if (!initial)
                    Show(warnings.Count == 0 ? "SpaceMouse: settings applied" : "SpaceMouse: error in config.ini, see log.txt");
            }
            catch (IOException)
            {
                // the file is probably being saved right now: try again in a second
            }
            catch (Exception e)
            {
                Log.Info("config.ini could not be read: " + e.Message);
            }
        }

        // --- Test mode: a file simulation.txt with six raw values replaces the SpaceMouse ---

        /// <summary>
        /// Lets the plugin be tested without touching the device: while simulation.txt exists, its six
        /// numbers (tx ty tz rx ry rz, full scale about 350) are used as the raw values.
        /// </summary>
        private void ApplySimulation(float now)
        {
            if (now >= _nextSimulationCheck)
            {
                _nextSimulationCheck = now + 0.05f;
                _simulationActive = false;
                try
                {
                    if (File.Exists(_simulationPath))
                    {
                        string[] parts = File.ReadAllText(_simulationPath).Split(new[] { ' ', ',', ';', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 6)
                        {
                            for (int i = 0; i < 6; i++)
                                _simulation[i] = short.Parse(parts[i]);
                            _simulationActive = true;
                        }
                    }
                }
                catch (Exception)
                {
                    // half-written file: read it again next time
                }
            }
            if (_simulationActive)
                Array.Copy(_simulation, _raw, 6);
        }
    }
}
