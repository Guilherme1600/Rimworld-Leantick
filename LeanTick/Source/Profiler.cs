using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Verse;

namespace LeanTick
{
    /// <summary>
    /// Medidor de tempo opcional. Só é instalado se estiver ligado nas opções (requer reinício do jogo).
    /// Mede tempo INCLUSIVO por método (um método que chama outros inclui o tempo deles).
    /// Todos os métodos abaixo foram confirmados no Assembly-CSharp.dll da 1.6.4543.
    /// </summary>
    public static class Profiler
    {
        private class Stat
        {
            public string name;
            public long ticks;
            public long calls;
        }

        public static bool Active;

        private static readonly Dictionary<MethodBase, Stat> stats = new Dictionary<MethodBase, Stat>();
        private static Stat tickStat;
        private static int gcStart;

        private static readonly string[][] targets = new string[][]
        {
            new[] { "Verse.TickManager", "DoSingleTick" },
            new[] { "Verse.TickList", "Tick" },
            new[] { "Verse.Pawn", "Tick" },
            new[] { "Verse.Pawn", "TickInterval" },
            new[] { "Verse.Pawn", "TickRare" },
            new[] { "Verse.AI.Pawn_JobTracker", "JobTrackerTick" },
            new[] { "Verse.AI.Pawn_JobTracker", "JobTrackerTickInterval" },
            new[] { "Verse.AI.Pawn_JobTracker", "DetermineNextConstantThinkTreeJob" },
            new[] { "Verse.AI.Pawn_JobTracker", "TryFindAndStartJob" },
            new[] { "RimWorld.JobGiver_Work", "TryIssueJobPackage" },
            new[] { "Verse.AI.Pawn_PathFollower", "PatherTick" },
            new[] { "Verse.PathFinder", "FindPathNow" },
            new[] { "Verse.Reachability", "CanReach" },
            new[] { "Verse.Reachability", "CanReachNonLocal" },
            new[] { "Verse.GenClosest", "ClosestThing_Global_Reachable" },
            new[] { "Verse.GenClosest", "ClosestThing_Global" },
            new[] { "Verse.GenClosest", "ClosestThingReachable" },
            new[] { "Verse.GenClosest", "RegionwiseBFSWorker" },
            new[] { "Verse.ListerThings", "ThingsMatching" },
            new[] { "Verse.AI.ReservationManager", "CanReserve" },
            new[] { "Verse.AI.ReservationManager", "Reserve" },
            new[] { "RimWorld.Pawn_NeedsTracker", "NeedsTrackerTickInterval" },
            new[] { "Verse.Pawn_HealthTracker", "HealthTick" },
            new[] { "Verse.Pawn_HealthTracker", "HealthTickInterval" },
            new[] { "Verse.AI.Pawn_MindState", "MindStateTickInterval" },
            new[] { "RimWorld.Pawn_RelationsTracker", "RelationsTrackerTickInterval" },
        };

        public static void Apply(Harmony harmony)
        {
            int patched = 0;
            foreach (string[] t in targets)
            {
                try
                {
                    Type type = AccessTools.TypeByName(t[0]);
                    if (type == null)
                    {
                        Log.Warning("[LeanTick] Medidor: tipo não encontrado: " + t[0]);
                        continue;
                    }

                    bool any = false;
                    foreach (MethodInfo m in AccessTools.GetDeclaredMethods(type))
                    {
                        if (m.Name != t[1] || m.IsAbstract || m.IsGenericMethodDefinition) continue;
                        harmony.Patch(m,
                            prefix: new HarmonyMethod(typeof(Profiler), nameof(Pre)),
                            postfix: new HarmonyMethod(typeof(Profiler), nameof(Post)));

                        var st = new Stat { name = type.Name + "." + m.Name + "(" + m.GetParameters().Length + ")" };
                        stats[m] = st;
                        if (t[0] == "Verse.TickManager" && t[1] == "DoSingleTick") tickStat = st;
                        any = true;
                        patched++;
                    }
                    if (!any) Log.Warning("[LeanTick] Medidor: método não encontrado: " + t[0] + "." + t[1]);
                }
                catch (Exception e)
                {
                    Log.Warning("[LeanTick] Medidor: falha em " + t[0] + "." + t[1] + ": " + e.Message);
                }
            }

            try
            {
                patched += PatchSubclasses(harmony, "Verse.AI.ThinkNode", "TryIssueJobPackage");
                patched += PatchSubclasses(harmony, "Verse.AI.ThinkNode_JobGiver", "TryGiveJob");
                patched += PatchSubclasses(harmony, "RimWorld.Need", "NeedInterval");
            }
            catch (Exception e)
            {
                Log.Warning("[LeanTick] Medidor: falha ao medir subclasses: " + e.Message);
            }

            Active = patched > 0;
            gcStart = GC.CollectionCount(0);
            Log.Message("[LeanTick] Medidor de tempo ativo (" + patched + " métodos).");
        }

