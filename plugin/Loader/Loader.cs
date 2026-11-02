using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace SpaceMouseStudio
{
    /// <summary>
    /// Tiny bootstrapper that lives in Studio's installation folder (Studio_Data\Managed).
    ///
    /// Unity calls <see cref="Boot"/> when Studio starts, because the installer adds this class to
    /// Studio_Data\RuntimeInitializeOnLoads.json and the assembly to ScriptingAssemblies.json.
    ///
    /// The loader then loads the actual plugin from %LOCALAPPDATA%\SpaceMouseStudio. The plugin is
    /// loaded from a byte array, so the DLL file is not locked and can be updated without
    /// administrator rights, even while Studio is running. Only this loader needs admin rights to
    /// install, and it rarely changes.
    /// </summary>
    public static class Loader
    {
        private static bool _booted;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        public static void Boot()
        {
            if (_booted)
                return;
            _booted = true;
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceMouseStudio");
            try
            {
                string dll = Path.Combine(dir, "SpaceMouseStudio.dll");
                if (!File.Exists(dll))
                    return;
                Assembly plugin = Assembly.Load(File.ReadAllBytes(dll));
                Type entry = plugin.GetType("SpaceMouseStudio.Plugin", true);
                entry.GetMethod("Start", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { dir });
            }
            catch (Exception e)
            {
                // the plugin must never prevent Studio from starting
                try
                {
                    Directory.CreateDirectory(dir);
                    File.AppendAllText(Path.Combine(dir, "log.txt"), DateTime.Now + " Loader error: " + e + Environment.NewLine);
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
