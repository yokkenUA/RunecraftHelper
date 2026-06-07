namespace RunecraftHelper
{
    using System;
    using GameHelper.Plugin;

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
    }
}
