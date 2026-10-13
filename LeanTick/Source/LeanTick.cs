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
        }
    }

    /// <summary>
    /// Cache negativa da pesquisa de trabalho.
    /// Se JobGiver_Work não encontrou nada para um peão, reutiliza "sem trabalho"
    /// durante N ticks (configurável) em vez de percorrer de novo todas as WorkGivers.
    /// Nunca guarda resultados positivos e ignora trabalho de emergência.
    /// </summary>
    public static class WorkSearchCache
    {
        private const int StaleLimit = 1000; // acima disto a entrada é considerada lixo (ex.: outro save)

        private static readonly Dictionary<int, int> nextSearchTick = new Dictionary<int, int>();
        private static readonly System.Random rng = new System.Random();
        private static FieldInfo emergencyField;
        private static bool broken;

        // Estatísticas (desde que o jogo arrancou)
        public static long Searched; // pesquisas completas feitas
        public static long Skipped;  // pesquisas evitadas pela cache

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

            Log.Message("[LeanTick] Cache de pesquisa de trabalho ativa.");
        }

        private static void Disable(Exception e)
        {
            broken = true;
            nextSearchTick.Clear();
            Log.Error("[LeanTick] Erro, otimização desativada automaticamente: " + e);
        }

        private static int NextDelay(LeanTickSettings s)
        {
            int ttl = s.ttl;
            if (s.jitter) ttl += rng.Next(0, ttl / 4 + 1); // espalha as pesquisas por ticks diferentes
            return ttl;
        }

        public static bool Prefix(object __instance, object[] __args, ref ThinkResult __result, out bool __state)
        {
            __state = false;
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
                    Searched++;
                    return true;
                }

                // Entrada inválida ou expirada
                if (next - now > StaleLimit || now >= next)
                {
                    nextSearchTick.Remove(pawn.thingIDNumber);
                    Searched++;
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
            try
            {
                var s = LeanTickMod.Settings;
                if (broken || __state || s == null || !s.enabled) return; // se foi saltado, não renovar a cache
                if (Current.ProgramState != ProgramState.Playing) return;

                Pawn pawn = __args[0] as Pawn;
                if (pawn == null) return;
                if ((bool)emergencyField.GetValue(__instance)) return;

                if (__result.Job == null)
                {
                    if (nextSearchTick.Count > 2000) nextSearchTick.Clear();
                    nextSearchTick[pawn.thingIDNumber] = Find.TickManager.TicksGame + NextDelay(s);
                }
                else
                {
                    nextSearchTick.Remove(pawn.thingIDNumber);
                }
            }
            catch (Exception e)
            {
                Disable(e);
            }
        }
    }
}
