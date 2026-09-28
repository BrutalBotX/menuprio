using System;
using System.Collections.Generic;
using System.IO;

namespace MenuPrio
{
    internal sealed class Candidate
    {
        public string Name { get; set; }
        public string Target { get; set; }
        public string Args { get; set; }
        public string WorkDir { get; set; }
        public int Count { get; set; }
        public DateTime LastUsed { get; set; }
        public string Source { get; set; } // rule | menu | observed | manual

        public Candidate Clone()
        {
            return (Candidate)MemberwiseClone();
        }

        public override string ToString()
        {
            return (Name ?? "") + " -> " + (Target ?? "");
        }
    }

    internal sealed class HistoryGroup
    {
        public string Key { get; set; }

        /// <summary>Exact-only when true. Otherwise the group also matches while typing
        /// ("open" matches o, op, ope, open and also opencode).</summary>
        public bool Strict { get; set; }

        public List<Candidate> Candidates { get; set; }

        public HistoryGroup()
        {
            Candidates = new List<Candidate>();
        }

        public HistoryGroup Clone()
        {
            var copy = new HistoryGroup { Key = Key, Strict = Strict };
            foreach (var c in Candidates) copy.Candidates.Add(c.Clone());
            return copy;
        }
    }

    internal sealed class HistoryEvent
    {
        public DateTime Time { get; set; }
        public string Typed { get; set; }
        public string Opened { get; set; }
        public string Target { get; set; }
        public string Source { get; set; }
    }

    internal sealed class HistoryData
    {
        public Dictionary<string, HistoryGroup> Groups { get; set; }

        /// <summary>Priority order of the left pane. Earlier = higher priority for
        /// ambiguous prefixes (typing "op" while "open" and "opa" both match).</summary>
        public List<string> GroupOrder { get; set; }

        public List<HistoryEvent> Events { get; set; }

        public HistoryData()
        {
            Groups = new Dictionary<string, HistoryGroup>(StringComparer.OrdinalIgnoreCase);
            GroupOrder = new List<string>();
            Events = new List<HistoryEvent>();
        }
    }

    internal sealed class HistoryMatch
    {
        public HistoryGroup Group { get; set; }
        public Candidate Top { get; set; }
    }

    /// <summary>
    /// Typed text -> ordered candidates ("what should Enter open") plus a capped
    /// activity log and a priority order for ambiguous prefixes. Thread safe;
    /// persists to JSON on every change.
    /// </summary>
    internal sealed class HistoryStore
    {
        public const int MaxEvents = 400;

        private readonly object _sync = new object();
        private readonly string _path;
        private readonly string _rulesPath;
        private HistoryData _data = new HistoryData();

        public HistoryStore(string path, string rulesPath)
        {
            _path = path;
            _rulesPath = rulesPath;

            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            Load();
        }

        public string Path { get { return _path; } }

        public static string DefaultPath
        {
            get { return System.IO.Path.Combine(AppDir(), "history.json"); }
        }

        public static string RulesPath
        {
            get { return System.IO.Path.Combine(AppDir(), "rules.ini"); }
        }

        public static string AppDir()
        {
            return System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MenuPrio");
        }

        // ---------------- loading / seeding ----------------

        private void Load()
        {
            lock (_sync)
            {
                if (File.Exists(_path))
                {
                    try
                    {
                        var json = File.ReadAllText(_path);
                        var data = Json.Read(json);
                        if (data != null)
                        {
                            _data = Sanitize(data);
                            Log.Info("history: loaded " + _data.Groups.Count + " group(s), "
                                + _data.Events.Count + " event(s) from " + _path);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error("history: could not read " + _path + " (backing up and starting fresh)", ex);
                        try { File.Copy(_path, _path + ".bad", true); } catch { }
                    }
                }

                _data = Seed();
                SaveLocked();
                Log.Info("history: created " + _path + " (" + _data.Groups.Count + " group(s))");
            }
        }

        private static HistoryData Sanitize(HistoryData data)
        {
            if (data.Groups == null) data.Groups = new Dictionary<string, HistoryGroup>(StringComparer.OrdinalIgnoreCase);
            if (data.Events == null) data.Events = new List<HistoryEvent>();
            if (data.GroupOrder == null) data.GroupOrder = new List<string>();

            var groups = new Dictionary<string, HistoryGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in data.Groups)
            {
                var g = kv.Value;
                if (g == null) continue;
                if (g.Candidates == null) g.Candidates = new List<Candidate>();
                var key = Normalize(!string.IsNullOrEmpty(g.Key) ? g.Key : kv.Key);
                if (key.Length == 0) continue;
                g.Key = key;
                groups[key] = g;
            }
            data.Groups = groups;

            // keep only known groups, remove duplicates, append missing ones alphabetically
            var order = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in data.GroupOrder)
            {
                var k = Normalize(raw);
                if (groups.ContainsKey(k) && seen.Add(k)) order.Add(k);
            }
            var missing = new List<string>();
            foreach (var k in groups.Keys)
                if (!seen.Contains(k)) missing.Add(k);
            missing.Sort(StringComparer.OrdinalIgnoreCase);
            order.AddRange(missing);
            data.GroupOrder = order;

