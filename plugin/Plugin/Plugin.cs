using System;
using System.IO;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SpaceMouseStudio
{
    /// <summary>
    /// Entry point, called once by <c>SpaceMouseStudio.Loader</c> when Studio starts.
    /// Creates the <see cref="Controller"/> and hooks it into Unity's frame loop.
    /// </summary>
    public static class Plugin
    {
        internal static Controller Controller { get; private set; }

        /// <param name="dir">Plugin folder (%LOCALAPPDATA%\SpaceMouseStudio) for config.ini and log.txt.</param>
        public static void Start(string dir)
        {
            if (Controller != null)
                return;
            Directory.CreateDirectory(dir);
            Log.Init(dir);
            Log.Info("SpaceMouse for BrickLink Studio " + typeof(Plugin).Assembly.GetName().Version +
                     " starting (Unity " + Application.unityVersion + ")");
            Controller = new Controller(dir);

            // Preferred: a MonoBehaviour that survives scene changes and gets Update() every frame.
            Driver driver = null;
            try
            {
                var go = new GameObject("SpaceMouseStudio");
                Object.DontDestroyOnLoad(go);
                driver = go.AddComponent<Driver>();
            }
            catch (Exception e)
            {
                Log.Info("AddComponent failed: " + e.Message);
            }
            if (driver != null)
            {
                Log.Info("Control running (Update)");
            }
            else
            {
                // Fallback if Unity refuses a component from a dynamically loaded assembly.
                Application.onBeforeRender += OnBeforeRender;
                Log.Info("Control running (onBeforeRender)");
            }
        }

        private static void OnBeforeRender()
        {
            if (!Controller.Tick())
            {
                Application.onBeforeRender -= OnBeforeRender;
                Log.Info("Too many errors - SpaceMouse control disabled");
            }
        }
    }

    /// <summary>Calls the controller once per frame on Unity's main thread.</summary>
    public sealed class Driver : MonoBehaviour
    {
        private void Update()
        {
            if (!Plugin.Controller.Tick())
            {
                enabled = false;
                Log.Info("Too many errors - SpaceMouse control disabled");
            }
        }

        private void OnApplicationQuit()
        {
            Plugin.Controller.Shutdown();
        }
    }
}
