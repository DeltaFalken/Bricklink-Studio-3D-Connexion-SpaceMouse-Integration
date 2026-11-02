using System;
using System.Collections.Generic;
using System.Globalization;

namespace SpaceMouseStudio
{
    /// <summary>
    /// Maps one SpaceMouse axis to one camera function. Written in config.ini as
    /// <c>[+|-]axis[ * factor]</c>, e.g. <c>rotate_vertical = -rx * 0.5</c>, or <c>off</c>.
    /// </summary>
    internal struct AxisMap
    {
        /// <summary>Index into the six device axes (0..5 = tx ty tz rx ry rz), or -1 if the function is off.</summary>
        public int Axis;

        /// <summary>Sign and strength. A leading "-" in the config makes this negative and so reverses the direction.</summary>
        public float Factor;

        /// <summary>Axis names in the order the device reports them.</summary>
        public static readonly string[] Names = { "tx", "ty", "tz", "rx", "ry", "rz" };

        public static AxisMap Parse(string spec)
        {
            string s = spec.Replace(" ", "").ToLowerInvariant();
            if (s == "off" || s == "none" || s == "")
                return new AxisMap { Axis = -1, Factor = 0f };

            // optional "* factor" suffix
            float factor = 1f;
            int star = s.IndexOf('*');
            if (star >= 0)
            {
                factor = Settings.ParseFloat(s.Substring(star + 1));
                s = s.Substring(0, star);
            }

            // optional sign prefix
            if (s.StartsWith("-"))
            {
                factor = -factor;
                s = s.Substring(1);
            }
            else if (s.StartsWith("+"))
            {
                s = s.Substring(1);
            }

            int axis = Array.IndexOf(Names, s);
            if (axis < 0)
                throw new FormatException("unknown axis '" + spec + "'");
            return new AxisMap { Axis = axis, Factor = factor };
        }

        /// <summary>Returns the value of the mapped axis (already shaped by dead zone and curve), times the factor.</summary>
        public float Apply(float[] shaped)
        {
            return Axis < 0 ? 0f : shaped[Axis] * Factor;
        }
    }

    /// <summary>
    /// All user settings, read from config.ini (a simple INI file).
    ///
    /// The field initializers are the defaults. They must match <see cref="DefaultText"/>, which is
    /// written to disk when config.ini does not exist yet. Keys that are missing from the file keep
    /// their default, so an old config.ini keeps working when new settings are added.
    /// </summary>
    internal sealed class Settings
    {
        // [General]
        public bool Enabled = true;
        public float Sensitivity = 1f;      // overall multiplier for all speeds
        public float Deadzone = 0.08f;      // fraction of full scale around the rest position that is ignored
        public float Curve = 1.8f;          // exponent of the response curve, 1 = linear
        public bool DominantAxis;           // use only the strongest of the five camera channels
        public float AxisMax = 350f;        // raw device value at full deflection
        public float Smoothing = 0.05f;     // time constant of the low-pass filter in seconds, 0 = off
        public bool PivotOnModel = true;    // rotate around the model center instead of Studio's camera target
        public bool BlockWheel = true;      // ignore 3DxWare's mouse wheel steps while the cap is deflected

        // [Speed], each at full deflection and sensitivity 1
        public float RotateSpeed = 150f;    // degrees per second
        public float PanSpeed = 700f;       // screen pixels per second, same unit as dragging with the mouse
        public float ZoomSpeed = 1.2f;      // natural log of the zoom factor per second

        // [Display]
        public int MotionFrameRate = -1;    // while moving: -1 = VSync to the monitor, 0 = leave unchanged, else a fixed FPS

        // [Mapping]. 3Dconnexion convention: the cap moves the model, so the camera moves the opposite way.
        public AxisMap RotateH = AxisMap.Parse("-rz");
        public AxisMap RotateV = AxisMap.Parse("rx");
        public AxisMap PanH = AxisMap.Parse("tx");
        public AxisMap PanV = AxisMap.Parse("tz");
        public AxisMap Zoom = AxisMap.Parse("ty");

