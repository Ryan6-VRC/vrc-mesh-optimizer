using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Tests.Decimation
{
    /// <summary>
    /// Shading as a viewer gets it, on a written mesh against its source: every sample on a written triangle carries its
    /// share of the triangle's area; its shading normal is the written triangle's interpolated vertex normal there, and
    /// the source's is the interpolated normal at the nearest point of a source triangle facing the same way.
    /// </summary>
    internal struct Shading
    {
        public double Area;
        public double Over10;
        public double Over45;
        public double AngleSq;
        /// <summary>Metres: the farthest a sample stood from the source.</summary>
        public float MaxDistance;

        public float Share10 => (float)(Over10 / Area);
        public float Share45 => (float)(Over45 / Area);
        public float RmsDegrees => (float)math.sqrt(AngleSq / Area);

        public override string ToString() =>
            $"{Share10:P1} of area over 10 deg, {Share45:P1} over 45, rms {RmsDegrees:F1} deg, surface within {MaxDistance * 1000:F2} mm";
    }

    internal static class NormalFixtures
    {
        public const float Tolerance = 1e-3f;

        /// <summary>
        /// A flat band 200 x 30 mm facing +Z whose two long rims carry normals tilted 40 degrees outward, as a band authored
        /// to read rounded: nothing in its geometry marks the rims, so every collapse costs nothing in position.
        /// </summary>
        public static MeshBuilder TiltedBand()
        {
            const int ny = 6;
            var b = Fixtures.Grid(40, ny, 0.2f, 0.03f, out var index);
            b.Normals = new float3[b.VertexCount];
            var tilt = math.radians(40f);
            for (var j = 0; j <= ny; j++)
            for (var i = 0; i <= 40; i++)
            {
                var side = j == 0 ? -1f : j == ny ? 1f : 0f;
                b.Normals[index[i, j]] = side == 0f ? new float3(0, 0, 1) : new float3(0, side * math.sin(tilt), math.cos(tilt));
            }
            return b;
        }

        /// <summary>
        /// Opens a UV seam across the band <see cref="TiltedBand"/> builds, down grid column <paramref name="column"/>: the
        /// triangles past it take copies of its vertices, the same in all but their UVs, and the UV island past it moves
        /// over by a half.
        /// </summary>
        public static void SplitColumn(MeshBuilder b, int column)
        {
            var x = b.Positions[column].x;
            var copy = new Dictionary<int, int>();
            var count = b.VertexCount;
            var shift = new float2(0.5f, 0f);
            for (var v = 0; v < count; v++)
            {
                if (b.Positions[v].x > x) b.Uvs[v] += shift;
                if (b.Positions[v].x != x) continue;
                copy[v] = b.Add(b.Positions[v], b.Uvs[v] + shift);
                b.Normals = b.Normals.Append(b.Normals[v]).ToArray();
            }
            for (var t = 0; t < b.Triangles.Count; t++)
            {
                var tri = b.Triangles[t];
                if ((b.Positions[tri.x].x + b.Positions[tri.y].x + b.Positions[tri.z].x) / 3f <= x) continue;
                for (var k = 0; k < 3; k++)
                    if (copy.TryGetValue(tri[k], out var c)) tri[k] = c;
                b.Triangles[t] = tri;
            }
        }

        /// <summary>
        /// A strap: a closed shell 3 mm thick and 23 mm wide, run 100 mm with open ends and bent over a radius of 60 mm, as
        /// over a shoulder, its two rims rounded through a half circle. Normals are the smooth face normals, so across each
        /// rim they turn from front to back.
        /// </summary>
        public static MeshBuilder RoundedShell() => RoundedShell(0.02f, 0.003f, 8, 6, 40, 0.06f);

        /// <summary><paramref name="bend"/> is the radius the strap's length is bent over, 0 straight; the front is the outer side.</summary>
        public static MeshBuilder RoundedShell(float width, float thickness, int across, int rim, int along, float bend)
        {
            const float length = 0.1f;
            var r = thickness / 2f;
            var loop = new List<float2>();
            for (var k = 0; k < across; k++) loop.Add(new float2(-width / 2 + width * k / across, -r));
            for (var k = 0; k < rim; k++)
            {
                var a = -math.PI / 2 + math.PI * k / rim;
                loop.Add(new float2(width / 2 + r * math.cos(a), r * math.sin(a)));
            }
            for (var k = 0; k < across; k++) loop.Add(new float2(width / 2 - width * k / across, r));
            for (var k = 0; k < rim; k++)
            {
                var a = math.PI / 2 + math.PI * k / rim;
                loop.Add(new float2(-width / 2 + r * math.cos(a), r * math.sin(a)));
            }
            var n = loop.Count;
            var b = new MeshBuilder();
            var idx = new int[along + 1, n];
            for (var i = 0; i <= along; i++)
            for (var k = 0; k < n; k++)
            {
                var p = loop[k];
                var x = length * i / along;
                var pos = new float3(x, p.x, p.y);
                if (bend > 0f)
                {
                    var phi = x / bend;
                    pos = new float3((bend + p.y) * math.sin(phi), p.x, (bend + p.y) * math.cos(phi) - bend);
                }
                idx[i, k] = b.Add(pos);
            }
            for (var i = 0; i < along; i++)
            for (var k = 0; k < n; k++)
            {
                var k1 = (k + 1) % n;
                b.Tri(idx[i, k], idx[i, k1], idx[i + 1, k]);
                b.Tri(idx[i, k1], idx[i + 1, k1], idx[i + 1, k]);
            }
            return b;
        }

        /// <summary>
        /// A coin 10 mm across and 1.2 mm thick, 16-sided: each face a centre and two rings, the outer ring shared with the
        /// rim band. Face and inner ring normals point straight out of their face; the outer rings are tilted 45 degrees
        /// toward the rim, the normals of a coin authored to read bevelled.
        /// </summary>
        public static MeshBuilder Coin()
        {
            const int sides = 16;
            const float radius = 0.005f, inner = 0.0025f, half = 0.0006f;
            var b = new MeshBuilder();
            var normals = new List<float3>();
            int Add(float3 p, float3 normal)
            {
                normals.Add(normal);
                return b.Add(p);
            }
            int[] Ring(float r, float z, float tilt)
            {
                var ring = new int[sides];
                for (var s = 0; s < sides; s++)
                {
                    var a = 2f * math.PI * s / sides;
                    var radial = new float3(math.cos(a), math.sin(a), 0f);
                    ring[s] = Add(new float3(r * radial.xy, z), math.normalize(math.lerp(new float3(0, 0, math.sign(z)), radial, tilt)));
                }
                return ring;
            }
            var frontCentre = Add(new float3(0, 0, half), new float3(0, 0, 1));
            var frontInner = Ring(inner, half, 0f);
            var frontOuter = Ring(radius, half, 0.5f);
            var backOuter = Ring(radius, -half, 0.5f);
            var backInner = Ring(inner, -half, 0f);
            var backCentre = Add(new float3(0, 0, -half), new float3(0, 0, -1));
            for (var s = 0; s < sides; s++)
            {
                var t = (s + 1) % sides;
                b.Tri(frontCentre, frontInner[s], frontInner[t]);
                b.Tri(frontInner[s], frontOuter[s], frontOuter[t]);
                b.Tri(frontInner[s], frontOuter[t], frontInner[t]);
                b.Tri(frontOuter[s], backOuter[s], backOuter[t]);
                b.Tri(frontOuter[s], backOuter[t], frontOuter[t]);
                b.Tri(backCentre, backInner[t], backInner[s]);
                b.Tri(backInner[s], backOuter[t], backOuter[s]);
                b.Tri(backInner[s], backInner[t], backOuter[t]);
            }
            b.Normals = normals.ToArray();
            return b;
        }

        /// <summary>A sphere of the given radius, densely tessellated, with smooth normals: nothing to mis-shade.</summary>
        public static MeshBuilder SmoothSphere(float radius) => Fixtures.Icosphere(4, radius);

        /// <summary>
        /// A torus, radius 40 mm about its axis and 12 mm about its tube, 64 x 32, smooth normals. Unlike a sphere's, its
        /// normals are not a linear function of position, so a linear field extended off its triangle misses them.
        /// </summary>
        public static MeshBuilder SmoothTorus()
        {
            const int nu = 64, nv = 32;
            const float big = 0.04f, small = 0.012f;
            var b = new MeshBuilder();
            var idx = new int[nu, nv];
            for (var i = 0; i < nu; i++)
            for (var j = 0; j < nv; j++)
            {
                var u = 2f * math.PI * i / nu;
                var v = 2f * math.PI * j / nv;
                idx[i, j] = b.Add(new float3((big + small * math.cos(v)) * math.cos(u), (big + small * math.cos(v)) * math.sin(u), small * math.sin(v)));
            }
            for (var i = 0; i < nu; i++)
            for (var j = 0; j < nv; j++)
            {
                int i1 = (i + 1) % nu, j1 = (j + 1) % nv;
                b.Tri(idx[i, j], idx[i1, j], idx[i1, j1]);
                b.Tri(idx[i, j], idx[i1, j1], idx[i, j1]);
            }
            return b;
        }

        /// <summary>A sheet 120 mm square rippled 4 mm up and down on a 60 mm wavelength each way, as cloth folds, 64 x 64, smooth normals.</summary>
        public static MeshBuilder SmoothWaves()
        {
            var b = Fixtures.Grid(64, 64, 0.12f, 0.12f, out _);
            var k = 2f * math.PI / 0.06f;
            for (var v = 0; v < b.VertexCount; v++)
            {
                var p = b.Positions[v];
                b.Positions[v] = new float3(p.x, p.y, 0.004f * math.sin(k * p.x) * math.sin(k * p.y));
            }
            return b;
        }

        /// <summary>Measures a cut's shading against its source; see <see cref="Shading"/>. <paramref name="spacing"/> is the sample spacing in metres.</summary>
        public static Shading Measure(MeshBuilder b, int3[] written, float spacing)
        {
            var p = b.Positions.ToArray();
            var n = b.VertexNormals();
            var source = b.Triangles.ToArray();
            var centre = new float3[source.Length];
            var reach = new float[source.Length];
            var facing = new float3[source.Length];
            for (var i = 0; i < source.Length; i++)
            {
                var t = source[i];
                centre[i] = (p[t.x] + p[t.y] + p[t.z]) / 3f;
                reach[i] = math.max(math.distance(centre[i], p[t.x]), math.max(math.distance(centre[i], p[t.y]), math.distance(centre[i], p[t.z])));
                facing[i] = math.normalizesafe(math.cross(p[t.y] - p[t.x], p[t.z] - p[t.x]));
            }
            var result = new Shading();
            foreach (var w in written)
            {
                var face = math.cross(p[w.y] - p[w.x], p[w.z] - p[w.x]);
                var area = 0.5f * math.length(face);
                if (!(area > 1e-14f)) continue;
                face /= 2f * area;
                var k = math.max(1, (int)math.ceil(math.sqrt(2f * area) / spacing));
                var weight = area / (k * k);
                for (var i = 0; i < k; i++)
                for (var j = 0; i + j < k; j++)
                {
                    Sample(new float2(i + 1f / 3f, j + 1f / 3f) / k);
                    if (i + j < k - 1) Sample(new float2(i + 2f / 3f, j + 2f / 3f) / k);
                }

                void Sample(float2 uv)
                {
                    var bary = new float3(1f - uv.x - uv.y, uv.x, uv.y);
                    var q = bary.x * p[w.x] + bary.y * p[w.y] + bary.z * p[w.z];
                    var shown = math.normalizesafe(bary.x * n[w.x] + bary.y * n[w.y] + bary.z * n[w.z]);
                    var best = float.PositiveInfinity;
                    var truth = float3.zero;
                    for (var s = 0; s < source.Length; s++)
                    {
                        if (math.dot(facing[s], face) <= 0f) continue;
                        var gap = math.distance(q, centre[s]) - reach[s];
                        if (gap > 0f && gap * gap >= best) continue;
                        var t = source[s];
                        var c = Fixtures.ClosestPoint(q, p[t.x], p[t.y], p[t.z], out var sb);
                        var d = math.lengthsq(c - q);
                        if (d >= best) continue;
                        best = d;
                        truth = sb.x * n[t.x] + sb.y * n[t.y] + sb.z * n[t.z];
                    }
                    var angle = math.degrees(math.acos(math.clamp(math.dot(shown, math.normalizesafe(truth)), -1f, 1f)));
                    result.Area += weight;
                    result.AngleSq += weight * angle * angle;
                    if (angle > 10f) result.Over10 += weight;
                    if (angle > 45f) result.Over45 += weight;
                    result.MaxDistance = math.max(result.MaxDistance, math.sqrt(best));
                }
            }
            return result;
        }
    }
}
