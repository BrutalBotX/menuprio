using System;
using System.IO;

namespace MenuPrio
{
    internal static class Log
    {
        private static readonly object Sync = new object();
        private static string _path;

        /// <summary>Log buffered keystrokes too (off by default).</summary>
        public static bool Verbose;

        public static string Path
        {
            get { return _path; }
        }

        public static void Init(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                _path = System.IO.Path.Combine(dir, "menuprio.log");

                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > 1024 * 1024)
                {
                    var old = _path + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(_path, old);
                }
            }
            catch
            {
                _path = null;
            }
        }

        public static void Info(string msg) { Write("INFO", msg); }
        public static void Warn(string msg) { Write("WARN", msg); }
        public static void Error(string msg) { Error(msg, null); }

        public static void Error(string msg, Exception ex)
        {
            Write("ERR ", ex == null ? msg : msg + " :: " + ex.GetType().Name + ": " + ex.Message);
        }

        private static void Write(string level, string msg)
        {
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] " + msg;
            System.Diagnostics.Debug.WriteLine(line);
            try { Console.WriteLine(line); } catch { }

            if (_path == null) return;
            try
            {
                lock (Sync)
                {
                    File.AppendAllText(_path, line + Environment.NewLine);
                }
            }
            catch
            {
                // never let logging kill the app
            }
        }
    }
}
