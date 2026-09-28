using System;
using System.IO;

namespace MenuPrio
{
    internal static class SelfTest
    {
        public static int Run()
        {
            int failures = 0;
            Console.WriteLine("MenuPrio self-test");
            Console.WriteLine("------------------");

            var root = Path.Combine(Path.GetTempPath(), "MenuPrioTest");
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            catch
            {
                // leftover files are fine; the store overwrites its own files
            }
            Directory.CreateDirectory(root);

            failures += RuleTests(root);
            failures += MatchingTests(root);
            failures += EditingTests(root);
            failures += JsonTests(root);

            Console.WriteLine("------------------");
            Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILURE(S)");
            return failures == 0 ? 0 : 1;
        }

        // ---------------- rules.ini parsing ----------------

        private static int RuleTests(string root)
        {
            int f = 0;
            Console.WriteLine("rules:");

            var rulesPath = Path.Combine(root, "rules_for_import.ini");
            File.WriteAllText(rulesPath,
                "@processes = A, B\r\n" +
                "exact open = C:\\x\\OpenCode.exe\r\n" +
                "prefix op = C:\\z\\other.exe\r\n" +
                "prefix code = C:\\y\\cmd.exe | /k opencode | C:\\y\r\n" +
                "exact quoted = \"C:\\Program Files\\Some App\\app.exe\" | --flag\r\n" +
                "# comment\r\n" +
                "this line is garbage\r\n");

            var rules = RuleFile.Load(rulesPath, false);
            f += Check("4 rules parsed", rules.Count == 4);

            Rule open = null, code = null, quoted = null;
            foreach (var r in rules)
            {
                if (r.Query == "open") open = r;
                if (r.Query == "code") code = r;
                if (r.Query == "quoted") quoted = r;
            }

            f += Check("exact rule", open != null && !open.Prefix && open.Target == "C:\\x\\OpenCode.exe");
            f += Check("prefix rule", code != null && code.Prefix);
            f += Check("args parsed", code != null && code.Args == "/k opencode");
            f += Check("workdir parsed", code != null && code.WorkingDir == "C:\\y");
            f += Check("quotes stripped", quoted != null && quoted.Target == "C:\\Program Files\\Some App\\app.exe");
            return f;
        }

        // ---------------- typing-along matching + priority order ----------------

        private static int MatchingTests(string root)
        {
            int f = 0;
            Console.WriteLine("matching:");

            var rulesPath = Path.Combine(root, "matching-rules.ini");
            File.WriteAllText(rulesPath, "exact open = C:\\x\\OpenCode.exe\r\n");
            var storePath = Path.Combine(root, "matching-history.json");

            var store = new HistoryStore(storePath, rulesPath);

            f += Check("seeded from rules.ini", store.Find("open") != null && store.Find("open").Top.Target == "C:\\x\\OpenCode.exe");
            f += Check("exact match", store.Find("open") != null);
            f += Check("typing along: o", store.Find("o") != null && store.Find("o").Top.Target == "C:\\x\\OpenCode.exe");
            f += Check("typing along: op", store.Find("op") != null);
            f += Check("typing along: ope", store.Find("ope") != null);
            f += Check("longer word matches key", store.Find("opencode") != null);
            f += Check("no random match", store.Find("zzz") == null);
            f += Check("match is case-insensitive", store.Find("OPEN") != null);

            // second, similar word
            store.AddCandidate("opa", new Candidate { Name = "OpaApp", Target = "C:\\y\\OpaApp.exe", Source = "manual" });
            f += Check("second word added", store.Find("opa") != null && store.Find("opa").Top.Target == "C:\\y\\OpaApp.exe");

            // ambiguous prefixes follow the left pane order
            f += Check("ambiguous op uses pane order (open first)", store.Find("op").Top.Target == "C:\\x\\OpenCode.exe");
            f += Check("move group to top", store.MoveGroup("opa", 0));
            f += Check("ambiguous op now picks opa", store.Find("op").Top.Target == "C:\\y\\OpaApp.exe");
            f += Check("ambiguous o now picks opa", store.Find("o").Top.Target == "C:\\y\\OpaApp.exe");
            f += Check("exact open still wins for open", store.Find("open").Top.Target == "C:\\x\\OpenCode.exe");
            f += Check("exact opa still wins for opa", store.Find("opa").Top.Target == "C:\\y\\OpaApp.exe");
            f += Check("longer/openx picks open", store.Find("openx").Top.Target == "C:\\x\\OpenCode.exe");

            // strict group
            store.SetStrict("open", true);
            f += Check("strict: exact still matches", store.Find("open") != null);
            f += Check("strict: typing along disabled", store.Find("ope") == null || store.Find("ope").Group.Key != "open");
            f += Check("strict: longer word disabled", store.Find("opencode") == null || store.Find("opencode").Group.Key != "open");
            store.SetStrict("open", false);
            f += Check("typing along re-enabled", store.Find("ope") != null);

            // persistence: order and strict flag survive a reload
            var store2 = new HistoryStore(storePath, rulesPath);
            var groups = store2.SnapshotGroups();
            f += Check("group order persisted", groups.Count >= 2 && groups[0].Key == "opa" && groups[1].Key == "open");
            f += Check("ambiguous op persisted", store2.Find("op").Top.Target == "C:\\y\\OpaApp.exe");

            // events + counts
            store2.RecordLaunch(store2.Find("open"), "open", "menu");
            f += Check("event recorded", store2.SnapshotEvents(10).Count == 1);
            f += Check("count bumped", FindGroup(store2, "open").Candidates[0].Count == 1);

            return f;
        }