        // [Buttons]: button number (1-based) -> action name
        public readonly Dictionary<int, string> Buttons = new Dictionary<int, string>
        {
            { 1, "fit_all" },
            { 2, "mode" },
        };

        // [Diagnostics]
        public bool Diagnose;               // record device and camera data on the next movement

        /// <summary>Commented config.ini that is created on first start. Values match the field defaults above.</summary>
        public const string DefaultText =
@"; ============================================================
;  SpaceMouse for BrickLink Studio - settings
;
;  Changes take effect as soon as this file is saved.
;  Studio does not need to be restarted.
;  Delete this file to restore the defaults.
; ============================================================

[General]
; Turn SpaceMouse control on or off (yes/no)
enabled = yes
; Overall sensitivity (1.0 = normal)
sensitivity = 1.0
; Range around the rest position that is ignored (0.0 to 0.5)
deadzone = 0.08
; Response curve: 1 = linear, higher = finer control for small movements
response_curve = 1.8
; Use only the strongest movement at any time (yes/no)
dominant_axis = no
; Smoothing time in seconds (0 = off, higher = softer but less direct)
smoothing = 0.05
; Pivot point for rotations:
;   model   the model turns in place around its center (if the center is not
;           in view: around the point of the model in the middle of the view)
;   target  like the mouse: around Studio's camera target
pivot = model
; 3DxWare does not know Studio and sends mouse wheel steps when the cap is
; tilted. Studio would then zoom in 10 % jumps.
; yes = ignore the mouse wheel while the cap is deflected
block_mouse_wheel = yes

[Speed]
; Each at full deflection
; Rotation in degrees per second
rotate = 150
; Panning in screen pixels per second (like dragging with the mouse)
pan = 700
; Zoom: 1.0 means a factor of about 2.7 per second
zoom = 1.2

[Display]
; Frame rate while the SpaceMouse is moving. Afterwards Studio's own setting
; (30 or 60 FPS) applies again.
;   monitor  synchronized to the display (VSync) - smoothest
;   60       fixed frame rate, e.g. 60 or 120
;   off      leave Studio's setting unchanged
motion_frame_rate = monitor

[Mapping]
; The cap moves the model, the camera moves the opposite way.
; SpaceMouse axes:
;   tx = push cap left/right           rx = tilt cap forward/back
;   ty = push cap forward/back         ry = tilt cap left/right
;   tz = pull cap up/push down         rz = twist cap
; A leading - reverses the direction, off disables the function.
; Optionally with a factor, e.g.:  rotate_vertical = rx * 0.5
rotate_horizontal = -rz
rotate_vertical = rx
pan_horizontal = tx
pan_vertical = tz
zoom = ty

[Buttons]
; Actions:
;   fit_all    fit the whole model into the view (like Ctrl+0)
;   mode       cycles: all -> rotate only -> pan + zoom only
;   toggle     turn SpaceMouse control on/off
;   dominant   strongest movement only, on/off
;   faster     increase sensitivity
;   slower     decrease sensitivity
;   settings   open this file
;   none
; More buttons (e.g. SpaceMouse Pro) can be added as button3, button4, ...
button1 = fit_all
button2 = mode

[Diagnostics]
; yes = record 15 seconds of SpaceMouse and camera data on the next movement
; (diagnostics_device.csv and diagnostics_frames.csv in this folder)
record = no
";

