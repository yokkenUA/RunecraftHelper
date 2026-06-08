namespace RunecraftHelper
{
    using System;
    using GameHelper.Plugin;

    // How the per-row price text is tinted to signal reward value at a glance.
    //   Off       — single neutral colour.
    //   Relative  — green/yellow/red vs. the median of the rows currently on screen.
    //   Absolute  — green/yellow/red vs. fixed Exalted thresholds.
    public enum RewardColorMode
    {
        Off = 0,
        Relative = 1,
        Absolute = 2,
    }

    public sealed class RunecraftHelperSettings : IPSettings
    {
        // poe.ninja PoE2 league slug as it appears in the API "league" parameter (spaces become '+').
        // Update each league launch. Default is the current league as of 2026-06-06.
        public string League = "Runes of Aldur";

        // How long cached prices stay valid before we re-fetch (minutes). Range enforced in the
        // UI slider (5–60).
        public int CacheTtlMinutes = 60;

        // Last successful sync timestamp (UTC). Zero means "never fetched yet".
        public DateTime LastSyncUtc = DateTime.MinValue;

        // Colour-coding of the overlay price text (see RewardColorMode).
        public RewardColorMode ColorMode = RewardColorMode.Relative;

        // Horizontal nudge (screen px) applied to the price text — lets the user slide it left/right
        // to clear long reward names or sit it wherever reads best. Negative = left, positive = right.
        public float OverlayXOffset = 0f;

        // Show the (debug) list window alongside the overlay: count / resolved metaId / price / game
        // name per visible row — handy to see which reward name failed to resolve to a price.
        public bool ShowWindow = true;
    }
}