            return data;
        }

        /// <summary>First run: import the old rules.ini (each rule becomes the first candidate of its group).</summary>
        private HistoryData Seed()
        {
            var data = new HistoryData();
            List<Rule> rules;
            try
            {
                rules = RuleFile.Load(_rulesPath, true);
            }
            catch (Exception ex)
            {
                Log.Error("history: could not import rules file", ex);
                rules = new List<Rule>();
            }

            foreach (var r in rules) MergeRule(data, r);
            return data;
        }

        private static void EnsureOrder(HistoryData data, string key)
        {
            if (data.GroupOrder == null) data.GroupOrder = new List<string>();
            foreach (var k in data.GroupOrder)
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return;
            data.GroupOrder.Add(key);
        }

        private static void MergeRule(HistoryData data, Rule r)
        {
            var key = Normalize(r.Query);
            if (key.Length == 0 || string.IsNullOrEmpty(r.Target)) return;

            HistoryGroup g;
            if (!data.Groups.TryGetValue(key, out g))
            {
                g = new HistoryGroup { Key = key };
                data.Groups[key] = g;
                EnsureOrder(data, key);
            }

            foreach (var c in g.Candidates)
                if (TargetsMatch(c, r.Target, null)) return;

            g.Candidates.Add(new Candidate
            {
                Name = FriendlyName(r.Target),
                Target = r.Target,
                Args = r.Args,
                WorkDir = r.WorkingDir,
                Source = "rule"
            });
        }

        /// <summary>Merges rules.ini again (tray menu). Returns how many candidates were added.</summary>
        public int ImportRulesFile(string path)
        {
            lock (_sync)
            {
                var rules = RuleFile.Load(path, false);
                int before = 0, after = 0;
                foreach (var g in _data.Groups.Values) before += g.Candidates.Count;

                foreach (var r in rules) MergeRule(_data, r);

                foreach (var g in _data.Groups.Values) after += g.Candidates.Count;
                SaveLocked();
                return after - before;
            }
        }

        // ---------------- matching ----------------

        // 0 = exact, 1 = the stored word starts with what was typed (typing along),
        // 2 = the typed text starts with the stored word (typed a longer word),
        // -1 = no match.
        private static int MatchRank(string typed, string key, bool strict)
        {
            if (typed == key) return 0;
            if (strict) return -1;
            if (key.StartsWith(typed, StringComparison.Ordinal)) return 1;
            if (typed.StartsWith(key, StringComparison.Ordinal)) return 2;
            return -1;
        }

