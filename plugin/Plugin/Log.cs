using System;
using System.IO;

namespace SpaceMouseStudio
{
    /// <summary>
    /// Minimal thread-safe log in the plugin folder (log.txt). The file is cleared on every Studio
    /// start, so it only ever contains the current session. Logging never throws.
    /// </summary>
    internal static class Log
    {
        private static readonly object Sync = new object();
        private static string _path;

        public static void Init(string dir)
        {
            _path = Path.Combine(dir, "log.txt");
            try
            {
                File.WriteAllText(_path, "");
            }
            catch (Exception)
            {
                // a missing log must not stop the plugin
            }
        }

        public static void Info(string message)
        {
            if (_path == null)
                return;
            lock (Sync)
            {
                try
                {
                    File.AppendAllText(_path, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
                }
                catch (Exception)
                {
                    // e.g. the file is open in an editor that locks it
                }
            }
        }
    }
}
