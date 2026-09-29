using System;
using Ryan6Vrc.MeshOptimizer.Core.Model;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Core.Decimation
{
    /// <summary>
    /// Synthetic bends: configurations in which a collapse's error is also measured, each one bone of the mesh's own rig
    /// turned about an axis through its pivot, every bone under it carried along. A decimated triangle spanning a joint
    /// cuts the chord of the curve the finer source follows once the joint bends; at rest nothing shows it, since the
    /// surface there is near flat. The bends come from the rig alone, never from an animation or a pose.
    /// </summary>
    public struct BendSettings
    {
        /// <summary>
        /// Degrees each bone swings, either way about each of two axes square to its length. 0 turns bends off, and the
        /// result is then exactly what it is without a rig. A bone is not twisted about its own length: a quarter turn of
        /// twist collapses a linear-blend-skinned source on itself, and holding a decimated surface to that costs
        /// triangles for a state no avatar shows.
        /// </summary>
        public float Degrees;
        /// <summary>
        /// Where the surface lies near another layer, the fraction of the gap a collapse's error in any bend may reach;
        /// acts only with the clearance rule on. 0 turns it off, leaving bends to count against the error level alone. A
        /// bend's error is a mean distance over the surface a vertex stands for, not its farthest point, and a bend is
        /// already an extreme of the joint, so the default is looser than the rest rule's share: tighter costs markedly
        /// more triangles near every joint a layer hugs.
        /// </summary>
        public float ClearanceShare;

        public static BendSettings Default => new BendSettings { Degrees = 90f, ClearanceShare = 0.7f };
    }

    /// <summary>
    /// The avatar's skeleton, per avatar-wide bone id (see <see cref="BoneInfluence.Bone"/>), in the evaluation meshes'
    /// space. Unassigned, a mesh has no bends.
    /// </summary>
    public struct BendRig
    {
        /// <summary>Per bone, its parent's id, or -1.</summary>
        public NativeArray<int> Parent;
        /// <summary>Per bone, its pivot.</summary>
        public NativeArray<float3> Position;
        /// <summary>
        /// Per bone, the unit direction it runs along, toward its children; zero where it has none. The bone's swing
        /// axes are square to it.
        /// </summary>
        public NativeArray<float3> Axis;
        /// <summary>Per bone, its own X axis: the first swing axis where it is not along <see cref="Axis"/>.</summary>
        public NativeArray<float3> Right;

        public bool IsCreated => Parent.IsCreated && Position.IsCreated && Axis.IsCreated && Right.IsCreated && Parent.Length > 0;
    }

    /// <summary>
    /// One mesh's bends, as the quadric bank and the collapse loop read them. A bend moves every point of the mesh by the
    /// share s of its skin weight on the bent bone and the bones under it: p + s (R - I)(p - c). Where s is 0 or 1 a
    /// point moves rigidly, with the parent or with the bone, and distances among such points are as at rest. So a
    /// vertex's quadric in a bend is held in its own home, the side it mostly moves with (s at or over one half), and a
    /// vertex needs a quadric of its own for a bend only where its surroundings do not all move with its home, which is
    /// near the joint; elsewhere its rest quadric stands for it. Frame ids start at <see cref="FrameBase"/>, above every
    /// shape frame's, so a vertex's frame list holds its shape frames first and its bends after them.
    /// </summary>
    internal struct Bends : IDisposable
    {
        public const int FrameBase = 1 << 28;
        /// <summary>
        /// Metres. A share counts as 0 or 1 where taking it so moves its vertex by no more than this in any bend: skin
        /// weights that sum to 1 in float land a rounding off it, and a vertex so treated moves rigidly with its home.
        /// </summary>
        const float Snap = 1e-6f;

        /// <summary>Frames per bent bone; a frame's bone is its index divided by this.</summary>
        public int PerBone;
        /// <summary>64-bit words of <see cref="Home"/> per source vertex.</summary>
        public int Words;
        /// <summary>Per bent bone, its pivot.</summary>
        [ReadOnly] public NativeArray<float3> Pivot;
        /// <summary>Per frame, the rotation of the bent bone's side.</summary>
        [ReadOnly] public NativeArray<float3x3> Rotation;
        /// <summary>Per source vertex, <see cref="Words"/> words: bit b set when the vertex's home in bone b's bends is the bone's side.</summary>
        [ReadOnly] public NativeArray<ulong> Home;
        /// <summary>
        /// Per source vertex, length count + 1: the bones it is split between, MixBone/MixShare[MixStart[v] ..
        /// MixStart[v + 1]), bone ascending, with its share on each. A bone not listed has a share of 0 or 1, the home's.
        /// </summary>
        [ReadOnly] public NativeArray<int> MixStart;
        [ReadOnly] public NativeArray<int> MixBone;
        [ReadOnly] public NativeArray<float> MixShare;

        public int FrameCount => Rotation.IsCreated ? Rotation.Length : 0;
        public bool Active => FrameCount > 0;

        public void Dispose()
        {
            if (Pivot.IsCreated) Pivot.Dispose();
            if (Rotation.IsCreated) Rotation.Dispose();
            if (Home.IsCreated) Home.Dispose();
            if (MixStart.IsCreated) MixStart.Dispose();
            if (MixBone.IsCreated) MixBone.Dispose();
            if (MixShare.IsCreated) MixShare.Dispose();
        }

        /// <summary>No bends: every array empty, so jobs can take it.</summary>
        public static Bends None(Allocator allocator) => new Bends
        {
            PerBone = 1,
            Words = 0,
            Pivot = new NativeArray<float3>(0, allocator),
            Rotation = new NativeArray<float3x3>(0, allocator),
            Home = new NativeArray<ulong>(0, allocator),
            MixStart = new NativeArray<int>(0, allocator),
            MixBone = new NativeArray<int>(0, allocator),
            MixShare = new NativeArray<float>(0, allocator),
        };

        /// <summary>
        /// Every bone the mesh is weighted to is bent, four ways; one weighted to nothing moves the mesh only through a
        /// weighted bone under it, which is bent in its own right.
        /// </summary>
        public static Bends Build(in EvalMesh mesh, in BendRig rig, in BendSettings settings, Allocator allocator)
        {
            if (!rig.IsCreated || !(settings.Degrees > 0f) || !mesh.Weights.IsCreated || mesh.Weights.Length == 0) return None(allocator);
            var boneCount = rig.Parent.Length;
            if (rig.Position.Length != boneCount || rig.Axis.Length != boneCount || rig.Right.Length != boneCount)
                throw new ArgumentException($"the rig has {boneCount} parents, {rig.Position.Length} positions, {rig.Axis.Length} axes and {rig.Right.Length} right axes; give one of each per bone");

            var bent = new int[boneCount];
            for (var b = 0; b < boneCount; b++) bent[b] = -1;
            var count = 0;
            foreach (var w in mesh.Weights)
            {
                if (!(w.Weight > 0f)) continue;
                if (w.Bone < 0 || w.Bone >= boneCount)
                    throw new ArgumentException($"a vertex is weighted to bone {w.Bone}, outside the rig's {boneCount} bones");
                if (bent[w.Bone] < 0) bent[w.Bone] = count++;
            }
            var bones = new int[count];
            for (var b = 0; b < boneCount; b++)
                if (bent[b] >= 0) bones[bent[b]] = b;

            const int perBone = 4;
            var result = new Bends { PerBone = perBone, Words = (count + 63) / 64 };
            try
            {
                result.Pivot = new NativeArray<float3>(count, allocator);
                result.Rotation = new NativeArray<float3x3>(count * perBone, allocator);
                var a = math.radians(settings.Degrees);
                for (var i = 0; i < count; i++)
                {
                    var b = bones[i];
                    result.Pivot[i] = rig.Position[b];
                    Axes(rig, b, out var e1, out var e2);
                    var k = i * perBone;
                    result.Rotation[k] = new float3x3(quaternion.AxisAngle(e1, a));
                    result.Rotation[k + 1] = new float3x3(quaternion.AxisAngle(e1, -a));
                    result.Rotation[k + 2] = new float3x3(quaternion.AxisAngle(e2, a));
                    result.Rotation[k + 3] = new float3x3(quaternion.AxisAngle(e2, -a));
                }

                var v = mesh.VertexCount;
                result.Home = new NativeArray<ulong>(v * result.Words, allocator);
                result.MixStart = new NativeArray<int>(v + 1, allocator);
                using var mixBone = new NativeList<int>(v, Allocator.Persistent);
                using var mixShare = new NativeList<float>(v, Allocator.Persistent);
                using var parent = new NativeArray<int>(rig.Parent, Allocator.Persistent);
                using var bentOf = new NativeArray<int>(bent, Allocator.Persistent);
                new ShareJob
                {
                    WeightStart = mesh.WeightStart,
                    Weights = mesh.Weights,
                    Parent = parent,
                    Bent = bentOf,
                    Positions = mesh.Positions,
                    Pivot = result.Pivot,
                    BentCount = count,
                    Words = result.Words,
                    Home = result.Home,
                    MixStart = result.MixStart,
                    MixBone = mixBone,
                    MixShare = mixShare,
                }.Run();
                result.MixBone = new NativeArray<int>(mixBone.AsArray(), allocator);
                result.MixShare = new NativeArray<float>(mixShare.AsArray(), allocator);
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        /// <summary>Two swing axes square to the bone's length, the first as near its own X as that allows.</summary>
        static void Axes(in BendRig rig, int b, out float3 e1, out float3 e2)
        {
            var along = math.normalizesafe(rig.Axis[b]);
            if (math.lengthsq(along) < 0.5f)
            {
                var p = rig.Parent[b];
                along = p >= 0 ? math.normalizesafe(rig.Position[b] - rig.Position[p]) : float3.zero;
            }
            if (math.lengthsq(along) < 0.5f) along = new float3(0f, 1f, 0f);
            var right = rig.Right[b];
            e1 = math.normalizesafe(right - along * math.dot(right, along));
            if (math.lengthsq(e1) < 0.5f)
                e1 = math.normalizesafe(math.cross(along, math.abs(along.x) < 0.9f ? new float3(1f, 0f, 0f) : new float3(0f, 1f, 0f)));
            e2 = math.cross(along, e1);
        }

        [BurstCompile(CompileSynchronously = true)]
        struct ShareJob : IJob
        {
            [ReadOnly] public NativeArray<int> WeightStart;
            [ReadOnly] public NativeArray<BoneInfluence> Weights;
            [ReadOnly] public NativeArray<int> Parent;
            [ReadOnly] public NativeArray<int> Bent;
            [ReadOnly] public NativeArray<float3> Positions;
            [ReadOnly] public NativeArray<float3> Pivot;
            public int BentCount;
            public int Words;
            public NativeArray<ulong> Home;
            public NativeArray<int> MixStart;
            public NativeList<int> MixBone;
            public NativeList<float> MixShare;

            public void Execute()
            {
                var share = new NativeArray<float>(BentCount, Allocator.Temp);
                var touched = new NativeList<int>(16, Allocator.Temp);
                var vertices = MixStart.Length - 1;
                for (var v = 0; v < vertices; v++)
                {
                    MixStart[v] = MixBone.Length;
                    touched.Clear();
                    for (var i = WeightStart[v]; i < WeightStart[v + 1]; i++)
                    {
                        var w = Weights[i];
                        if (!(w.Weight > 0f)) continue;
                        // The influence moves with every bent bone above it, itself included.
                        var guard = 0;
                        for (var a = w.Bone; a >= 0 && a < Bent.Length && guard < 1024; a = Parent[a], guard++)
                        {
                            var k = Bent[a];
                            if (k < 0) continue;
                            if (share[k] == 0f) touched.Add(k);
                            share[k] += w.Weight;
                        }
                    }
                    touched.Sort();
                    for (var i = 0; i < touched.Length; i++)
                    {
                        var k = touched[i];
                        var s = share[k];
                        share[k] = 0f;
                        if (s >= 0.5f) Home[v * Words + (k >> 6)] |= 1UL << (k & 63);
                        // Taking the share as its home's moves the vertex by at most the dropped part times |R - I| <= 2
                        // times its distance from the pivot.
                        if (math.min(s, 1f - s) * 2f * math.distance(Positions[v], Pivot[k]) <= Snap) continue;
                        MixBone.Add(k);
                        MixShare.Add(s);
                    }
                }
                MixStart[vertices] = MixBone.Length;
            }
        }

        // ---------------------------------------------------------------- queries, callable from Burst

        public int BoneOf(int frame) => frame / PerBone;

        public bool HomeIn(int source, int bone) => (Home[source * Words + (bone >> 6)] & (1UL << (bone & 63))) != 0;

        /// <summary>The source vertex's share on the bone's side: its mixed share, or its home's 0 or 1.</summary>
        public double ShareOf(int source, int bone)
        {
            for (var i = MixStart[source]; i < MixStart[source + 1]; i++)
            {
                var b = MixBone[i];
                if (b == bone) return MixShare[i];
                if (b > bone) break;
            }
            return HomeIn(source, bone) ? 1.0 : 0.0;
        }

        /// <summary>
        /// Where the source vertex at <paramref name="p"/> stands in the frame, read in the given home's space: the bone's
        /// side undone for home true, as it stands for home false.
        /// </summary>
        public double3 InHome(int source, double3 p, int frame, bool home)
        {
            var bone = BoneOf(frame);
            var r = (double3x3)Rotation[frame];
            var c = (double3)Pivot[bone];
            var s = ShareOf(source, bone);
            var own = HomeIn(source, bone);
            var d = p - c;
            // Posed: p + s (R - I) d. Read from the bone's side: R^T (posed - c) + c = p + (1 - s)(R^T - I) d.
            var y = own ? p + (1.0 - s) * (math.mul(math.transpose(r), d) - d) : p + s * (math.mul(r, d) - d);
            if (own == home) return y;
            return own ? math.mul(r, y - c) + c : math.mul(math.transpose(r), y - c) + c;
        }

        /// <summary>A quadric held in home <paramref name="from"/>'s space, read in home <paramref name="to"/>'s.</summary>
        public Quadric Carry(in Quadric q, int frame, bool from, bool to)
        {
            if (from == to) return q;
            var r = (double3x3)Rotation[frame];
            var c = (double3)Pivot[BoneOf(frame)];
            // A point y in the destination's space is m y + t in the source's.
            var m = to ? r : math.transpose(r);
            return q.Transformed(m, c - math.mul(m, c));
        }

        /// <summary>Adds to <paramref name="frames"/> every frame of every bone the source vertex is split between.</summary>
        public void AddMixed(int source, ref NativeList<int> frames)
        {
            for (var i = MixStart[source]; i < MixStart[source + 1]; i++) AddBone(MixBone[i], ref frames);
        }

        /// <summary>Adds to <paramref name="frames"/> every frame of every bone in whose bends the two source vertices have different homes.</summary>
        public void AddApart(int a, int b, ref NativeList<int> frames)
        {
            for (var i = 0; i < Words; i++)
            {
                var x = Home[a * Words + i] ^ Home[b * Words + i];
                while (x != 0)
                {
                    AddBone(i * 64 + math.tzcnt(x), ref frames);
                    x &= x - 1;
                }
            }
        }

        void AddBone(int bone, ref NativeList<int> frames)
        {
            for (var k = 0; k < PerBone; k++) frames.Add(FrameBase + bone * PerBone + k);
        }
    }
}