        /// <summary>
        /// Picks the matching group: exact beats typing-along beats longer-word;
        /// ties are broken by the left pane priority order.
        /// </summary>
        public HistoryMatch Find(string typed)
        {
            lock (_sync)
            {
                var q = Normalize(typed);
                if (q.Length == 0) return null;

                var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < _data.GroupOrder.Count; i++) order[_data.GroupOrder[i]] = i;

                HistoryGroup best = null;
                int bestRank = 0;
                int bestOrder = 0;
                bool hasBest = false;

                foreach (var g in _data.Groups.Values)
                {
                    if (g.Candidates == null || g.Candidates.Count == 0) continue;

                    int rank = MatchRank(q, g.Key, g.Strict);
                    if (rank < 0) continue;

                    int ord;
                    if (!order.TryGetValue(g.Key, out ord)) ord = int.MaxValue;

                    bool better;
                    if (!hasBest) better = true;
                    else if (rank != bestRank) better = rank < bestRank;
                    else if (rank == 2 && g.Key.Length != best.Key.Length) better = g.Key.Length > best.Key.Length;
                    else better = ord < bestOrder;

                    if (better)
                    {
                        best = g;
                        bestRank = rank;
                        bestOrder = ord;
                        hasBest = true;
                    }
                }

                if (!hasBest) return null;
                return new HistoryMatch { Group = best, Top = best.Candidates[0] };
            }
        }

        // ---------------- recording ----------------

        /// <summary>We launched a candidate ourselves; stats + event.</summary>
        public void RecordLaunch(HistoryMatch match, string typed, string source)
        {
            if (match == null || match.Top == null) return;
            lock (_sync)
            {
                TouchLocked(match.Group, match.Top, typed, Display(match.Top), source);
            }
        }

        /// <summary>A process appeared after a search; figure out which candidate it was and record it.</summary>
        public void RecordObserved(string typed, string name, string target, string args, string workdir, string source)
        {
            var key = Normalize(typed);
            if (key.Length == 0) return;

            lock (_sync)
            {
                HistoryGroup g;
                if (!_data.Groups.TryGetValue(key, out g))
                {
                    g = new HistoryGroup { Key = key };
                    _data.Groups[key] = g;
                    EnsureOrder(_data, key);
                }

                Candidate c = null;
                foreach (var existing in g.Candidates)
                {
                    if (TargetsMatch(existing, target, name)) { c = existing; break; }
                }

                if (c == null)
                {
                    c = new Candidate
                    {
                        Name = string.IsNullOrEmpty(name) ? FriendlyName(target) : name,
                        Target = string.IsNullOrEmpty(target) ? (name + ".exe") : target,
                        Args = args ?? "",
                        WorkDir = workdir ?? "",
                        Source = source
                    };
                    g.Candidates.Add(c);
                }

                TouchLocked(g, c, typed, Display(c), source);
            }
        }

        private void TouchLocked(HistoryGroup g, Candidate c, string typed, string opened, string source)
        {
            c.Count++;
            c.LastUsed = DateTime.Now;
            _data.Events.Insert(0, new HistoryEvent
            {
                Time = DateTime.Now,
                Typed = typed,
                Opened = opened,
                Target = c.Target,
                Source = source
            });
            if (_data.Events.Count > MaxEvents)
                _data.Events.RemoveRange(MaxEvents, _data.Events.Count - MaxEvents);

            SaveLocked();
        }

        // ---------------- editing (UI) ----------------

        public void AddCandidate(string key, Candidate c)
        {
            key = Normalize(key);
            if (key.Length == 0 || c == null || string.IsNullOrEmpty(c.Target)) return;

            lock (_sync)
            {
                HistoryGroup g;
                if (!_data.Groups.TryGetValue(key, out g))
                {
                    g = new HistoryGroup { Key = key };
                    _data.Groups[key] = g;
                    EnsureOrder(_data, key);
                }

                foreach (var existing in g.Candidates)
                    if (TargetsMatch(existing, c.Target, null)) { existing.Source = "manual"; SaveLocked(); return; }

                if (string.IsNullOrEmpty(c.Source)) c.Source = "manual";
                g.Candidates.Add(c);
                SaveLocked();
            }
        }

        public void UpdateCandidateAt(string key, int index, Candidate edited)
        {
            lock (_sync)
            {
                var g = GetGroupLocked(key);
                if (g == null || index < 0 || index >= g.Candidates.Count) return;

                var c = g.Candidates[index];
                c.Name = edited.Name;
                c.Target = edited.Target;
                c.Args = edited.Args;
                c.WorkDir = edited.WorkDir;
                SaveLocked();
            }
        }

        public bool RemoveCandidateAt(string key, int index)
        {
            lock (_sync)
            {
                var g = GetGroupLocked(key);
                if (g == null || index < 0 || index >= g.Candidates.Count) return false;
                g.Candidates.RemoveAt(index);
                SaveLocked();
                return true;
            }
        }

        public bool RemoveGroup(string key)
        {
            lock (_sync)
            {
                var k = Normalize(key);
                if (!_data.Groups.Remove(k)) return false;
                _data.GroupOrder.RemoveAll(delegate(string s)
                {
                    return string.Equals(s, k, StringComparison.OrdinalIgnoreCase);
                });
                SaveLocked();
                return true;
            }
        }

        public bool MoveCandidate(string key, int from, int to)
        {
            lock (_sync)
            {
                var g = GetGroupLocked(key);
                if (g == null || from < 0 || from >= g.Candidates.Count) return false;
                if (to < 0) to = 0;
                if (to > g.Candidates.Count - 1) to = g.Candidates.Count - 1;
                if (from == to) return false;

                var c = g.Candidates[from];
                g.Candidates.RemoveAt(from);
                g.Candidates.Insert(to, c);
                SaveLocked();
                return true;
            }
        }

        /// <summary>Moves a group to index <paramref name="to"/> in the left pane order.</summary>
        public bool MoveGroup(string key, int to)
        {
            lock (_sync)
            {
                key = Normalize(key);
                EnsureAllGroupsInOrderLocked();

                int from = _data.GroupOrder.FindIndex(delegate(string s)
                {
                    return string.Equals(s, key, StringComparison.OrdinalIgnoreCase);
                });
                if (from < 0) return false;

                if (to < 0) to = 0;
                if (to > _data.GroupOrder.Count - 1) to = _data.GroupOrder.Count - 1;
                if (to == from) return false;

                _data.GroupOrder.RemoveAt(from);
                _data.GroupOrder.Insert(to, key);
                SaveLocked();
                return true;
            }
        }

        public void SetStrict(string key, bool strict)
        {
            lock (_sync)
            {
                var g = GetGroupLocked(key);
                if (g == null) return;
                g.Strict = strict;
                SaveLocked();
            }
        }

        /// <summary>Picks the matching group for a typed text, read-only (used by the UI preview).</summary>
        public HistoryMatch Preview(string typed)
        {
            return Find(typed);
        }

        // ---------------- snapshots (UI) ----------------

        public List<HistoryGroup> SnapshotGroups()
        {
            lock (_sync)
            {
                EnsureAllGroupsInOrderLocked();

                var list = new List<HistoryGroup>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var k in _data.GroupOrder)
                {
                    HistoryGroup g;
                    if (_data.Groups.TryGetValue(k, out g) && seen.Add(k)) list.Add(g.Clone());
                }
                foreach (var kv in _data.Groups)
                    if (seen.Add(kv.Key)) list.Add(kv.Value.Clone());

                return list;
            }
        }

        public List<HistoryEvent> SnapshotEvents(int max)
        {
            lock (_sync)
            {
                int n = Math.Min(max, _data.Events.Count);
                return new List<HistoryEvent>(_data.Events.GetRange(0, n));
            }
        }

        // ---------------- helpers ----------------

        private void EnsureAllGroupsInOrderLocked()
        {
            if (_data.GroupOrder == null) _data.GroupOrder = new List<string>();

            var seen = new HashSet<string>(_data.GroupOrder, StringComparer.OrdinalIgnoreCase);
            var missing = new List<string>();
            foreach (var k in _data.Groups.Keys)
                if (!seen.Contains(k)) missing.Add(k);

            if (missing.Count == 0) return;
            missing.Sort(StringComparer.OrdinalIgnoreCase);
            _data.GroupOrder.AddRange(missing);
        }

        private HistoryGroup GetGroupLocked(string key)
        {
            HistoryGroup g;
            return _data.Groups.TryGetValue(Normalize(key), out g) ? g : null;
        }

        private static string Normalize(string s)
        {
            return (s ?? "").Trim().ToLowerInvariant();
        }

        private static string Display(Candidate c)
        {
            if (!string.IsNullOrEmpty(c.Name)) return c.Name;
            return FriendlyName(c.Target);
        }

        public static string FriendlyName(string target)
        {
            if (string.IsNullOrEmpty(target)) return "";
            try
            {
                var file = System.IO.Path.GetFileName(target);
                if (string.IsNullOrEmpty(file)) return target;
                var name = System.IO.Path.GetFileNameWithoutExtension(file);
                return name.Length > 0 ? name : file;
            }
            catch
            {
                return target;
            }
        }

        private static string FileNameOrEmpty(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try
            {
                var file = System.IO.Path.GetFileName(path);
                return file ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static string StripExe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? s.Substring(0, s.Length - 4)
                : s;
        }

        private static bool TargetsMatch(Candidate c, string observedTarget, string observedName)
        {
            if (c == null || string.IsNullOrEmpty(c.Target)) return false;

            if (!string.IsNullOrEmpty(observedTarget)
                && string.Equals(c.Target, observedTarget, StringComparison.OrdinalIgnoreCase))
                return true;

            var cFile = FileNameOrEmpty(c.Target);
            if (cFile.Length > 0)
            {
                if (!string.IsNullOrEmpty(observedTarget))
                {
                    var oFile = FileNameOrEmpty(observedTarget);
                    if (oFile.Length > 0 && string.Equals(cFile, oFile, StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                if (!string.IsNullOrEmpty(observedName)
                    && string.Equals(StripExe(cFile), observedName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (!string.IsNullOrEmpty(observedName)
                && string.Equals(StripExe(c.Target), observedName, StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        private void SaveLocked()
        {
            try
            {
                var json = Json.Write(_data);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(_path)) File.Replace(tmp, _path, null);
                else File.Move(tmp, _path);
            }
            catch (Exception ex)
            {
                Log.Error("history: save failed", ex);
            }
        }
    }
}
