using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace LeanTick
{
    [StaticConstructorOnStartup]
    public static class Startup
    {
        static Startup()
        {
            try
            {
                var harmony = new Harmony("leantick.optimizer");
                WorkSearchCache.Apply(harmony);
            }
            catch (Exception e)
            {
                Log.Error("[LeanTick] Falha ao iniciar, mod inativo: " + e);
            }

            try
            {
                if (LeanTickMod.Settings != null && LeanTickMod.Settings.profiler)
                {
                    Profiler.Apply(new Harmony("leantick.profiler"));
                }
            }
            catch (Exception e)
            {
                Log.Error("[LeanTick] Falha ao iniciar o medidor: " + e);
            }
        }
    }

    /// <summary>
    /// Duas caches negativas sobre JobGiver_Work (validado no IL da 1.6.4543):
    ///  1) Por colono: se TryIssueJobPackage não encontrou NADA, reutiliza "sem trabalho" durante N ticks.
    ///  2) Por colono + tipo de trabalho (WorkGiver): se um WorkGiver scanner foi examinado e falhou
    ///     antes de outro encontrar trabalho, esse WorkGiver é saltado durante N ticks para esse colono
    ///     (via PawnCanUseWorkGiver, que o jogo já trata como "saltar este tipo").
    /// Nunca guarda resultados positivos e ignora os nós de trabalho de emergência.
    /// </summary>
    public static class WorkSearchCache
    {
        private const int StaleLimit = 1000; // acima disto a entrada é considerada lixo (ex.: outro save)

        private static readonly Dictionary<int, int> nextSearchTick = new Dictionary<int, int>();
        private static readonly Dictionary<long, int> giverFail = new Dictionary<long, int>();
        private static readonly List<WorkGiverDef> evaluated = new List<WorkGiverDef>();
        private static readonly System.Random rng = new System.Random();
        private static FieldInfo emergencyField;
        private static bool broken;
        private static bool canUsePatched;
        private static bool inCall; // true só durante um TryIssueJobPackage normal (não emergência)

        // Estatísticas (desde que o jogo arrancou)
        public static long Searched;     // pesquisas completas feitas
        public static long Skipped;      // pesquisas evitadas (colono sem trabalho)
        public static long GiverSkipped; // pesquisas evitadas por tipo de trabalho

        public static void Apply(Harmony harmony)
        {
            Type type = AccessTools.TypeByName("RimWorld.JobGiver_Work")
                        ?? AccessTools.TypeByName("Verse.AI.JobGiver_Work");
            if (type == null)
            {
                Log.Warning("[LeanTick] JobGiver_Work não encontrado. Otimização desativada.");
                return;
            }

            MethodInfo target = AccessTools.Method(type, "TryIssueJobPackage",
                new Type[] { typeof(Pawn), typeof(JobIssueParams) });
            emergencyField = AccessTools.Field(type, "emergency");

            if (target == null || emergencyField == null)
            {
                Log.Warning("[LeanTick] Método ou campo esperado não encontrado. Otimização desativada.");
                return;
            }

            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(WorkSearchCache), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(WorkSearchCache), nameof(Postfix)));

            MethodInfo canUse = AccessTools.Method(type, "PawnCanUseWorkGiver",
                new Type[] { typeof(Pawn), typeof(WorkGiver) });
            if (canUse != null)
            {
                harmony.Patch(canUse,
                    prefix: new HarmonyMethod(typeof(WorkSearchCache), nameof(CanUsePrefix)),
                    postfix: new HarmonyMethod(typeof(WorkSearchCache), nameof(CanUsePostfix)));
                canUsePatched = true;
                Log.Message("[LeanTick] Cache por tipo de trabalho ativa.");
            }
            else
            {
                Log.Warning("[LeanTick] PawnCanUseWorkGiver não encontrado. Cache por tipo de trabalho desativada.");
            }

            Log.Message("[LeanTick] Cache de pesquisa de trabalho ativa.");
        }

        private static void Disable(Exception e)
        {
            broken = true;
            inCall = false;
            nextSearchTick.Clear();
            giverFail.Clear();
            Log.Error("[LeanTick] Erro, otimização desativada automaticamente: " + e);
        }

        private static int NextDelay(LeanTickSettings s)
        {
            int ttl = s.ttl;
            if (s.jitter) ttl += rng.Next(0, ttl / 4 + 1); // espalha as pesquisas por ticks diferentes
            return ttl;
        }

        private static long Key(Pawn p, WorkGiverDef d)
        {
            return ((long)p.thingIDNumber << 16) | d.shortHash;
        }

        private static void BeginFullSearch(LeanTickSettings s)
        {
            Searched++;
            evaluated.Clear();
            inCall = canUsePatched && s.perGiver;
        }

        // ---------- JobGiver_Work.TryIssueJobPackage ----------

        public static bool Prefix(object __instance, object[] __args, ref ThinkResult __result, out bool __state)
        {
            __state = false;
            inCall = false;
            try
            {
                var s = LeanTickMod.Settings;
                if (broken || s == null || !s.enabled || Current.ProgramState != ProgramState.Playing) return true;

                Pawn pawn = __args[0] as Pawn;
                if (pawn == null) return true;
                if ((bool)emergencyField.GetValue(__instance)) return true;

                int now = Find.TickManager.TicksGame;
                int next;
                if (!nextSearchTick.TryGetValue(pawn.thingIDNumber, out next))
                {
                    BeginFullSearch(s);
                    return true;
                }

                // Entrada inválida ou expirada
                if (next - now > StaleLimit || now >= next)
                {
                    nextSearchTick.Remove(pawn.thingIDNumber);
                    BeginFullSearch(s);
                    return true;
                }

                __result = ThinkResult.NoJob;
                __state = true; // saltámos o original
                Skipped++;
                return false;
            }
            catch (Exception e)
            {
                Disable(e);
                return true;
            }
        }

        public static void Postfix(object __instance, object[] __args, ThinkResult __result, bool __state)
        {
            bool learn = inCall;
            inCall = false;
            try
            {
                var s = LeanTickMod.Settings;
                if (broken || __state || s == null || !s.enabled) return; // se foi saltado, não renovar a cache
                if (Current.ProgramState != ProgramState.Playing) return;

                Pawn pawn = __args[0] as Pawn;
                if (pawn == null) return;
                if ((bool)emergencyField.GetValue(__instance)) return;

                int now = Find.TickManager.TicksGame;
                if (__result.Job == null)
                {
                    if (nextSearchTick.Count > 2000) nextSearchTick.Clear();
                    nextSearchTick[pawn.thingIDNumber] = now + NextDelay(s);
                }
                else
                {
                    nextSearchTick.Remove(pawn.thingIDNumber);

                    // Os WorkGivers examinados antes do último não encontraram nada para este colono.
                    if (learn && evaluated.Count > 1)
                    {
                        WorkGiverDef winner = __result.Job.workGiverDef;
                        if (winner == null || winner == evaluated[evaluated.Count - 1])
                        {
                            if (giverFail.Count > 20000) giverFail.Clear();
                            for (int i = 0; i < evaluated.Count - 1; i++)
                            {
                                giverFail[Key(pawn, evaluated[i])] = now + NextDelay(s);
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Disable(e);
            }
        }

        // ---------- JobGiver_Work.PawnCanUseWorkGiver (instância, privado) ----------

        public static bool CanUsePrefix(object[] __args, ref bool __result, out bool __state)
        {
            __state = false;
            try
            {
                if (!inCall || broken) return true;
                var s = LeanTickMod.Settings;
                if (s == null || !s.enabled || !s.perGiver) return true;

                Pawn pawn = __args[0] as Pawn;
                WorkGiver giver = __args[1] as WorkGiver;
                if (pawn == null || giver == null || giver.def == null) return true;
                if (!(giver is WorkGiver_Scanner)) return true; // só os scanners são caros

                long key = Key(pawn, giver.def);
                int exp;
                if (!giverFail.TryGetValue(key, out exp)) return true;

                int now = Find.TickManager.TicksGame;
                if (exp - now > StaleLimit || now >= exp)
                {
                    giverFail.Remove(key);
                    return true;
                }

                __result = false; // o jogo trata isto como "colono não pode usar este tipo de trabalho agora"
                __state = true;
                GiverSkipped++;
                return false;
            }
            catch (Exception e)
            {
                Disable(e);
                return true;
            }
        }

        public static void CanUsePostfix(object[] __args, bool __result, bool __state)
        {
            try
            {
                if (!inCall || broken || __state || !__result) return;
                WorkGiver giver = __args[1] as WorkGiver;
                if (giver == null || giver.def == null) return;
                if (!(giver is WorkGiver_Scanner)) return;
                evaluated.Add(giver.def); // este tipo vai ser realmente pesquisado
            }
            catch (Exception e)
            {
                Disable(e);
            }
        }
    }
}