        /// <summary>Parses a number. Accepts a decimal comma as well, since users often type "1,5".</summary>
        public static float ParseFloat(string s)
        {
            return float.Parse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static bool ParseBool(string s)
        {
            switch (s.Trim().ToLowerInvariant())
            {
                case "yes":
                case "on":
                case "true":
                case "1":
                    return true;
                case "no":
                case "off":
                case "false":
                case "0":
                    return false;
            }
            throw new FormatException("expected yes or no, not '" + s + "'");
        }

        private static bool ParsePivot(string s)
        {
            switch (s.Trim().ToLowerInvariant())
            {
                case "model":
                    return true;
                case "target":
                case "studio":
                    return false;
            }
            throw new FormatException("expected model or target, not '" + s + "'");
        }

        private static int ParseFrameRate(string s)
        {
            switch (s.Trim().ToLowerInvariant())
            {
                case "monitor":
                case "vsync":
                    return -1;
                case "off":
                case "no":
                    return 0;
            }
            // a fixed frame rate below 10 FPS would make Studio unusable
            return Math.Max(10, (int)ParseFloat(s));
        }

        /// <summary>
        /// Parses the INI text. Invalid lines are skipped and reported in <paramref name="warnings"/>,
        /// so a single typo never disables the whole configuration.
        /// </summary>
        public static Settings Parse(string text, List<string> warnings)
        {
            var cfg = new Settings();
            string section = "";
            string[] lines = text.Replace("\r", "").Split('\n');
            for (int n = 0; n < lines.Length; n++)
            {
                // strip comments (";" or "#" up to the end of the line)
                string line = lines[n];
                int comment = line.IndexOfAny(new[] { ';', '#' });
                if (comment >= 0)
                    line = line.Substring(0, comment);
                line = line.Trim();
                if (line.Length == 0)
                    continue;

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    warnings.Add("line " + (n + 1) + ": '=' missing");
                    continue;
                }
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string value = line.Substring(eq + 1).Trim();
                try
                {
                    if (!cfg.Apply(section, key, value))
                        warnings.Add("line " + (n + 1) + ": unknown entry '" + key + "' in [" + section + "]");
                }
                catch (Exception e)
                {
                    warnings.Add("line " + (n + 1) + ": " + e.Message);
                }
            }
            return cfg;
        }

        /// <summary>Applies one key/value pair. Returns false if the key is unknown; throws if the value is invalid.</summary>
        private bool Apply(string section, string key, string value)
        {
            switch (section + "/" + key)
            {
                case "general/enabled": Enabled = ParseBool(value); return true;
                case "general/sensitivity": Sensitivity = ParseFloat(value); return true;
                case "general/deadzone": Deadzone = Math.Max(0f, Math.Min(0.9f, ParseFloat(value))); return true;
                case "general/response_curve": Curve = Math.Max(0.2f, ParseFloat(value)); return true;
                case "general/dominant_axis": DominantAxis = ParseBool(value); return true;
                case "general/full_scale": AxisMax = Math.Max(1f, ParseFloat(value)); return true;
                case "general/smoothing": Smoothing = Math.Max(0f, Math.Min(1f, ParseFloat(value))); return true;
                case "general/pivot": PivotOnModel = ParsePivot(value); return true;
                case "general/block_mouse_wheel": BlockWheel = ParseBool(value); return true;
                case "speed/rotate": RotateSpeed = ParseFloat(value); return true;
                case "speed/pan": PanSpeed = ParseFloat(value); return true;
                case "speed/zoom": ZoomSpeed = ParseFloat(value); return true;
                case "display/motion_frame_rate": MotionFrameRate = ParseFrameRate(value); return true;
                case "mapping/rotate_horizontal": RotateH = AxisMap.Parse(value); return true;
                case "mapping/rotate_vertical": RotateV = AxisMap.Parse(value); return true;
                case "mapping/pan_horizontal": PanH = AxisMap.Parse(value); return true;
                case "mapping/pan_vertical": PanV = AxisMap.Parse(value); return true;
                case "mapping/zoom": Zoom = AxisMap.Parse(value); return true;
                case "diagnostics/record": Diagnose = ParseBool(value); return true;
            }

            // [Buttons] button1 ... button32
            int number;
            if (section == "buttons" && key.StartsWith("button") && int.TryParse(key.Substring(6), out number) && number >= 1 && number <= 32)
            {
                Buttons[number] = value.ToLowerInvariant();
                return true;
            }
            return false;
        }
    }
}