        /// <summary>
        /// Mede o método indicado em todas as subclasses do jogo base (Assembly-CSharp) que o declaram.
        /// Serve para descobrir QUAL nó de decisão ou QUAL necessidade é lenta.
        /// </summary>
        private static int PatchSubclasses(Harmony harmony, string baseTypeName, string methodName)
        {
            Type baseType = AccessTools.TypeByName(baseTypeName);
            if (baseType == null)
            {
                Log.Warning("[LeanTick] Medidor: tipo base não encontrado: " + baseTypeName);
                return 0;
            }

            int count = 0;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "Assembly-CSharp") continue;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(x => x != null).ToArray(); }

                foreach (Type t in types)
                {
                    if (t.IsAbstract && t.IsSealed) continue;
                    if (t != baseType && !baseType.IsAssignableFrom(t)) continue;

                    foreach (MethodInfo m in AccessTools.GetDeclaredMethods(t))
                    {
                        if (m.Name != methodName || m.IsAbstract || m.IsGenericMethodDefinition) continue;
                        if (stats.ContainsKey(m)) continue;
                        try
                        {
                            harmony.Patch(m,
                                prefix: new HarmonyMethod(typeof(Profiler), nameof(Pre)),
                                postfix: new HarmonyMethod(typeof(Profiler), nameof(Post)));
                            stats[m] = new Stat { name = t.Name + "." + m.Name + "(" + m.GetParameters().Length + ")" };
                            count++;
                        }
                        catch (Exception)
                        {
                            // alguns métodos não podem ser medidos; ignorar
                        }
                    }
                }
            }
            return count;
        }

        public static void Pre(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        public static void Post(MethodBase __originalMethod, long __state)
        {
            Stat s;
            if (stats.TryGetValue(__originalMethod, out s))
            {
                s.ticks += Stopwatch.GetTimestamp() - __state;
                s.calls++;
            }
        }

        public static void Reset()
        {
            foreach (Stat s in stats.Values)
            {
                s.ticks = 0;
                s.calls = 0;
            }
            gcStart = GC.CollectionCount(0);
        }

        private static double Ms(long ticks)
        {
            return ticks * 1000.0 / Stopwatch.Frequency;
        }

        public static List<string> BuildLines(int max)
        {
            var lines = new List<string>();
            if (tickStat == null || tickStat.calls == 0)
            {
                lines.Add("Sem dados ainda. Deixa o jogo correr alguns segundos.");
                return lines;
            }

            long n = tickStat.calls;
            double msPerTick = Ms(tickStat.ticks) / n;
            lines.Add("Ticks medidos: " + n + " | média por tick: " + msPerTick.ToString("0.000")
                      + " ms | TPS máximo estimado: " + (msPerTick > 0 ? (1000.0 / msPerTick).ToString("0") : "-"));

            IEnumerable<Stat> rows = stats.Values
                .Where(s => s != tickStat && s.calls > 0)
                .OrderByDescending(s => s.ticks)
                .Take(max);

            foreach (Stat s in rows)
            {
                double perTick = Ms(s.ticks) / n;
                double pct = msPerTick > 0 ? 100.0 * perTick / msPerTick : 0;
                double usPerCall = Ms(s.ticks) * 1000.0 / s.calls;
                lines.Add(s.name + ": " + perTick.ToString("0.000") + " ms/tick (" + pct.ToString("0.0")
                          + "%), " + ((double)s.calls / n).ToString("0.0") + " chamadas/tick, "
                          + usPerCall.ToString("0.0") + " us/chamada");
            }

            int gc = GC.CollectionCount(0) - gcStart;
            lines.Add("GC (gen0) desde o reset: " + gc + " | memória gerida: "
                      + (GC.GetTotalMemory(false) / (1024 * 1024)) + " MB");
            lines.Add("Nota: valores inclusivos; um método que chama outros inclui o tempo deles, por isso as percentagens não somam 100%.");
            return lines;
        }

        public static void WriteLog()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[LeanTick] Relatório do medidor:");
            foreach (string l in BuildLines(60)) sb.AppendLine(l);
            Log.Message(sb.ToString());
        }
    }
}
