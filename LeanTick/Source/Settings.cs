using UnityEngine;
using Verse;

namespace LeanTick
{
    public class LeanTickSettings : ModSettings
    {
        public bool enabled = true;
        public int ttl = 60;
        public bool jitter = true;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref enabled, "enabled", true);
            Scribe_Values.Look(ref ttl, "ttl", 60);
            Scribe_Values.Look(ref jitter, "jitter", true);
            ttl = Mathf.Clamp(ttl, 10, 600);
        }
    }

    public class LeanTickMod : Mod
    {
        public static LeanTickSettings Settings;

        public LeanTickMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<LeanTickSettings>();
        }

        public override string SettingsCategory()
        {
            return "LeanTick";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var s = Settings;
            var l = new Listing_Standard();
            l.Begin(inRect);

            l.CheckboxLabeled("Ativar cache de pesquisa de trabalho", ref s.enabled,
                "Desligar devolve o comportamento original do jogo.");
            l.Gap();

            l.Label("Intensidade (quanto tempo um colono sem trabalho espera até procurar de novo):");
            if (l.ButtonText("Seguro: 20 ticks (0,3 s)")) s.ttl = 20;
            if (l.ButtonText("Equilibrado: 60 ticks (1 s)")) s.ttl = 60;
            if (l.ButtonText("Agressivo: 150 ticks (2,5 s)")) s.ttl = 150;
            if (l.ButtonText("Extremo: 300 ticks (5 s)")) s.ttl = 300;
            l.Gap(6f);

            l.Label("Valor atual: " + s.ttl + " ticks (" + (s.ttl / 60f).ToString("0.0") + " s de jogo)");
            s.ttl = Mathf.RoundToInt(l.Slider(s.ttl, 10f, 600f));
            l.Gap();

            l.CheckboxLabeled("Espalhar pesquisas no tempo (variação de até +25%)", ref s.jitter,
                "Evita que muitos colonos voltem a pesquisar exatamente no mesmo tick.");
            l.Gap();

            l.Label("Valores altos poupam mais CPU, mas um colono sem trabalho demora mais a reparar em trabalho novo. Trabalho de emergência nunca é atrasado.");
            l.Gap();

            long total = WorkSearchCache.Searched + WorkSearchCache.Skipped;
            string pct = total > 0 ? (100f * WorkSearchCache.Skipped / total).ToString("0.0") + "%" : "-";
            l.Label("Estatísticas desde que o jogo arrancou: pesquisas feitas " + WorkSearchCache.Searched
                    + ", evitadas " + WorkSearchCache.Skipped + " (" + pct + ")");

            l.End();
        }
    }
}
