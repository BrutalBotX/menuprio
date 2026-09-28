using System;
using System.Collections.Generic;
using System.IO;

namespace MenuPrio
{
    /// <summary>App settings (currently just which windows count as the launcher).</summary>
    internal sealed class Config
    {
        private readonly List<string> _launcherProcesses = new List<string>();

        public static string DefaultPath
        {
            get { return System.IO.Path.Combine(HistoryStore.AppDir(), "config.ini"); }
        }

        public static Config Load(string path, string fallbackRulesPath)
        {
            var cfg = new Config();
            bool found = false;

            try
            {
                if (File.Exists(path))
                {
                    foreach (var raw in File.ReadAllLines(path))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                        if (line.StartsWith("@processes", StringComparison.OrdinalIgnoreCase))
                        {
                            cfg._launcherProcesses.Clear();
                            var value = line.Substring("@processes".Length).TrimStart('=', ' ', '\t');
                            foreach (var part in value.Split(','))
                            {
                                var name = part.Trim();
                                if (name.Length > 0) cfg._launcherProcesses.Add(name);
                            }
                            found = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("config: could not read " + path, ex);
            }

            // carry over from the old rules.ini, if it had a custom list
            if (!found && !string.IsNullOrEmpty(fallbackRulesPath) && File.Exists(fallbackRulesPath))
            {
                try
                {
                    foreach (var raw in File.ReadAllLines(fallbackRulesPath))
                    {
                        var line = raw.Trim();
                        if (!line.StartsWith("@processes", StringComparison.OrdinalIgnoreCase)) continue;
                        cfg._launcherProcesses.Clear();
                        var value = line.Substring("@processes".Length).TrimStart('=', ' ', '\t');
                        foreach (var part in value.Split(','))
                        {
                            var name = part.Trim();
                            if (name.Length > 0) cfg._launcherProcesses.Add(name);
                        }
                        found = true;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("config: could not read " + fallbackRulesPath, ex);
                }
            }

            if (cfg._launcherProcesses.Count == 0)
            {
                cfg._launcherProcesses.Add("StartMenuExperienceHost");
                cfg._launcherProcesses.Add("SearchHost");
                cfg._launcherProcesses.Add("SearchUI");
            }

            if (!File.Exists(path))
            {
                try
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                    File.WriteAllText(path,
                        "# MenuPrio settings\r\n" +
                        "# Windows that count as the Start menu / search.\r\n" +
                        "@processes = " + string.Join(", ", cfg._launcherProcesses.ToArray()) + "\r\n");
                    Log.Info("config: created " + path);
                }
                catch (Exception ex)
                {
                    Log.Error("config: could not write " + path, ex);
                }
            }

            Log.Info("config: launcher processes = " + string.Join(", ", cfg._launcherProcesses.ToArray()));
            return cfg;
        }

        public bool IsLauncherProcess(string processName)
        {
            if (string.IsNullOrEmpty(processName)) return false;
            foreach (var p in _launcherProcesses)
                if (string.Equals(p, processName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
