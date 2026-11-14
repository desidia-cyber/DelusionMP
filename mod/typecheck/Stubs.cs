// Minimal stand-ins so the engine-independent core compiles outside Unity.
// These exist only for the typecheck build; the shipped plugin compiles against the
// real UnityEngine.CoreModule / Il2CppInterop assemblies.
namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public override string ToString() => $"({x}, {y}, {z})";
    }
}
