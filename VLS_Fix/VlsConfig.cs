using System.Collections.Generic;
using System.Xml.Serialization;

namespace VLS_Fix
{
    public class VlsConfig
    {
        public bool Enabled = true;

        [XmlArrayItem("Subtype")]
        public List<string> VLSSubtypes = new List<string>();

        [XmlArrayItem("Subtype")]
        public List<string> HomingSubtypes = new List<string>();
        public float HomingBoostSeconds = 2f;
        public float HomingTurnRateMultiplier = 4f;
        public float HomingSpeedFactor = 0.5f;

        public float ExitSeconds = 0.3f;
        public float MaxTurnSeconds = 6f;
        public float RampSeconds = 0.7f;
        public float SmokeBurstSeconds = 0.2f;
        public float NoTurnAngleDeg = 2f;

        public float ReferenceTurnRateDeg = 14f;
        public float AgilityExponent = 1.5f;

        public float MaxAngularAccelDeg = 80f;
        public float BrakeMargin = 1f;
        public float SpeedGain = 20f;
        public float AimGain = 4f;
        public float SettleAngleDeg = 0.5f;
        public float SettleRateDeg = 2f;

        public float DriftAccel = 30f;
        public float LateralAccel = 20f;
        public float SlipSettleDeg = 1f;

        public float JetRollDeg = 45f;
        public float JetResponse = 0.4f;
        public float ManeuverSmokeLevel = 0.3f;
        public float ManeuverSmokeAccelFactor = 1f;
        public float JetSmokeLevel = 1f;
        public float JetSmokeSpeed = 5f;
    }

    /// <summary>
    /// One config file (a mod's Data/VLS.xml, or the world's Storage VLS.xml): only the elements it contains are
    /// set, so layers can be applied on top of each other. An empty or missing subtype list leaves the list as is.
    /// Mod layers merge their subtypes into the lists (a subtype listed for one profile leaves the other); the world
    /// layer replaces the lists it sets.
    /// </summary>
    [XmlRoot("VlsConfig")]
    public class VlsConfigLayer
    {
        public bool? Enabled;

        [XmlArrayItem("Subtype")]
        public List<string> VLSSubtypes;

        [XmlArrayItem("Subtype")]
        public List<string> HomingSubtypes;
        public float? HomingBoostSeconds;
        public float? HomingTurnRateMultiplier;
        public float? HomingSpeedFactor;

        public float? ExitSeconds;
        public float? MaxTurnSeconds;
        public float? RampSeconds;
        public float? SmokeBurstSeconds;
        public float? NoTurnAngleDeg;

        public float? ReferenceTurnRateDeg;
        public float? AgilityExponent;

        public float? MaxAngularAccelDeg;
        public float? BrakeMargin;
        public float? SpeedGain;
        public float? AimGain;
        public float? SettleAngleDeg;
        public float? SettleRateDeg;

        public float? DriftAccel;
        public float? LateralAccel;
        public float? SlipSettleDeg;

        public float? JetRollDeg;
        public float? JetResponse;
        public float? ManeuverSmokeLevel;
        public float? ManeuverSmokeAccelFactor;
        public float? JetSmokeLevel;
        public float? JetSmokeSpeed;

        public void ApplyTo(VlsConfig c, bool mergeLists)
        {
            if (Enabled.HasValue) c.Enabled = Enabled.Value;

            if (mergeLists)
            {
                MergeSubtypes(VLSSubtypes, c.VLSSubtypes, c.HomingSubtypes);
                MergeSubtypes(HomingSubtypes, c.HomingSubtypes, c.VLSSubtypes);
            }
            else
            {
                if (VLSSubtypes != null && VLSSubtypes.Count > 0) c.VLSSubtypes = new List<string>(VLSSubtypes);
                if (HomingSubtypes != null && HomingSubtypes.Count > 0) c.HomingSubtypes = new List<string>(HomingSubtypes);
            }
            if (HomingBoostSeconds.HasValue) c.HomingBoostSeconds = HomingBoostSeconds.Value;
            if (HomingTurnRateMultiplier.HasValue) c.HomingTurnRateMultiplier = HomingTurnRateMultiplier.Value;
            if (HomingSpeedFactor.HasValue) c.HomingSpeedFactor = HomingSpeedFactor.Value;

            if (ExitSeconds.HasValue) c.ExitSeconds = ExitSeconds.Value;
            if (MaxTurnSeconds.HasValue) c.MaxTurnSeconds = MaxTurnSeconds.Value;
            if (RampSeconds.HasValue) c.RampSeconds = RampSeconds.Value;
            if (SmokeBurstSeconds.HasValue) c.SmokeBurstSeconds = SmokeBurstSeconds.Value;
            if (NoTurnAngleDeg.HasValue) c.NoTurnAngleDeg = NoTurnAngleDeg.Value;

            if (ReferenceTurnRateDeg.HasValue) c.ReferenceTurnRateDeg = ReferenceTurnRateDeg.Value;
            if (AgilityExponent.HasValue) c.AgilityExponent = AgilityExponent.Value;

            if (MaxAngularAccelDeg.HasValue) c.MaxAngularAccelDeg = MaxAngularAccelDeg.Value;
            if (BrakeMargin.HasValue) c.BrakeMargin = BrakeMargin.Value;
            if (SpeedGain.HasValue) c.SpeedGain = SpeedGain.Value;
            if (AimGain.HasValue) c.AimGain = AimGain.Value;
            if (SettleAngleDeg.HasValue) c.SettleAngleDeg = SettleAngleDeg.Value;
            if (SettleRateDeg.HasValue) c.SettleRateDeg = SettleRateDeg.Value;

            if (DriftAccel.HasValue) c.DriftAccel = DriftAccel.Value;
            if (LateralAccel.HasValue) c.LateralAccel = LateralAccel.Value;
            if (SlipSettleDeg.HasValue) c.SlipSettleDeg = SlipSettleDeg.Value;

            if (JetRollDeg.HasValue) c.JetRollDeg = JetRollDeg.Value;
            if (JetResponse.HasValue) c.JetResponse = JetResponse.Value;
            if (ManeuverSmokeLevel.HasValue) c.ManeuverSmokeLevel = ManeuverSmokeLevel.Value;
            if (ManeuverSmokeAccelFactor.HasValue) c.ManeuverSmokeAccelFactor = ManeuverSmokeAccelFactor.Value;
            if (JetSmokeLevel.HasValue) c.JetSmokeLevel = JetSmokeLevel.Value;
            if (JetSmokeSpeed.HasValue) c.JetSmokeSpeed = JetSmokeSpeed.Value;
        }

        static void MergeSubtypes(List<string> from, List<string> into, List<string> other)
        {
            if (from == null)
                return;
            foreach (var subtype in from)
            {
                if (string.IsNullOrWhiteSpace(subtype))
                    continue;
                var id = subtype.Trim();
                other.Remove(id);
                if (!into.Contains(id))
                    into.Add(id);
            }
        }
    }
}