        // ---------------- editing / observation ----------------

        private static int EditingTests(string root)
        {
            int f = 0;
            Console.WriteLine("editing:");

            var rulesPath = Path.Combine(root, "editing-rules.ini");
            File.WriteAllText(rulesPath, "exact open = C:\\x\\OpenCode.exe\r\n");
            var storePath = Path.Combine(root, "editing-history.json");

            var store = new HistoryStore(storePath, rulesPath);
            store.RecordObserved("open", "cmd", "C:\\Windows\\System32\\cmd.exe", "", "", "observed");

            var open = FindGroup(store, "open");
            f += Check("observed app added", open != null && open.Candidates.Count == 2);
            f += Check("rule candidate still on top", open != null && open.Candidates[0].Target == "C:\\x\\OpenCode.exe");

            store.RecordObserved("open", "cmd", "C:\\Windows\\System32\\cmd.exe", "", "", "observed");
            open = FindGroup(store, "open");
            f += Check("repeat observation merges", open != null && open.Candidates.Count == 2 && open.Candidates[1].Count == 2);

            f += Check("remove candidate", store.RemoveCandidateAt("open", 0));
            f += Check("top changes after remove", store.Find("open").Top.Target == "C:\\Windows\\System32\\cmd.exe");
            f += Check("remove group", store.RemoveGroup("open") && store.Find("open") == null);

            // rules import
            var rules2 = Path.Combine(root, "rules2.ini");
            File.WriteAllText(rules2, "exact term = C:\\t\\wt.exe\r\n");
            int added = store.ImportRulesFile(rules2);
            f += Check("rules import adds candidate", added == 1 && store.Find("term") != null);

            return f;
        }

        // ---------------- JSON round-trip ----------------

        private static int JsonTests(string root)
        {
            int f = 0;
            Console.WriteLine("json:");

            var rulesPath = Path.Combine(root, "json-rules.ini");
            File.WriteAllText(rulesPath, "exact open = C:\\x\\OpenCode.exe\r\n");
            var storePath = Path.Combine(root, "json-history.json");

            var store = new HistoryStore(storePath, rulesPath);
            store.AddCandidate("weird", new Candidate
            {
                Name = "Quote \" and backslash \\ app",
                Target = "C:\\Program Files\\A \"weird\" app\\app.exe",
                Args = "--msg \"line1\\nline2\" --tab\tend",
                WorkDir = "C:\\temp\\caf\u00e9",
                Source = "manual"
            });
            store.RecordLaunch(store.Find("weird"), "weird", "menu");

            var store2 = new HistoryStore(storePath, rulesPath);
            var w = FindGroup(store2, "weird");
            f += Check("quotes and backslashes survive", w != null && w.Candidates.Count == 1
                && w.Candidates[0].Name == "Quote \" and backslash \\ app"
                && w.Candidates[0].Target == "C:\\Program Files\\A \"weird\" app\\app.exe");
            f += Check("escapes and unicode survive", w != null
                && w.Candidates[0].Args == "--msg \"line1\\nline2\" --tab\tend"
                && w.Candidates[0].WorkDir == "C:\\temp\\caf\u00e9");
            f += Check("stats survive", w != null && w.Candidates[0].Count == 1);
            f += Check("events survive", store2.SnapshotEvents(10).Count == 1);

            store2.RecordObserved("nulls", "cmd", "C:\\Windows\\System32\\cmd.exe", null, null, "observed");
            var store3 = new HistoryStore(storePath, rulesPath);
            f += Check("null fields survive", FindGroup(store3, "nulls") != null);

            var badPath = Path.Combine(root, "bad-history.json");
            File.WriteAllText(badPath, "{oops");
            var badStore = new HistoryStore(badPath, rulesPath);
            f += Check("corrupt file recovers", badStore.Find("open") != null && File.Exists(badPath + ".bad"));

            return f;
        }

        // ---------------- helpers ----------------

        private static HistoryGroup FindGroup(HistoryStore store, string key)
        {
            foreach (var g in store.SnapshotGroups())
                if (g.Key == key) return g;
            return null;
        }

        private static int Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + name);
            return ok ? 0 : 1;
        }
    }
}
