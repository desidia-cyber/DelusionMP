using DelusionMP.Core;
using DelusionMP.Game;
using DelusionMP.Net;

namespace DelusionMP.Sync
{
    /// <summary>
    /// Session rules ([Rules] in the mod config, edited on the launcher's Rules page) are the
    /// host's: the host sends them on every join and the client uses them for the session.
    /// The client's own values come back when it plays alone (next start).
    /// </summary>
    public static class RulesSync
    {
        public static void Send(Session session)
        {
            if (session == null || session.Role != SessionRole.Host) return;
            var w = new Writer();
            w.U8(1); // version
            w.Bool(WorldEventsSync.ExclusiveUniqueBuys);
            w.Bool(CoopDeath.Enabled);
            w.I32(CoopDeath.MaxStacks);
            w.F32(CoopDeath.PctPerStack);
            w.F32(CoopDeath.RespawnDelay);
            w.Bool(CoopDeath.RespawnAtPartner);
            session.SendReliable(PacketKind.Rules, w.ToArray());
        }

        public static void Apply(byte[] payload)
        {
            if (Session.Instance?.Role != SessionRole.Client) return;
            var r = new Reader(payload);
            if (r.U8() != 1) return;
            WorldEventsSync.ExclusiveUniqueBuys = r.Bool();
            CoopDeath.Enabled = r.Bool();
            CoopDeath.MaxStacks = r.I32();
            CoopDeath.PctPerStack = r.F32();
            CoopDeath.RespawnDelay = r.F32();
            CoopDeath.RespawnAtPartner = r.Bool();
            Log.Info($"rules from the host: {Describe()}");
        }

        public static string Describe() =>
            $"exclusiveShop={WorldEventsSync.ExclusiveUniqueBuys} deathDebuffs={CoopDeath.Enabled} maxStacks={CoopDeath.MaxStacks} " +
            $"pctPerStack={CoopDeath.PctPerStack} respawnDelay={CoopDeath.RespawnDelay} respawnAtPartner={CoopDeath.RespawnAtPartner}";
    }
}
