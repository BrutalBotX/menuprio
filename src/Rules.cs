using System;
using System.Collections.Generic;
using System.IO;

namespace MenuPrio
{
    internal sealed class Rule
    {
        public string Query;
        public bool Prefix;
        public string Target;
        public string Args;
        public string WorkingDir;

        public override string ToString()
        {
            return (Prefix ? "prefix " : "exact ") + Query + " => " + Target;
        }
    }

    /// <summary>
    /// Legacy rules.ini parser. Since the history store exists, rules are only
    /// imported (first run / tray menu), never used for live interception.
    /// </summary>
    internal static class RuleFile
    {
        public static Rule DefaultOpenCode()
        {
            return new Rule
            {
                Query = "open",
                Prefix = false,
                Target = Clean("%LOCALAPPDATA%\\Programs\\@opencodedesktop\\OpenCode.exe"),
                Args = "",
                WorkingDir = ""
            };
        }

        public static List<Rule> Load(string path, bool defaultIfMissing)
        {
            var rules = new List<Rule>();

            if (!File.Exists(path))
            {
                if (defaultIfMissing) rules.Add(DefaultOpenCode());
                return rules;
            }

            try
            {
                var lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line[0] == '@') continue;

                    var rule = ParseLine(line);
                    if (rule == null || string.IsNullOrEmpty(rule.Query) || string.IsNullOrEmpty(rule.Target))
                    {
                        Log.Warn("rules: line " + (i + 1) + " not understood: " + line);
                        continue;
                    }
                    rules.Add(rule);
                }
            }
            catch (Exception ex)
            {
                Log.Error("rules: could not read " + path, ex);
            }

            return rules;
        }

        public static Rule ParseLine(string line)
        {
            bool prefix = false;

            if (line.StartsWith("prefix", StringComparison.OrdinalIgnoreCase) &&
                line.Length > 6 && (line[6] == ' ' || line[6] == '\t'))
            {
                prefix = true;
                line = line.Substring(6).Trim();
            }
            else if (line.StartsWith("exact", StringComparison.OrdinalIgnoreCase) &&
                     line.Length > 5 && (line[5] == ' ' || line[5] == '\t'))
            {
                line = line.Substring(5).Trim();
            }

            int eq = line.IndexOf('=');
            if (eq <= 0) return null;

            var rule = new Rule();
            rule.Prefix = prefix;
            rule.Query = line.Substring(0, eq).Trim().ToLowerInvariant();

            var parts = line.Substring(eq + 1).Split('|');
            rule.Target = Clean(parts[0]);
            if (parts.Length > 1) rule.Args = Clean(parts[1]);
            if (parts.Length > 2) rule.WorkingDir = Clean(parts[2]);
            return rule;
        }

        public static string Clean(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                s = s.Substring(1, s.Length - 2);
            return Environment.ExpandEnvironmentVariables(s);
        }
    }
}
