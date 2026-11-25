using ProtoBuf;
using VRageMath;

namespace VLS_Fix
{
    [ProtoContract]
    public class VlsPacket
    {
        public const byte LAUNCH = 1;
        public const byte HAND_OFF = 2;

        [ProtoMember(1)] public byte Kind;
        [ProtoMember(2)] public long MissileId;
        [ProtoMember(3)] public long TargetId;
        [ProtoMember(4)] public float Elapsed;
        [ProtoMember(5)] public float TurnSeconds;
        [ProtoMember(6)] public Vector3D Position;
        [ProtoMember(7)] public Vector3D Forward;
        [ProtoMember(8)] public Vector3 Velocity;
    }
}