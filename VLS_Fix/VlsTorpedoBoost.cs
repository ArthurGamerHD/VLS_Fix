using System;
using System.Collections.Generic;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Utils;
using VRageMath;

namespace VLS_Fix
{
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation | MyUpdateOrder.AfterSimulation)]
    public class VlsTorpedoBoost : MySessionComponentBase
    {
        const string CONFIG_FILE = "Data/VLS.xml";
        const string EXAMPLE_FILE = "Data/VLS.example.xml";
        const string USER_FILE = "VLS.xml";
        const string CONFIG_VARIABLE = "VLS_Fix.Config";
        static readonly string[] DefaultVLSSubtypes = { "Torpedo", "Torpedo_Heavy", "Torpedo_Seeking", "Torpedo_Piercing" };
        static readonly string[] DefaultHomingSubtypes = { "MissileHoming" };

        const ushort NET_ID = 39217;
        const float DT = 1f / 60f;

        // The vanilla Smoke_Torpedo trail carries four side flames (Flames_L/R/U/D), But they pause too when the main plume is paused
        static readonly string[] JetEffects = { "VLS_TurnJet_L", "VLS_TurnJet_R", "VLS_TurnJet_U", "VLS_TurnJet_D" };

        // Emission direction of each flame in effect-local space (X right, Y up, Z back), from its SBC.
        static readonly Vector3D[] JetDirections = { new Vector3D(0, 1, 0), new Vector3D(0, -1, 0), new Vector3D(1, 0, 0), new Vector3D(-1, 0, 0) };

        // Trail_Smoke of the vanilla trail alone, run at reduced emission while the vanilla trail is paused.
        const string SMOKE_EFFECT = "VLS_TorpedoSmoke";
        
        static readonly string[] JetSmokeEffects = { "VLS_JetSmoke_L", "VLS_JetSmoke_R", "VLS_JetSmoke_U", "VLS_JetSmoke_D" };

        class AmmoInfo
        {
            public MyMissileAmmoDefinition Definition;
            public float BaseTurnRateDeg;
            public double AngularAccelMax;
            public double AimGain;
            public float DriftAccel;
            public float LateralAccel;
            public bool Homing;
        }

        class Tracked
        {
            public IMyMissile Missile;
            public AmmoInfo Ammo;
            public float Elapsed;
            public Vector3D PrevForward;
            public Vector3D Commanded;
            public Vector3D Up;
            public float LaunchSpeed;
            public Vector3D Position;
            public Vector3D VelocityDir;
            public VRage.ModAPI.IMyEntity Target;
            public Vector3D AngularVelocity;
            public Vector3D AngularAccel;
            public Vector3D LateralAccelVec;
            public float TurnSeconds;
            public bool TurnPlanned;
            public bool RotationSettled;
            public bool HandedOff;

            public bool TrailHidden;
            public MyParticleEffect[] Jets;
            public MyParticleEffect[] JetSmoke;
            public MyParticleEffect Smoke;
            public float[] JetLevels;
        }

        VlsConfig _cfg;
        MatrixD _jetLocalMatrix;
        readonly Dictionary<MyMissileAmmoDefinition, AmmoInfo> _ammo = new Dictionary<MyMissileAmmoDefinition, AmmoInfo>();
        readonly Dictionary<long, Tracked> _tracked = new Dictionary<long, Tracked>();
        readonly List<Tracked> _iter = new List<Tracked>();
        readonly Dictionary<long, long> _pendingTargets = new Dictionary<long, long>();
        bool _active;

        bool IsServer => MyAPIGateway.Multiplayer.IsServer;

        public override void LoadData()
        {
            _cfg = LoadConfig();
            if (!_cfg.Enabled)
                return;
            _active = true;
            _jetLocalMatrix = MatrixD.CreateRotationZ(MathHelper.ToRadians(_cfg.JetRollDeg));

            foreach (var subtype in _cfg.VLSSubtypes)
            {
                var def = GetMissileAmmo(subtype);
                if (def == null || !def.CanSeek || _ammo.ContainsKey(def))
                    continue;

                float agility = Math.Max(def.MaxTurnRateDegreesPerSecond, 0.1f) / Math.Max(_cfg.ReferenceTurnRateDeg, 0.1f);
                double rot = Math.Pow(agility, _cfg.AgilityExponent);
                _ammo[def] = new AmmoInfo
                {
                    Definition = def,
                    BaseTurnRateDeg = def.MaxTurnRateDegreesPerSecond,
                    AngularAccelMax = MathHelper.ToRadians(_cfg.MaxAngularAccelDeg) * rot,
                    AimGain = _cfg.AimGain * Math.Sqrt(rot),
                    DriftAccel = _cfg.DriftAccel * agility,
                    LateralAccel = _cfg.LateralAccel * agility,
                };
            }

            // Homing profile: no tube exit or jets, just a tight, slow start that fades into the vanilla seeker.
            foreach (var subtype in _cfg.HomingSubtypes)
            {
                var def = GetMissileAmmo(subtype);
                if (def == null || !def.CanSeek || _ammo.ContainsKey(def))
                    continue;
                _ammo[def] = new AmmoInfo { Definition = def, BaseTurnRateDeg = def.MaxTurnRateDegreesPerSecond, Homing = true };
            }

            MyAPIGateway.Missiles.OnMissileAdded += OnMissileAdded;
            MyAPIGateway.Missiles.OnMissileRemoved += OnMissileRemoved;
            MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(NET_ID, OnPacket);
        }

        protected override void UnloadData()
        {
            if (!_active)
                return;
            _active = false;

            if (MyAPIGateway.Missiles != null)
            {
                MyAPIGateway.Missiles.OnMissileAdded -= OnMissileAdded;
                MyAPIGateway.Missiles.OnMissileRemoved -= OnMissileRemoved;
            }
            if (MyAPIGateway.Multiplayer != null)
                MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(NET_ID, OnPacket);

            _ammo.Clear();
            _tracked.Clear();
            _pendingTargets.Clear();
        }

        static MyMissileAmmoDefinition GetMissileAmmo(string subtype)
        {
            var id = new MyDefinitionId(typeof(MyObjectBuilder_AmmoDefinition), subtype);
            try { return MyDefinitionManager.Static.GetAmmoDefinition(id) as MyMissileAmmoDefinition; }
            catch (Exception) { return null; } // GetAmmoDefinition throws on unknown ids
        }

        /// <summary>
        /// Server: this mod's Data/VLS.xml, then the Data/VLS.xml of every other loaded mod in load order (adding
        /// their torpedo types, overriding values), then the world's Storage VLS.xml on top; the result is stored as a world
        /// variable, which travels to every joining client with the world. Clients use only that variable, so all
        /// machines fly the same path.
        /// </summary>
        VlsConfig LoadConfig()
        {
            VlsConfig cfg = null;
            if (IsServer)
            {
                cfg = new VlsConfig();
                ApplyLayer(cfg, ReadModFile(CONFIG_FILE, ModContext.ModItem), CONFIG_FILE, true);
                ApplyOtherModConfigs(cfg);
                ApplyLayer(cfg, ReadWorldStorageConfig(), "world storage " + USER_FILE, false);
                FillDefaultSubtypes(cfg);
                MyAPIGateway.Utilities.SetVariable(CONFIG_VARIABLE, MyAPIGateway.Utilities.SerializeToXML(cfg));
                return cfg;
            }

            string xml;
            if (MyAPIGateway.Utilities.GetVariable(CONFIG_VARIABLE, out xml) && !string.IsNullOrEmpty(xml))
            {
                try { cfg = MyAPIGateway.Utilities.SerializeFromXML<VlsConfig>(xml); }
                catch (Exception e) { MyLog.Default.WriteLineAndConsole("VLS_Fix: could not read the server's config: " + e.Message); }
            }
            if (cfg == null)
            {
                MyLog.Default.WriteLineAndConsole("VLS_Fix: no config from the server, using " + CONFIG_FILE + ".");
                cfg = new VlsConfig();
                ApplyLayer(cfg, ReadModFile(CONFIG_FILE, ModContext.ModItem), CONFIG_FILE, true);
            }
            FillDefaultSubtypes(cfg);
            return cfg;
        }

        static void FillDefaultSubtypes(VlsConfig cfg)
        {
            if (cfg.VLSSubtypes == null || cfg.VLSSubtypes.Count == 0)
                cfg.VLSSubtypes = new List<string>(DefaultVLSSubtypes);
            if (cfg.HomingSubtypes == null || cfg.HomingSubtypes.Count == 0)
                cfg.HomingSubtypes = new List<string>(DefaultHomingSubtypes);
        }

        /// <summary>Other mods ship a Data/VLS.xml to register their own torpedoes or tune the launch for them.</summary>
        void ApplyOtherModConfigs(VlsConfig cfg)
        {
            var mods = MyAPIGateway.Session.Mods;
            if (mods == null)
                return;
            var self = ModContext.ModItem;
            foreach (var mod in mods)
            {
                if (mod.Name == self.Name && mod.PublishedFileId == self.PublishedFileId)
                    continue;
                string xml = null;
                try
                {
                    if (MyAPIGateway.Utilities.FileExistsInModLocation(CONFIG_FILE, mod)) // (I know this is unreliable on Linux Compat)
                        xml = ReadModFile(CONFIG_FILE, mod);
                }
                catch (Exception)
                {
                    // a mod whose folder is missing or unreadable simply has no config
                } 
                if (xml == null)
                    continue;
                var source = CONFIG_FILE + " of mod " + mod.FriendlyName + " (" + mod.Name + ")";
                MyLog.Default.WriteLineAndConsole("VLS_Fix: merging " + source);
                ApplyLayer(cfg, xml, source, true);
            }
        }

        static void ApplyLayer(VlsConfig cfg, string xml, string source, bool mergeLists)
        {
            if (string.IsNullOrWhiteSpace(xml))
                return;
            try { MyAPIGateway.Utilities.SerializeFromXML<VlsConfigLayer>(xml).ApplyTo(cfg, mergeLists); }
            catch (Exception e) { MyLog.Default.WriteLineAndConsole("VLS_Fix: could not read " + source + ", ignoring it: " + e.Message); }
        }

        static string ReadModFile(string path, MyObjectBuilder_Checkpoint.ModItem mod)
        {
            try
            {
                if (MyAPIGateway.Utilities.FileExistsInModLocation(path, mod))
                    using (var reader = MyAPIGateway.Utilities.ReadFileInModLocation(path, mod))
                        return reader.ReadToEnd();
                MyLog.Default.WriteLineAndConsole("VLS_Fix: " + path + " not found in the mod folder.");
            }
            catch (Exception e)
            {
                MyLog.Default.WriteLineAndConsole("VLS_Fix: could not read " + path + ": " + e.Message);
            }
            return null;
        }

        /// <summary>The world's override file; when there is none, the commented-out example is written in its place.</summary>
        string ReadWorldStorageConfig()
        {
            var type = typeof(VlsTorpedoBoost);
            try
            {
                if (MyAPIGateway.Utilities.FileExistsInWorldStorage(USER_FILE, type))
                    using (var reader = MyAPIGateway.Utilities.ReadFileInWorldStorage(USER_FILE, type))
                        return reader.ReadToEnd();

                var example = ReadModFile(EXAMPLE_FILE, ModContext.ModItem);
                if (example != null)
                    using (var writer = MyAPIGateway.Utilities.WriteFileInWorldStorage(USER_FILE, type))
                        writer.Write(example);
            }
            catch (Exception e)
            {
                MyLog.Default.WriteLineAndConsole("VLS_Fix: could not access world storage " + USER_FILE + ": " + e.Message);
            }
            return null;
        }

        void OnMissileAdded(IMyMissile missile)
        {
            var def = missile.AmmoDefinition as MyMissileAmmoDefinition;
            AmmoInfo info;
            if (def == null || !_ammo.TryGetValue(def, out info))
                return;

            // Missiles are pooled: the same entity is re-added on every launch, so this resets its state.
            var matrix = missile.WorldMatrix;
            var t = new Tracked
            {
                Missile = missile,
                Ammo = info,
                PrevForward = matrix.Forward,
                Commanded = matrix.Forward,
                Up = matrix.Up,
                LaunchSpeed = missile.LinearVelocity.Length(),
                Position = matrix.Translation,
                VelocityDir = missile.LinearVelocity.LengthSquared() > 1e-6f ? Vector3D.Normalize(missile.LinearVelocity) : matrix.Forward,
                TurnSeconds = _cfg.MaxTurnSeconds,
            };
            _tracked[missile.EntityId] = t;

            if (IsServer)
            {
                long targetId = GetLaunchTargetId(missile);
                t.Target = FindEntity(targetId);
                if (targetId != 0 && MyAPIGateway.Multiplayer.MultiplayerActive)
                    Send(new VlsPacket { Kind = VlsPacket.LAUNCH, MissileId = missile.EntityId, TargetId = targetId });
            }
            else
            {
                long targetId;
                if (_pendingTargets.TryGetValue(missile.EntityId, out targetId))
                {
                    _pendingTargets.Remove(missile.EntityId);
                    t.Target = FindEntity(targetId);
                }
            }

            UpdateVisuals(t);
        }

        void OnMissileRemoved(IMyMissile missile)
        {
            Tracked t;
            if (!_tracked.TryGetValue(missile.EntityId, out t) || t.Missile != missile)
                return;
            StopJets(t); // the game stops its own trail on removal
            _tracked.Remove(missile.EntityId);
        }

        // Not exposed on IMyMissile, but MyMissile.GetObjectBuilder copies m_targetEntityId. Server only: it also
        // reads Physics.LinearVelocity, and client missiles have no physics.
        static long GetLaunchTargetId(IMyMissile missile)
        {
            var ob = missile.GetObjectBuilder() as MyObjectBuilder_Missile;
            return ob == null ? 0 : ob.TargetEntityId;
        }

        static VRage.ModAPI.IMyEntity FindEntity(long id)
        {
            VRage.ModAPI.IMyEntity entity;
            return id != 0 && MyAPIGateway.Entities.TryGetEntityById(id, out entity) ? entity : null;
        }

        static void Send(VlsPacket packet)
        {
            MyAPIGateway.Multiplayer.SendMessageToOthers(NET_ID, MyAPIGateway.Utilities.SerializeToBinary(packet));
        }

        void SendHandOff(Tracked t)
        {
            if (!MyAPIGateway.Multiplayer.MultiplayerActive)
                return;
            Send(new VlsPacket
            {
                Kind = VlsPacket.HAND_OFF,
                MissileId = t.Missile.EntityId,
                Elapsed = t.Elapsed,
                TurnSeconds = t.TurnSeconds,
                Position = t.Missile.GetPosition(),
                Forward = t.PrevForward,
                Velocity = t.Missile.LinearVelocity,
            });
        }

        void OnPacket(ushort id, byte[] data, ulong sender, bool fromServer)
        {
            if (!fromServer || IsServer)
                return;

            VlsPacket p;
            try { p = MyAPIGateway.Utilities.SerializeFromBinary<VlsPacket>(data); }
            catch (Exception) { return; }
            if (p == null)
                return;

            Tracked t;
            bool known = _tracked.TryGetValue(p.MissileId, out t) && !t.Missile.MarkedForClose && !t.Missile.Closed;

            if (p.Kind == VlsPacket.LAUNCH)
            {
                if (known)
                    t.Target = FindEntity(p.TargetId);
                else
                {
                    // The launcher's spawn event and this message travel separately; keep it until the missile exists.
                    if (_pendingTargets.Count > 256)
                        _pendingTargets.Clear();
                    _pendingTargets[p.MissileId] = p.TargetId;
                }
            }
            else if (p.Kind == VlsPacket.HAND_OFF && known)
                ApplyHandOff(t, p);
        }

        // Client: aligns with the server's hand-off state. Normally both flew the same path and this changes nothing
        // visible; it matters when the client lacked the target (not streamed in) or the packet arrived late.
        void ApplyHandOff(Tracked t, VlsPacket p)
        {
            var missile = t.Missile;
            float lead = Math.Max(0f, t.Elapsed - p.Elapsed);
            Vector3D position = p.Position + (Vector3D)p.Velocity * lead;

            if (t.HandedOff)
            {
                // The vanilla trajectory sync may already have turned it further; only fix the position.
                var m = missile.WorldMatrix;
                m.Translation = position;
                missile.SetWorldMatrix(m);
                return;
            }

            t.Elapsed = Math.Max(t.Elapsed, p.Elapsed);
            t.TurnSeconds = p.TurnSeconds;
            t.TurnPlanned = true;
            t.HandedOff = true;
            t.PrevForward = p.Forward;
            t.Commanded = p.Forward;
            t.Position = position;
            t.AngularVelocity = Vector3D.Zero;
            t.AngularAccel = Vector3D.Zero;
            t.LateralAccelVec = Vector3D.Zero;
            missile.SetWorldMatrix(OrientedMatrix(position, p.Forward, ref t.Up));
            SetVelocity(missile, p.Velocity);
            UpdateVisuals(t);
        }

        float TurnEnd(Tracked t) { return _cfg.ExitSeconds + t.TurnSeconds; }

        bool InScriptedPhase(Tracked t)
        {
            float end = t.Ammo.Homing ? _cfg.HomingBoostSeconds : TurnEnd(t);
            return !t.HandedOff && t.Elapsed < end;
        }

        float Ramp(Tracked t)
        {
            if (t.Ammo.Homing)
                return 1f; // speed is handled in FlyHoming
            float end = TurnEnd(t);
            if (t.Elapsed < end)
                return 0f;
            return _cfg.RampSeconds <= 0f ? 1f : MathHelper.Clamp((t.Elapsed - end) / _cfg.RampSeconds, 0f, 1f);
        }

        bool TrailOn(Tracked t)
        {
            return t.Elapsed < Math.Min(_cfg.SmokeBurstSeconds, _cfg.ExitSeconds) || t.HandedOff || t.Elapsed >= TurnEnd(t);
        }

        bool JetsOn(Tracked t)
        {
            return !TrailOn(t);
        }
        
        static Vector3D AimDirection(Tracked t, Vector3D nose)
        {
            var target = t.Target;
            if (target == null || target.MarkedForClose || target.Closed)
                return nose;

            Vector3D aim = target.WorldAABB.Center;
            var top = target.GetTopMostParent();
            if (top != null && top.Physics != null)
            {
                double speed = Math.Max(t.Ammo.Definition.DesiredSpeed, 1f);
                aim += (Vector3D)top.Physics.LinearVelocity * (Vector3D.Distance(aim, t.Position) / speed);
            }

            Vector3D dir = aim - t.Position;
            double lenSq = dir.LengthSquared();
            return lenSq > 1e-6 ? dir / Math.Sqrt(lenSq) : nose;
        }

        static MatrixD OrientedMatrix(Vector3D position, Vector3D forward, ref Vector3D up)
        {
            Vector3D u = up - forward * Vector3D.Dot(up, forward);
            if (u.LengthSquared() < 1e-6)
                u = Vector3D.CalculatePerpendicularVector(forward);
            u.Normalize();
            up = u;
            return MatrixD.CreateWorld(position, forward, u);
        }

        void PlanTurn(Tracked t)
        {
            if (t.TurnPlanned || t.Elapsed < _cfg.ExitSeconds)
                return;
            t.TurnPlanned = true;
            Vector3D nose = t.PrevForward;
            double angle = Math.Acos(MathHelper.Clamp(Vector3D.Dot(nose, AimDirection(t, nose)), -1.0, 1.0));
            if (MathHelper.ToDegrees(angle) < _cfg.NoTurnAngleDeg)
                t.TurnSeconds = 0f;
        }

        // One frame of the scripted path; overrides whatever the seeker and physics did this frame. The nose is held
        // during the exit and jet-turned after it. Speed is what the game's acceleration produced (after the cap in
        // UpdateBeforeSimulation); the direction of travel lags the nose and is pulled onto it at DriftAccel.
        void FlyScripted(Tracked t)
        {
            Vector3D nose = t.PrevForward;
            if (t.Elapsed >= _cfg.ExitSeconds)
                nose = FlyTurn(t, nose);

            float speed = t.Missile.LinearVelocity.Length();
            var ammo = t.Ammo;
            t.LateralAccelVec = Vector3D.Zero;
            if (ammo.DriftAccel <= 0f || speed < 1e-3f)
                t.VelocityDir = nose;
            else
            {
                Vector3D v = t.VelocityDir * speed + nose * (ammo.DriftAccel * DT);

                if (t.RotationSettled && ammo.LateralAccel > 0f)
                {
                    Vector3D slide = v - nose * Vector3D.Dot(v, nose);
                    double slideSpeed = slide.Length();
                    if (slideSpeed > 1e-6)
                    {
                        double dv = Math.Min(ammo.LateralAccel * DT, slideSpeed);
                        t.LateralAccelVec = -slide / slideSpeed * (dv / DT);
                        v += t.LateralAccelVec * DT;
                    }
                }
                t.VelocityDir = v.LengthSquared() > 1e-12 ? Vector3D.Normalize(v) : nose;
            }

            if (t.RotationSettled)
            {
                double slip = Math.Acos(MathHelper.Clamp(Vector3D.Dot(t.VelocityDir, nose), -1.0, 1.0));
                if (ammo.LateralAccel <= 0f || MathHelper.ToDegrees(slip) < _cfg.SlipSettleDeg)
                {
                    t.TurnSeconds = Math.Max(0f, t.Elapsed - _cfg.ExitSeconds);
                    t.LateralAccelVec = Vector3D.Zero;
                }
            }

            Vector3 velocity = (Vector3)t.VelocityDir * speed;
            t.Position += (Vector3D)velocity * DT;
            t.Missile.SetWorldMatrix(OrientedMatrix(t.Position, nose, ref t.Up));
            SetVelocity(t.Missile, velocity);
            t.PrevForward = nose;
            t.Commanded = nose;
        }

        // Homing profile, one frame. Starts at HomingTurnRateMultiplier x the vanilla turn rate and HomingSpeedFactor x
        // DesiredSpeed (so a much tighter turn radius), both fading linearly to vanilla over HomingBoostSeconds
        void FlyHoming(Tracked t)
        {
            var def = t.Ammo.Definition;
            float k = _cfg.HomingBoostSeconds <= 0f ? 1f : MathHelper.Clamp(t.Elapsed / _cfg.HomingBoostSeconds, 0f, 1f);
            float rateDeg = t.Ammo.BaseTurnRateDeg * MathHelper.Lerp(_cfg.HomingTurnRateMultiplier, 1f, k);
            float cap = def.DesiredSpeed * MathHelper.Lerp(_cfg.HomingSpeedFactor, 1f, k);

            Vector3D nose = t.PrevForward;
            nose = TurnToward(nose, AimDirection(t, nose), MathHelper.ToRadians(rateDeg) * DT);

            float speed = Math.Min(t.Missile.LinearVelocity.Length(), cap);
            Vector3 velocity = (Vector3)nose * speed;
            t.Position += (Vector3D)velocity * DT;
            t.Missile.SetWorldMatrix(OrientedMatrix(t.Position, nose, ref t.Up));
            SetVelocity(t.Missile, velocity);
            t.PrevForward = nose;
            t.Commanded = nose;
        }

        // Jet-driven turn with no turn-rate limit, only a cap on angular acceleration. The nose spins up until its
        // angular speed reaches the speed it can just brake from at the remaining angle (sqrt(2·margin·accel·angle)),
        // then the opposite jets brake along that curve. The jets lag the curve a little, so the nose swings slightly
        // past the aim before settling. Vectors live in the plane tangent to the nose.
        Vector3D FlyTurn(Tracked t, Vector3D nose)
        {
            Vector3D aim = AimDirection(t, nose);
            Vector3D toward = aim - nose * Vector3D.Dot(nose, aim);
            double angle = Math.Acos(MathHelper.Clamp(Vector3D.Dot(nose, aim), -1.0, 1.0));
            Vector3D dir = toward.LengthSquared() > 1e-12 ? Vector3D.Normalize(toward) : Vector3D.Zero;

            double maxAccel = t.Ammo.AngularAccelMax;
            double curve = Math.Min(Math.Sqrt(2.0 * _cfg.BrakeMargin * maxAccel * angle), t.Ammo.AimGain * angle);

            Vector3D accel = (dir * curve - t.AngularVelocity) * _cfg.SpeedGain;
            if (accel.LengthSquared() > maxAccel * maxAccel)
                accel = Vector3D.Normalize(accel) * maxAccel;

            Vector3D w = t.AngularVelocity + accel * DT;
            double speed = w.Length();
            Vector3D next = speed * DT > 1e-9 ? TurnToward(nose, Vector3D.Normalize(nose + w / speed), speed * DT) : nose;

            t.AngularVelocity = w - next * Vector3D.Dot(next, w);
            t.AngularAccel = accel;

            if (MathHelper.ToDegrees(angle) < _cfg.SettleAngleDeg && MathHelper.ToDegrees(speed) < _cfg.SettleRateDeg)
                t.RotationSettled = true;
            return next;
        }

        // After hand-off. The seeker (server only, also mirrored to clients through ChangeTrajectorySync) moves the
        // nose every 10th frame in one jump; any such change is taken as the new commanded heading and the nose is
        // swung onto it at the vanilla turn rate per frame. Same total turn, but continuous.
        static void SmoothSeeker(Tracked t)
        {
            var missile = t.Missile;
            var matrix = missile.WorldMatrix;
            Vector3D current = matrix.Forward;
            if (Vector3D.DistanceSquared(current, t.PrevForward) > 1e-14)
                t.Commanded = current;

            Vector3D forward = TurnToward(t.PrevForward, t.Commanded, MathHelper.ToRadians(t.Ammo.BaseTurnRateDeg) * DT);
            if (Vector3D.DistanceSquared(current, forward) > 1e-14)
                missile.SetWorldMatrix(OrientedMatrix(matrix.Translation, forward, ref t.Up));
            t.PrevForward = forward;

            // Without preserved momentum the seeker also snaps velocity onto its own heading; keep it on the nose.
            if (!t.Ammo.Definition.MissilePreserveLaunchMomentum)
            {
                float speed = missile.LinearVelocity.Length();
                if (speed > 1e-3f)
                    SetVelocity(missile, (Vector3)forward * speed);
            }
        }

        // LinearVelocity is the missile's own m_linearVelocity; Physics exists on the server only.
        static void SetVelocity(IMyMissile missile, Vector3 v)
        {
            missile.LinearVelocity = v;
            if (missile.Physics != null)
                missile.Physics.LinearVelocity = v;
        }

        static Vector3D TurnToward(Vector3D from, Vector3D to, double maxRad)
        {
            double dot = MathHelper.Clamp(Vector3D.Dot(from, to), -1.0, 1.0);
            if (Math.Acos(dot) <= maxRad)
                return to;
            Vector3D axis = Vector3D.Cross(from, to);
            if (axis.LengthSquared() < 1e-12)
                axis = Vector3D.CalculatePerpendicularVector(from);
            axis.Normalize();
            return Vector3D.Normalize(Vector3D.TransformNormal(from, MatrixD.CreateFromAxisAngle(axis, maxRad)));
        }

        void UpdateVisuals(Tracked t)
        {
            if (MyAPIGateway.Utilities.IsDedicated || t.Ammo.Homing)
                return;

            bool trail = TrailOn(t);
            var vanilla = t.Missile.ParticleEffect;
            if (vanilla != null && trail == t.TrailHidden)
            {
                if (trail)
                    vanilla.Play();
                else
                    vanilla.StopEmitting(); // emitted smoke lingers and fades out naturally
                t.TrailHidden = !trail;
            }

            if (JetsOn(t))
            {
                if (t.Jets == null)
                    StartJets(t);
                DriveJets(t);
            }
            else
                StopJets(t);
        }

        static MyParticleEffect SpawnParented(Tracked t, string name, MatrixD local)
        {
            var entity = t.Missile as MyEntity;
            if (entity == null || entity.Render == null)
                return null;
            Vector3D worldPos = t.Missile.GetPosition();
            MyParticleEffect effect;
            return MyParticlesManager.TryCreateParticleEffect(name, ref local, ref worldPos, entity.Render.GetRenderObjectID(), out effect) ? effect : null;
        }

        void StartJets(Tracked t)
        {
            t.Jets = new MyParticleEffect[JetEffects.Length];
            t.JetSmoke = new MyParticleEffect[JetEffects.Length];
            t.JetLevels = new float[JetEffects.Length];
            for (int i = 0; i < JetEffects.Length; i++)
            {
                t.Jets[i] = SpawnParented(t, JetEffects[i], _jetLocalMatrix);
                if (t.Jets[i] != null)
                    t.Jets[i].UserBirthMultiplier = 0f;
                t.JetSmoke[i] = SpawnParented(t, JetSmokeEffects[i], _jetLocalMatrix);
                if (t.JetSmoke[i] != null)
                {
                    t.JetSmoke[i].UserBirthMultiplier = 0f;
                    t.JetSmoke[i].UserVelocityMultiplier = _cfg.JetSmokeSpeed;
                }
            }

            t.Smoke = SpawnParented(t, SMOKE_EFFECT, MatrixD.Identity);
            if (t.Smoke != null)
            {
                t.Smoke.UserBirthMultiplier = _cfg.ManeuverSmokeLevel;
                t.Smoke.UserVelocityMultiplier = 0f;
            }
        }

        // The ports come in two perpendicular pairs, so each fires with the component of the demand (turn
        // acceleration plus drift cancel, each normalised to this ammo's limit) along its own direction; a demand
        // between two ports fires both and their thrust adds up to it. Over-full demands are scaled down together
        // so the direction is kept. Each jet's plume follows its flame's strength.
        void DriveJets(Tracked t)
        {
            if (t.Jets == null)
                return;

            var m = t.Missile.WorldMatrix;
            double fullTurn = t.Ammo.AngularAccelMax;
            double fullSide = t.Ammo.LateralAccel;

            var wanted = new float[t.Jets.Length];
            float most = 0f;
            for (int i = 0; i < t.Jets.Length; i++)
            {
                Vector3D d = Vector3D.TransformNormal(JetDirections[i], _jetLocalMatrix);
                Vector3D worldDir = m.Right * d.X + m.Up * d.Y + m.Backward * d.Z;
                double turn = fullTurn > 0 ? Vector3D.Dot(worldDir, t.AngularAccel) / fullTurn : 0.0;
                double side = fullSide > 0 ? -Vector3D.Dot(worldDir, t.LateralAccelVec) / fullSide : 0.0;
                wanted[i] = (float)Math.Max(1 * (turn + side), 0.0);
                most = Math.Max(most, wanted[i]);
            }

            for (int i = 0; i < t.Jets.Length; i++)
            {
                var jet = t.Jets[i];
                if (jet == null)
                    continue;
                float level = most > 1f ? wanted[i] / most : wanted[i];
                t.JetLevels[i] = MathHelper.Lerp(t.JetLevels[i], level, _cfg.JetResponse);
                jet.UserBirthMultiplier = t.JetLevels[i];
                jet.UserScale = 0.5f + 0.5f * t.JetLevels[i];
                if (t.JetSmoke[i] != null)
                    t.JetSmoke[i].UserBirthMultiplier = _cfg.JetSmokeLevel * t.JetLevels[i];
            }

            if (t.Smoke != null)
            {
                // Motion inheritance moves the smoke with the torpedo's real displacement, drift included, while the
                // push is along the nose; capping it at the velocity along the nose keeps the smoke from ever moving
                // backward along that axis, however far the torpedo slides.
                float alongNose = Math.Max(0f, (float)Vector3D.Dot(t.Missile.LinearVelocity, m.Forward));
                t.Smoke.UserVelocityMultiplier = Math.Min(_cfg.ManeuverSmokeAccelFactor * t.Ammo.Definition.MissileAcceleration, alongNose);
            }
        }

        static void StopJets(Tracked t)
        {
            if (t.Smoke != null)
            {
                t.Smoke.Stop(false);
                t.Smoke = null;
            }
            if (t.Jets == null)
                return;
            foreach (var jet in t.Jets)
                if (jet != null)
                    jet.Stop(false);
            foreach (var plume in t.JetSmoke)
                if (plume != null)
                    plume.Stop(false);
            t.Jets = null;
            t.JetSmoke = null;
        }

        // Speed cap / floor. Runs after the missiles' own UpdateBeforeSimulation (MySector, priority 800) and before
        // physics, so the written velocity is what gets integrated this frame. Held at launch speed until the turn
        // ends, then ramped to DesiredSpeed from both sides (vanilla acceleration is too slow for the ramp).
        public override void UpdateBeforeSimulation()
        {
            if (_tracked.Count == 0)
                return;

            _iter.Clear();
            _iter.AddRange(_tracked.Values);
            foreach (var t in _iter)
            {
                var missile = t.Missile;
                if (missile.MarkedForClose || missile.Closed)
                    continue;

                float ramp = Ramp(t);
                if (ramp >= 1f)
                    continue;

                float desired = Math.Max(t.Ammo.Definition.DesiredSpeed, t.LaunchSpeed);
                float cap = MathHelper.Lerp(t.LaunchSpeed, desired, ramp);
                float floor = ramp <= 0f ? 0f : cap;

                var v = missile.LinearVelocity;
                float speed = v.Length();
                float target = MathHelper.Clamp(speed, floor, cap);
                if (target == speed)
                    continue;

                Vector3 dir = speed > 1e-3f ? v / speed : (Vector3)missile.WorldMatrix.Forward;
                SetVelocity(missile, dir * target);
            }
        }

        // Scripted path, or seeker smoothing after hand-off. Runs after the seeker's UpdateAfterSimulation10 (also
        // inside MySector), so it sees and overrides the seeker's heading change for this frame.
        public override void UpdateAfterSimulation()
        {
            if (_tracked.Count == 0)
                return;

            _iter.Clear();
            _iter.AddRange(_tracked.Values);
            foreach (var t in _iter)
            {
                var missile = t.Missile;
                if (missile.MarkedForClose || missile.Closed)
                    continue;

                t.Elapsed += DT;

                if (!t.Ammo.Homing)
                    PlanTurn(t);
                if (InScriptedPhase(t))
                {
                    if (t.Ammo.Homing)
                        FlyHoming(t);
                    else
                        FlyScripted(t);
                }
                else
                {
                    if (!t.HandedOff)
                    {
                        t.HandedOff = true;
                        if (IsServer)
                            SendHandOff(t);
                    }
                    SmoothSeeker(t);
                }

                UpdateVisuals(t);
            }
        }
    }
}
