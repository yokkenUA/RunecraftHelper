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

        // Show the per-monolith debug window: pick a nearby monolith and dump everything the offer
        // rule uses (anchor/p/N, sockets-vs-station N, area level, addresses, and the full offered
        // recipe list). Used to report game-vs-plugin recipe mismatches. Off by default.
        public bool ShowWindow = false;

        // ── Monolith reward window (Runeshape Encounter) ─────────────────────
        // Show a window listing, per nearby monolith, the candidate recipes (filtered by the
        // monolith's anchor rune + hole position) and their poe.ninja Exalted prices. The anchor is
        // read off the persistent Expedition2Encounter device, so it works out of the network bubble.
        public bool ShowMonolithRewards = false;

        // Hide candidate rewards whose unit Exalted price is below this (0 = show all, incl. unpriced).
        public float MonolithRewardsMinExalted = 0f;
    }
}
