using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;

namespace ScanProbe {
    public static class Program {
        public static int Main() {
            PlayMakerFSM[] fsms = UnityEngine.Resources.FindObjectsOfTypeAll<PlayMakerFSM>();
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> fsmNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int total = 0;
            foreach (var fsm in fsms) {
                if (fsm == null || fsm.FsmVariables == null) continue;
                try {
                    string fname = fsm.FsmName;
                    if (!string.IsNullOrEmpty(fname)) fsmNames.Add(fname);
                } catch { }
                foreach (var v in fsm.FsmVariables.BoolVariables) if (v != null) { names.Add(v.Name); total++; }
                foreach (var v in fsm.FsmVariables.FloatVariables) if (v != null) { names.Add(v.Name); total++; }
                foreach (var v in fsm.FsmVariables.IntVariables) if (v != null) { names.Add(v.Name); total++; }
            }
            Console.WriteLine("Total variables: " + total);
            Console.WriteLine("Unique names: " + names.Count);
            Console.WriteLine("FSM names: " + fsmNames.Count);
            Console.WriteLine();
            
            List<string> clock = new List<string>();
            foreach (var n in names) {
                if (n.IndexOf("hour", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("minute", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("day", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("weather", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("rain", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("cloud", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("time", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("clock", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("sun", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("season", StringComparison.OrdinalIgnoreCase) >= 0) {
                    clock.Add(n);
                }
            }
            clock.Sort();
            Console.WriteLine("=== CLOCK/WEATHER ===");
            foreach (var n in clock) Console.WriteLine("  " + n);
            
            Console.WriteLine();
            List<string> items = new List<string>();
            foreach (var n in names) {
                if (n.IndexOf("item", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("pickup", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("drop", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("hold", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("carry", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("inventory", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("take", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("put", StringComparison.OrdinalIgnoreCase) >= 0) {
                    items.Add(n);
                }
            }
            items.Sort();
            Console.WriteLine("=== ITEM ===");
            foreach (var n in items) Console.WriteLine("  " + n);
            
            List<string> fnames = new List<string>(fsmNames);
            fnames.Sort();
            Console.WriteLine();
            Console.WriteLine("=== FSM NAMES (item/clock/weather) ===");
            foreach (var n in fnames) {
                string l = n.ToLowerInvariant();
                if (l.IndexOf("item") >= 0 || l.IndexOf("clock") >= 0 || l.IndexOf("weather") >= 0 || l.IndexOf("time") >= 0 || l.IndexOf("rain") >= 0 || l.IndexOf("sun") >= 0 || l.IndexOf("pickup") >= 0) {
                    Console.WriteLine("  " + n);
                }
            }
            return 0;
        }
    }
}
