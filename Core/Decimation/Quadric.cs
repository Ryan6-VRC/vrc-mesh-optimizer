using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Core.Decimation
{
    /// <summary>
    /// A weighted sum of squared distances to planes, Garland &amp; Heckbert's quadric, in double precision.
    /// Positions are metres and the errors that matter are around a tenth of a millimetre squared, which
    /// float32 cannot resolve against a plane offset near one metre.
    ///
    /// Evaluate(p) = sum of weight * (n . p + d)^2. <see cref="W"/> is the summed weight, so Evaluate / W is a
    /// weighted mean squared distance and its square root a distance in metres.
    /// </summary>
    internal struct Quadric
    {
        public double A00, A01, A02, A11, A12, A22;
        public double B0, B1, B2;
        public double C;
        public double W;

        /// <summary>The plane n . p + d = 0 with unit normal n, at the given weight.</summary>
        public static Quadric FromPlane(double3 n, double d, double weight)
        {
            return new Quadric
            {
                A00 = weight * n.x * n.x, A01 = weight * n.x * n.y, A02 = weight * n.x * n.z,
                A11 = weight * n.y * n.y, A12 = weight * n.y * n.z, A22 = weight * n.z * n.z,
                B0 = weight * n.x * d, B1 = weight * n.y * d, B2 = weight * n.z * d,
                C = weight * d * d,
                W = weight,
            };
        }

        public double Evaluate(double3 p)
        {
            var ax = A00 * p.x + A01 * p.y + A02 * p.z;
            var ay = A01 * p.x + A11 * p.y + A12 * p.z;
            var az = A02 * p.x + A12 * p.y + A22 * p.z;
            var r = p.x * ax + p.y * ay + p.z * az + 2.0 * (B0 * p.x + B1 * p.y + B2 * p.z) + C;
            return math.max(r, 0.0);
        }

        public void Add(in Quadric q)
        {
            A00 += q.A00; A01 += q.A01; A02 += q.A02; A11 += q.A11; A12 += q.A12; A22 += q.A22;
            B0 += q.B0; B1 += q.B1; B2 += q.B2; C += q.C; W += q.W;
        }

        public static Quadric Sum(in Quadric a, in Quadric b)
        {
            var r = a;
            r.Add(b);
            return r;
        }

        /// <summary>
        /// The same planes read in another space: the result evaluated at y equals this evaluated at m y + t. Exact for
        /// any affine map, so a quadric carried through a rigid motion measures the same distances.
        /// </summary>
        public Quadric Transformed(in double3x3 m, double3 t)
        {
            var a = new double3x3(A00, A01, A02, A01, A11, A12, A02, A12, A22);
            var b = new double3(B0, B1, B2);
            var at = math.mul(a, t);
            var ma = math.mul(math.transpose(m), math.mul(a, m));
            var mb = math.mul(math.transpose(m), at + b);
            return new Quadric
            {
                A00 = ma.c0.x, A01 = ma.c1.x, A02 = ma.c2.x, A11 = ma.c1.y, A12 = ma.c2.y, A22 = ma.c2.z,
                B0 = mb.x, B1 = mb.y, B2 = mb.z,
                C = math.dot(t, at) + 2.0 * math.dot(b, t) + C,
                W = W,
            };
        }
    }

    /// <summary>
    /// One scalar attribute's deviation from each accumulated triangle's linear field, Hoppe 1999's form
    /// specialised to a target whose attribute value is fixed: sum of weight * (g . p + d - a)^2, where (g, d)
    /// is a triangle's attribute gradient and offset. Stored over the homogeneous point h = (p, 1).
    /// </summary>
    internal struct AttributeQuadric
    {
        // M = sum w (g,d)(g,d)^T, L = sum w (g,d), S = sum w.
        public double M00, M01, M02, M03, M11, M12, M13, M22, M23, M33;
        public double L0, L1, L2, L3;
        public double S;

        public static AttributeQuadric FromGradient(double3 g, double d, double weight)
        {
            return new AttributeQuadric
            {
                M00 = weight * g.x * g.x, M01 = weight * g.x * g.y, M02 = weight * g.x * g.z, M03 = weight * g.x * d,
                M11 = weight * g.y * g.y, M12 = weight * g.y * g.z, M13 = weight * g.y * d,
                M22 = weight * g.z * g.z, M23 = weight * g.z * d,
                M33 = weight * d * d,
                L0 = weight * g.x, L1 = weight * g.y, L2 = weight * g.z, L3 = weight * d,
                S = weight,
            };
        }

        public double Evaluate(double3 p, double a)
        {
            var hx = M00 * p.x + M01 * p.y + M02 * p.z + M03;
            var hy = M01 * p.x + M11 * p.y + M12 * p.z + M13;
            var hz = M02 * p.x + M12 * p.y + M22 * p.z + M23;
            var hw = M03 * p.x + M13 * p.y + M23 * p.z + M33;
            var quad = p.x * hx + p.y * hy + p.z * hz + hw;
            var lin = L0 * p.x + L1 * p.y + L2 * p.z + L3;
            return math.max(quad - 2.0 * a * lin + a * a * S, 0.0);
        }

        public void Add(in AttributeQuadric q)
        {
            M00 += q.M00; M01 += q.M01; M02 += q.M02; M03 += q.M03; M11 += q.M11; M12 += q.M12; M13 += q.M13;
            M22 += q.M22; M23 += q.M23; M33 += q.M33;
            L0 += q.L0; L1 += q.L1; L2 += q.L2; L3 += q.L3; S += q.S;
        }
    }

    /// <summary>Texture-coordinate error for both UV0 channels, in metres of texel displacement squared.</summary>
    internal struct UvQuadric
    {
        public AttributeQuadric U;
        public AttributeQuadric V;

        public double Evaluate(double3 p, float2 uv) => U.Evaluate(p, uv.x) + V.Evaluate(p, uv.y);

        public void Add(in UvQuadric q)
        {
            U.Add(q.U);
            V.Add(q.V);
        }
    }

    /// <summary>
    /// A vertex normal's deviation from each accumulated triangle's linear normal field: the three channels'
    /// <see cref="AttributeQuadric"/>s, summed into one, since they share every triangle's weight. Evaluate(p, n) = sum of
    /// weight * |G p + d - n|^2, where row c of G and entry c of d are channel c's gradient and offset.
    /// </summary>
    internal struct NormalQuadric
    {
        // M = sum over channels of w (g,d)(g,d)^T, X/Y/Z = w (g,d) per channel, S = sum w.
        public double M00, M01, M02, M03, M11, M12, M13, M22, M23, M33;
        public double4 X, Y, Z;
        public double S;

        /// <summary>
        /// A mesh normal as the term reads it. One with a non-finite component reads as zero, a full unit from every unit
        /// normal, so the vertex carrying it is priced as unlike its neighbours' shading: in a quadric it would evaluate to a
        /// non-finite error, read as free, and pass to every vertex that absorbs it.
        /// </summary>
        public static double3 Readable(float3 n) => math.all(math.isfinite(n)) ? (double3)n : double3.zero;

        /// <summary>One triangle's field at the given weight: channel c is gx/gy/gz[c] . p + d[c].</summary>
        public static NormalQuadric FromField(double3 gx, double3 gy, double3 gz, double3 d, double weight)
        {
            var q = new NormalQuadric { S = weight };
            q.X = weight * new double4(gx, d.x);
            q.Y = weight * new double4(gy, d.y);
            q.Z = weight * new double4(gz, d.z);
            q.Channel(new double4(gx, d.x), weight);
            q.Channel(new double4(gy, d.y), weight);
            q.Channel(new double4(gz, d.z), weight);
            return q;
        }

        void Channel(double4 h, double w)
        {
            M00 += w * h.x * h.x; M01 += w * h.x * h.y; M02 += w * h.x * h.z; M03 += w * h.x * h.w;
            M11 += w * h.y * h.y; M12 += w * h.y * h.z; M13 += w * h.y * h.w;
            M22 += w * h.z * h.z; M23 += w * h.z * h.w;
            M33 += w * h.w * h.w;
        }

        public double Evaluate(double3 p, double3 n)
        {
            var hx = M00 * p.x + M01 * p.y + M02 * p.z + M03;
            var hy = M01 * p.x + M11 * p.y + M12 * p.z + M13;
            var hz = M02 * p.x + M12 * p.y + M22 * p.z + M23;
            var hw = M03 * p.x + M13 * p.y + M23 * p.z + M33;
            var quad = p.x * hx + p.y * hy + p.z * hz + hw;
            var h = new double4(p, 1.0);
            var lin = n.x * math.dot(X, h) + n.y * math.dot(Y, h) + n.z * math.dot(Z, h);
            return math.max(quad - 2.0 * lin + math.dot(n, n) * S, 0.0);
        }

        public void Add(in NormalQuadric q)
        {
            M00 += q.M00; M01 += q.M01; M02 += q.M02; M03 += q.M03; M11 += q.M11; M12 += q.M12; M13 += q.M13;
            M22 += q.M22; M23 += q.M23; M33 += q.M33;
            X += q.X; Y += q.Y; Z += q.Z; S += q.S;
        }
    }

    /// <summary>A vertex's quadric in one live frame's configuration.</summary>
    internal struct FrameQuadric
    {
        public int Frame;
        public Quadric Q;
    }

    /// <summary>A vertex's source-surface normal in one live frame's configuration.</summary>
    internal struct FrameNormal
    {
        public int Frame;
        public float3 N;
    }
}
