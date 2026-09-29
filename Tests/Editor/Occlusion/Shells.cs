using System.Collections.Generic;
using Ryan6Vrc.MeshOptimizer.Core.Model;
using Ryan6Vrc.MeshOptimizer.Core.Occlusion;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Tests.Occlusion
{
    /// <summary>How a live frame moves the vertices of a shell within its cap.</summary>
    internal enum MoveKind
    {
        /// <summary>The cap slides by the amount along <see cref="Move.Direction"/>, or its axis: a flap opening, or a surface leaving.</summary>
        Away,
        /// <summary>The cap swells outward by the amount: a surface pushed out through what covers it.</summary>
        Push,
        /// <summary>The cap sinks inward by the amount: a cover pulled in through what it covers.</summary>
        Sink,
    }

    internal struct Move
    {
        public MoveKind Kind;
        public float3 Axis;
        /// <summary>Vertices within this angle of the axis move; 180 moves the whole shell.</summary>
        public float CapDegrees;
        public float Amount;
        /// <summary>For Away: where the cap slides. Zero slides it along its axis.</summary>
        public float3 Direction;
        /// <summary>Moves the shell's detached island instead of a cap: it slides clear without stretching anything.</summary>
        public bool IslandOnly;

        public override string ToString() => IslandOnly
            ? $"island {Kind} toward {Direction} by {Amount:F3}"
            : $"{Kind} {CapDegrees:F0}deg about {Axis} toward {Direction} by {Amount:F3}";
    }

    /// <summary>
    /// A closed UV sphere about its centre, one renderer or split by azimuth into several, optionally with a
    /// hole, a bulge that makes two shells cross, and live frames. Vertices are shared, poles included, so
    /// the surface is closed exactly; normals are the geometry's own.
    /// </summary>
    internal sealed class Shell
    {
        public string Name = "shell";
        public float3 Centre;
        public float Radius = 0.1f;
        /// <summary>Radius grows by this times the direction's y: two shells with opposite bulges cross at the equator.</summary>
        public float Bulge;
        public BoneInfluence[] Weights = MeshBuilder.On(1);
        public bool Occludes = true;
        /// <summary>Wound to face the centre.</summary>
        public bool Inward;
        public FaceCulling Culling = FaceCulling.Back;
        public float3 HoleAxis = new float3(0, 1, 0);
        /// <summary>Faces whose centre lies within this angle of <see cref="HoleAxis"/> are left out; 0 closes the shell.</summary>
        public float HoleDegrees;
        /// <summary>Faces whose centre lies within this angle of <see cref="IslandAxis"/> get vertices of their own, a cap a frame can lift clear.</summary>
        public float IslandDegrees;
        public float3 IslandAxis = new float3(0, 1, 0);
        public int Rings = 8;
        public int Segments = 16;
        public quaternion Orientation = quaternion.identity;
        /// <summary>Renderers to split into by azimuth about the shell's own pole, each carrying every frame on its part.</summary>
        public int Sectors = 1;
        public readonly List<Move> Frames = new List<Move>();

        public override string ToString()
        {
            var w = "";
            foreach (var b in Weights) w += $"{b.Bone}:{b.Weight:F2} ";
            var f = "";
            foreach (var m in Frames) f += $" [{m}]";
            return $"{Name} r={Radius:F4} bulge={Bulge:F3} bones={w}occludes={Occludes} inward={Inward} cull={Culling} " +
                   $"hole={HoleDegrees:F0}deg@{HoleAxis} island={IslandDegrees:F0}deg@{IslandAxis} {Rings}x{Segments} sectors={Sectors} orientation={Orientation.value}{f}";
        }

        float3 Dir(int ring, int seg)
        {
            var theta = math.PI * ring / Rings;
            var phi = 2f * math.PI * seg / Segments;
            var d = new float3(math.sin(theta) * math.cos(phi), math.cos(theta), math.sin(theta) * math.sin(phi));
            return math.mul(Orientation, d);
        }

        float3 At(float3 dir) => Centre + dir * (Radius + Bulge * dir.y);

        /// <summary>The renderers, one per sector with any face.</summary>
        public List<MeshBuilder> Build()
        {
            // One vertex per pole, Segments per ring between.
            var pos = new List<float3>();
            var dirs = new List<float3>();
            void V(float3 d) { dirs.Add(d); pos.Add(At(d)); }
            V(Dir(0, 0));
            for (var i = 1; i < Rings; i++)
                for (var j = 0; j < Segments; j++) V(Dir(i, j));
            V(Dir(Rings, 0));
            int Ring(int i, int j) => i == 0 ? 0 : i == Rings ? pos.Count - 1 : 1 + (i - 1) * Segments + ((j % Segments) + Segments) % Segments;

            var tris = new List<int3>();
            void T(int a, int b, int c) => tris.Add(Inward ? new int3(a, c, b) : new int3(a, b, c));
            for (var i = 0; i < Rings; i++)
                for (var j = 0; j < Segments; j++)
                {
                    int a = Ring(i, j), b = Ring(i, j + 1), c = Ring(i + 1, j), d = Ring(i + 1, j + 1);
                    if (i == 0) T(b, d, c);
                    else if (i == Rings - 1) T(a, b, c);
                    else { T(a, b, c); T(b, d, c); }
                }

            // Normals from the closed surface, before any hole, so a rim vertex points where the surface does.
            var nrm = new float3[pos.Count];
            foreach (var t in tris)
            {
                var g = math.cross(pos[t.y] - pos[t.x], pos[t.z] - pos[t.x]);
                nrm[t.x] += g; nrm[t.y] += g; nrm[t.z] += g;
            }
            for (var v = 0; v < nrm.Length; v++) nrm[v] = math.normalize(nrm[v]);

            var pole = math.mul(Orientation, new float3(0, 1, 0));
            var basisX = math.mul(Orientation, new float3(1, 0, 0));
            var basisZ = math.mul(Orientation, new float3(0, 0, 1));
            var sectorTris = new List<(int3 tri, bool island)>[Sectors];
            for (var k = 0; k < Sectors; k++) sectorTris[k] = new List<(int3, bool)>();
            foreach (var t in tris)
            {
                var centre = math.normalize(dirs[t.x] + dirs[t.y] + dirs[t.z]);
                if (HoleDegrees > 0f && Degrees(centre, HoleAxis) < HoleDegrees) continue;
                var island = IslandDegrees > 0f && Degrees(centre, IslandAxis) < IslandDegrees;
                var phi = math.atan2(math.dot(centre, basisZ), math.dot(centre, basisX));
                if (phi < 0f) phi += 2f * math.PI;
                var sector = math.min(Sectors - 1, (int)(phi / (2f * math.PI) * Sectors));
                sectorTris[sector].Add((t, island));
            }

            var outList = new List<MeshBuilder>();
            foreach (var list in sectorTris)
            {
                if (list.Count == 0) continue;
                var b = new MeshBuilder();
                var map = new Dictionary<(int v, bool island), int>();
                int Map(int v, bool island)
                {
                    if (map.TryGetValue((v, island), out var i)) return i;
                    i = b.AddVertex(pos[v], nrm[v], new float2(0.5f, 0.5f), Weights);
                    map[(v, island)] = i;
                    return i;
                }
                foreach (var (t, island) in list) b.AddTriangle(Map(t.x, island), Map(t.y, island), Map(t.z, island));
                foreach (var mv in Frames)
                {
                    var frame = b.AddFrame();
                    var axis = math.normalize(mv.Axis);
                    var slide = math.any(mv.Direction != 0f) ? math.normalize(mv.Direction) : axis;
                    var moved = new List<(int local, float3 delta)>();
                    foreach (var kv in map)
                    {
                        var d = dirs[kv.Key.v];
                        if (mv.IslandOnly ? !kv.Key.island : Degrees(d, axis) > mv.CapDegrees) continue;
                        var delta = mv.Kind == MoveKind.Away ? slide * mv.Amount : mv.Kind == MoveKind.Push ? d * mv.Amount : -d * mv.Amount;
                        moved.Add((kv.Value, delta));
                    }
                    moved.Sort((x, y) => x.local.CompareTo(y.local));
                    foreach (var (local, delta) in moved) frame.Add(new ShapeDelta { Vertex = local, Delta = delta });
                }
                outList.Add(b);
            }
            return outList;
        }

        static float Degrees(float3 a, float3 b) => math.degrees(math.acos(math.clamp(math.dot(math.normalize(a), math.normalize(b)), -1f, 1f)));

        /// <summary>Adds every renderer of the shell to the scene; returns each one's scene index and geometry.</summary>
        public List<(int index, MeshBuilder mesh)> AddTo(Scene scene)
        {
            var added = new List<(int, MeshBuilder)>();
            var sub = Culling == FaceCulling.Back ? null : new OccluderSubmesh { Culling = Culling };
            foreach (var b in Build())
            {
                added.Add((scene.Meshes.Count, b));
                if (sub != null) scene.Add(b, Occludes, sub);
                else scene.Add(b, Occludes);
            }
            return added;
        }
    }
}
