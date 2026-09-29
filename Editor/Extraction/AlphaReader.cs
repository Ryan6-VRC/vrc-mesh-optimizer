using Ryan6Vrc.MeshOptimizer.Runtime;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Extraction
{
    /// <summary>How a shader family combines its alpha inputs.</summary>
    internal enum AlphaFormula
    {
        /// <summary>Unity's Standard and legacy transparent shaders: _MainTex alpha times _Color alpha.</summary>
        Plain,
        /// <summary>lilToon: _AlphaMaskMode, _AlphaMaskScale, _AlphaMaskValue, the mask sampled at the main texture's UV.</summary>
        LilToon,
        /// <summary>Poiyomi: _MainAlphaMaskMode, _AlphaMaskBlendStrength, _AlphaMaskValue, _AlphaMaskInvert, _MainIgnoreTexAlpha.</summary>
        Poiyomi,
    }

    /// <summary>A material's alpha inputs, as its shader reads them.</summary>
    internal struct AlphaInputs
    {
        public AlphaFormula Formula;
        public float ColorAlpha;
        /// <summary>Poiyomi's "ignore texture alpha": the main texture's alpha is at least this.</summary>
        public float MainAlphaFloor;
        /// <summary>0 off, 1 replace, 2 multiply, 3 add, 4 subtract; the same in lilToon and Poiyomi.</summary>
        public int MaskMode;
        public float MaskScale;
        public float MaskOffset;
        public bool MaskInvert;
    }

    /// <summary>
    /// A material's alpha over its main texture's UV square, with the transform from mesh UV0 into that square.
    /// Row-major, first row at v = 0, texel centres at (i + 0.5) / size.
    /// </summary>
    internal sealed class AlphaMap
    {
        public readonly int Width;
        public readonly int Height;
        public readonly float[] Alpha;
        /// <summary>The main texture's scale (xy) and offset (zw), applied to UV0 before sampling.</summary>
        public readonly Vector4 MainST;
        public readonly TextureWrapMode WrapU;
        public readonly TextureWrapMode WrapV;
        /// <summary>
        /// A sample is solid where its alpha is at least this: the material's _Cutoff when it only alpha-tests,
        /// 1 when it may blend, since a blended surface below full alpha shows what is behind it.
        /// </summary>
        public readonly float SolidFrom;

        public AlphaMap(int width, int height, float[] alpha, Vector4 mainST, TextureWrapMode wrapU, TextureWrapMode wrapV, float solidFrom)
        {
            Width = width;
            Height = height;
            Alpha = alpha;
            MainST = mainST;
            WrapU = wrapU;
            WrapV = wrapV;
            SolidFrom = solidFrom;
        }

        /// <summary>
        /// The alpha at a mesh UV0: the least of the four texels a bilinear sample there blends, so filtering can
        /// only make a sample read less solid than the shader sees it, never more.
        /// </summary>
        public float Sample(Vector2 uv0)
        {
            var fx = (uv0.x * MainST.x + MainST.z) * Width - 0.5f;
            var fy = (uv0.y * MainST.y + MainST.w) * Height - 0.5f;
            var x0 = Mathf.FloorToInt(fx);
            var y0 = Mathf.FloorToInt(fy);
            var least = 1f;
            for (var dy = 0; dy < 2; dy++)
            for (var dx = 0; dx < 2; dx++)
            {
                var x = Wrapped(x0 + dx, Width, WrapU);
                var y = Wrapped(y0 + dy, Height, WrapV);
                least = Mathf.Min(least, Alpha[y * Width + x]);
            }
            return least;
        }

        public bool IsSolid(Vector2 uv0) => Sample(uv0) >= SolidFrom;

        static int Wrapped(int i, int n, TextureWrapMode mode)
        {
            switch (mode)
            {
                case TextureWrapMode.Clamp:
                    return Mathf.Clamp(i, 0, n - 1);
                case TextureWrapMode.Mirror:
                {
                    var period = ((i % (2 * n)) + 2 * n) % (2 * n);
                    return period < n ? period : 2 * n - 1 - period;
                }
                case TextureWrapMode.MirrorOnce:
                {
                    var m = i < 0 ? -i - 1 : i;
                    return Mathf.Min(m, n - 1);
                }
                default:
                    return ((i % n) + n) % n;
            }
        }
    }

    /// <summary>
    /// What a material does to a ray that hits it. <see cref="Occludes"/> false lets every ray through; an
    /// occluding surface with a null <see cref="Alpha"/> stops every ray; otherwise a ray stops only where the
    /// map is solid.
    /// </summary>
    internal readonly struct OccluderSurface
    {
        public readonly bool Occludes;
        public readonly AlphaMap Alpha;

        public OccluderSurface(bool occludes, AlphaMap alpha)
        {
            Occludes = occludes;
            Alpha = alpha;
        }

        public static OccluderSurface Solid => new OccluderSurface(true, null);
        public static OccluderSurface Clear => new OccluderSurface(false, null);
    }

    /// <summary>
    /// Decides whether a material can be see-through and, when it can, reads its alpha without touching any
    /// texture's import settings: each texture is drawn into a temporary RenderTexture and read back.
    /// </summary>
    internal static class AlphaReader
    {
        /// <summary>Unity's AlphaTest queue. Anything drawn from here on may discard or blend.</summary>
        public const int AlphaTestQueue = 2450;
        /// <summary>The largest side of a stored map; a larger texture is shrunk keeping each block's least alpha.</summary>
        public const int MaxResolution = 1024;

        public static bool CanBeSeeThrough(int renderQueue, string renderType) =>
            renderQueue >= AlphaTestQueue || !(string.IsNullOrEmpty(renderType) || renderType == "Opaque");

        /// <summary>The alpha a shader computes from its main texture's alpha and its mask's red channel.</summary>
        public static float Combine(in AlphaInputs p, float mainAlpha, float maskRed)
        {
            var alpha = Mathf.Max(mainAlpha, p.MainAlphaFloor) * p.ColorAlpha;
            if (p.MaskMode == 0) return alpha;

            float mask;
            if (p.Formula == AlphaFormula.Poiyomi)
            {
                mask = Mathf.Clamp01(maskRed * p.MaskScale + (p.MaskInvert ? -p.MaskOffset : p.MaskOffset));
                if (p.MaskInvert) mask = 1f - mask;
            }
            else
            {
                mask = Mathf.Clamp01(maskRed * p.MaskScale + p.MaskOffset);
            }

            return p.MaskMode switch
            {
                1 => mask,
                2 => alpha * mask,
                3 => Mathf.Clamp01(alpha + mask),
                4 => Mathf.Clamp01(alpha - mask),
                _ => alpha,
            };
        }

        public static OccluderSurface Read(Material material, OcclusionMode mode)
        {
            if (mode == OcclusionMode.NeverOccludes || material == null) return OccluderSurface.Clear;
            if (mode == OcclusionMode.ForceOpaque) return OccluderSurface.Solid;
            if (!CanBeSeeThrough(material.renderQueue, material.GetTag("RenderType", true, ""))) return OccluderSurface.Solid;

            // A shader whose alpha math this reader does not know, or a setup it cannot place in one UV square,
            // counts as see-through everywhere, which only ever keeps triangles that could have gone.
            if (!TryReadInputs(material, out var inputs)) return OccluderSurface.Clear;
            var main = material.HasProperty("_MainTex") ? material.GetTexture("_MainTex") : null;
            var mainST = material.HasProperty("_MainTex") ? ScaleOffset(material, "_MainTex") : new Vector4(1, 1, 0, 0);
            var mask = inputs.MaskMode != 0 && material.HasProperty("_AlphaMask") ? material.GetTexture("_AlphaMask") : null;
            if (!Supported(material, inputs, mainST, main, mask)) return OccluderSurface.Clear;

            // Both textures are read at the larger one's full size, then shrunk keeping each block's least alpha,
            // so no transparent texel is averaged away.
            var width = Mathf.Max(main != null ? main.width : 1, mask != null ? mask.width : 1);
            var height = Mathf.Max(main != null ? main.height : 1, mask != null ? mask.height : 1);
            var mainPixels = main != null ? ReadPixels(main, width, height) : null;
            var maskPixels = mask != null ? ReadPixels(mask, width, height) : null;
            var alpha = new float[width * height];
            for (var i = 0; i < alpha.Length; i++)
            {
                var a = mainPixels != null ? mainPixels[i].a / 255f : 1f;
                var r = maskPixels != null ? maskPixels[i].r / 255f : 1f;
                alpha[i] = Combine(inputs, a, r);
            }
            alpha = Shrink(alpha, ref width, ref height);

            var wrapSource = main != null ? main : mask;
            var wrapU = wrapSource != null ? wrapSource.wrapModeU : TextureWrapMode.Repeat;
            var wrapV = wrapSource != null ? wrapSource.wrapModeV : TextureWrapMode.Repeat;
            return new OccluderSurface(true, new AlphaMap(width, height, alpha, mainST, wrapU, wrapV, SolidFrom(material)));
        }

        /// <summary>
        /// A material that only alpha-tests is solid from its _Cutoff; anything that may blend is solid only at full
        /// alpha. Standard and Poiyomi name cutout as _Mode 1; lilToon and Unity's legacy shaders name it in the shader.
        /// </summary>
        internal static float SolidFrom(Material m)
        {
            var cutoutOnly = Float(m, "_Mode", -1f) == 1f || m.shader.name.Contains("Cutout");
            return cutoutOnly && m.HasProperty("_Cutoff") ? m.GetFloat("_Cutoff") : 1f;
        }

        /// <summary>Shrinks a map to at most <see cref="MaxResolution"/> a side, each texel the least of its block.</summary>
        internal static float[] Shrink(float[] alpha, ref int width, ref int height)
        {
            var fx = (width + MaxResolution - 1) / MaxResolution;
            var fy = (height + MaxResolution - 1) / MaxResolution;
            if (fx == 1 && fy == 1) return alpha;
            var w = (width + fx - 1) / fx;
            var h = (height + fy - 1) / fy;
            var shrunk = new float[w * h];
            for (var i = 0; i < shrunk.Length; i++) shrunk[i] = 1f;
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var o = y / fy * w + x / fx;
                shrunk[o] = Mathf.Min(shrunk[o], alpha[y * width + x]);
            }
            width = w;
            height = h;
            return shrunk;
        }

        /// <summary>Unity's own shaders that take alpha from _MainTex and _Color alone.</summary>
        static bool IsPlainShader(string name) =>
            name == "Standard" || name == "Standard (Specular setup)" || name == "Autodesk Interactive" ||
            name.StartsWith("Unlit/Transparent") || name.StartsWith("Legacy Shaders/Transparent/");

        /// <summary>
        /// A material's alpha inputs, when its shader is one whose alpha this reader can compute: false for any other
        /// shader, and for a lilToon or Poiyomi material with any other term on that can lower its alpha
        /// (<see cref="LilToonLowersAlphaElsewhere"/>, <see cref="PoiyomiLowersAlphaElsewhere"/>).
        /// </summary>
        internal static bool TryReadInputs(Material m, out AlphaInputs p)
        {
            p = new AlphaInputs
            {
                ColorAlpha = m.HasProperty("_Color") ? m.GetColor("_Color").a : 1f,
                MaskScale = 1f,
            };
            if (m.HasProperty("_AlphaMaskMode"))
            {
                p.Formula = AlphaFormula.LilToon;
                p.MaskMode = (int)m.GetFloat("_AlphaMaskMode");
                p.MaskScale = Float(m, "_AlphaMaskScale", 1f);
                p.MaskOffset = Float(m, "_AlphaMaskValue", 0f);
                return !LilToonLowersAlphaElsewhere(m);
            }
            if (m.HasProperty("_MainAlphaMaskMode"))
            {
                p.Formula = AlphaFormula.Poiyomi;
                p.MaskMode = (int)m.GetFloat("_MainAlphaMaskMode");
                p.MaskScale = Float(m, "_AlphaMaskBlendStrength", 1f);
                p.MaskOffset = Float(m, "_AlphaMaskValue", 0f);
                p.MaskInvert = Float(m, "_AlphaMaskInvert", 0f) != 0f;
                p.MainAlphaFloor = Float(m, "_MainIgnoreTexAlpha", 0f);
                return !PoiyomiLowersAlphaElsewhere(m);
            }
            p.Formula = AlphaFormula.Plain;
            return IsPlainShader(m.shader.name);
        }

        /// <summary>
        /// Whether a lilToon material has a term on, besides main texture, colour and alpha mask, that can lower the alpha
        /// it draws, as lilToon's fragment code applies them (<c>Shader/Includes/lil_common_frag.hlsl</c> and
        /// <c>lil_common_frag_alpha.hlsl</c>): the second or third main colour in an alpha mode other than add, dissolve,
        /// distance fade toward a colour below full alpha, and dither.
        /// </summary>
        internal static bool LilToonLowersAlphaElsewhere(Material m) =>
            On(m, "_UseMain2ndTex") && ModeLowers(Float(m, "_Main2ndTexAlphaMode", 0f)) ||
            On(m, "_UseMain3rdTex") && ModeLowers(Float(m, "_Main3rdTexAlphaMode", 0f)) ||
            Vector(m, "_DissolveParams").x != 0f ||
            Vector(m, "_DistanceFade").z != 0f && m.HasProperty("_DistanceFadeColor") && m.GetColor("_DistanceFadeColor").a < 1f ||
            On(m, "_UseDither");

        /// <summary>lilToon's alpha modes: 1 replace, 2 multiply, 3 add, 4 subtract. Only adding never lowers.</summary>
        static bool ModeLowers(float mode) => mode != 0f && mode != 3f;

        /// <summary>
        /// Whether a Poiyomi material has a term on, besides main texture, colour and alpha mask, that can lower the alpha
        /// it draws, as Poiyomi Toon's fragment code applies them: the alpha options (a negative alpha mod, a global mask,
        /// distance, Fresnel and angular alpha, AudioLink alpha, dithering); vertex colour alpha; a back face that replaces
        /// alpha; dissolve; a decal whose alpha blend can lower it (any but add and max); a matcap that overrides alpha or
        /// multiplies its intensity into it; the AudioLink spectrum decal, flipbook, pathing, depth, Voronoi and Truchet
        /// alpha overrides; and a rim light that multiplies into alpha. A module's own toggle must be on for its terms to
        /// apply. Only an unlocked material is read at all: locking bakes these properties into the shader, and a locked
        /// material has no <c>_MainAlphaMaskMode</c> to be read by. Not modelled: Poiyomi modules that move where the main
        /// texture is sampled rather than writing alpha, which are stochastic sampling (<c>_MainTexStochastic</c>), parallax
        /// (<c>_PoiParallax</c>) and video effects (<c>_VideoEffectsEnable</c>); a material with one on is read as if its
        /// main texture were sampled at the mesh's UV.
        /// </summary>
        internal static bool PoiyomiLowersAlphaElsewhere(Material m)
        {
            if (Float(m, "_AlphaMod", 0f) < 0f || Float(m, "_AlphaGlobalMask", 0f) > 0f) return true;
            foreach (var option in new[] { "_AlphaDistanceFade", "_AlphaFresnel", "_AlphaAngular", "_AlphaAudioLinkEnabled", "_AlphaDithering" })
                if (On(m, option)) return true;
            if (On(m, "_MainVertexColoringEnabled") && Float(m, "_MainUseVertexColorAlpha", 0f) > 0f) return true;
            if (On(m, "_BackFaceEnabled") && On(m, "_BackFaceReplaceAlpha")) return true;
            if (On(m, "_EnableDissolve")) return true;
            foreach (var decal in new[] { "", "1", "2", "3" })
            {
                // Poiyomi's decal alpha blends: 1 replace, 2 multiply, 3 add, 4 subtract, 5 min, 6 max.
                var blend = Float(m, "_DecalOverrideAlpha" + decal, 0f);
                if (On(m, "_DecalEnabled" + decal) && blend != 0f && blend != 3f && blend != 6f) return true;
            }
            foreach (var (matcap, enable) in new[] { ("_Matcap", "_MatcapEnable"), ("_Matcap2", "_Matcap2Enable"), ("_Matcap3", "_Matcap3Enable"), ("_Matcap4", "_Matcap4Enable") })
            {
                if (!On(m, enable)) continue;
                // An intensity blended into alpha: 0 adds, 1 multiplies.
                if (Float(m, matcap + "AlphaOverride", 0f) > 0f || On(m, matcap + "ApplyToAlphaEnabled") && Float(m, matcap + "ApplyToAlphaBlendType", 0f) == 1f) return true;
            }
            if (On(m, "_EnableALDecal") && Float(m, "_ALDecalControlsAlpha", 0f) > 0f) return true;
            if (On(m, "_EnableFlipbook") && On(m, "_FlipbookAlphaControlsFinalAlpha")) return true;
            if (On(m, "_EnablePathing") && On(m, "_PathingOverrideAlpha")) return true;
            if (On(m, "_EnableTouchGlow") && On(m, "_DepthAlphaToggle")) return true;
            if (On(m, "_VoronoiEnabled") && On(m, "_VoronoiAffectsMaterialAlpha")) return true;
            if (On(m, "_EnableTruchet") && On(m, "_TruchetOverrideAlpha")) return true;
            // A rim light's apply-to-alpha: 1 adds, 2 multiplies.
            return On(m, "_EnableRimLighting") && Float(m, "_RimApplyAlpha", 0f) == 2f ||
                   On(m, "_EnableRim2Lighting") && Float(m, "_Rim2ApplyAlpha", 0f) == 2f;
        }

        static bool On(Material m, string name) => Float(m, name, 0f) != 0f;

        static bool Supported(Material m, in AlphaInputs p, Vector4 mainST, Texture main, Texture mask)
        {
            if (main != null && main.dimension != UnityEngine.Rendering.TextureDimension.Tex2D) return false;
            if (mask != null && mask.dimension != UnityEngine.Rendering.TextureDimension.Tex2D) return false;
            if (p.Formula == AlphaFormula.Poiyomi && Float(m, "_MainTexUV", 0f) != 0f) return false;
            // A scrolling or rotating texture has no one alpha per mesh UV.
            if (Vector(m, "_MainTex_ScrollRotate") != Vector4.zero || Vector(m, "_MainTexPan") != Vector4.zero) return false;
            if (p.MaskMode == 0 || mask == null) return true;

            if (Vector(m, "_AlphaMaskPan") != Vector4.zero) return false;
            if (m.GetTextureScale("_AlphaMask") != Vector2.one || m.GetTextureOffset("_AlphaMask") != Vector2.zero) return false;
            // One map carries one pair of wrap modes.
            if (main != null && (mask.wrapModeU != main.wrapModeU || mask.wrapModeV != main.wrapModeV)) return false;
            if (p.Formula == AlphaFormula.Poiyomi)
            {
                // Poiyomi samples the mask at the mesh's UV rather than the main texture's, so the two share a
                // square only when the main texture is untransformed.
                if (Float(m, "_AlphaMaskUV", 0f) != 0f) return false;
                if (mainST != new Vector4(1, 1, 0, 0)) return false;
            }
            return true;
        }

        static float Float(Material m, string name, float fallback) => m.HasProperty(name) ? m.GetFloat(name) : fallback;

        static Vector4 Vector(Material m, string name) => m.HasProperty(name) ? m.GetVector(name) : Vector4.zero;

        static Vector4 ScaleOffset(Material m, string name)
        {
            var scale = m.GetTextureScale(name);
            var offset = m.GetTextureOffset(name);
            return new Vector4(scale.x, scale.y, offset.x, offset.y);
        }

        /// <summary>
        /// A texture's texels as the shader samples them, scaled to the given size, read through the GPU so a
        /// texture that is not readable needs no import change. Linear throughout, so values arrive as sampled.
        /// </summary>
        internal static Color32[] ReadPixels(Texture texture, int width, int height)
        {
            var target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            var readback = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            try
            {
                Graphics.Blit(texture, target);
                RenderTexture.active = target;
                readback.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                return readback.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                Object.DestroyImmediate(readback);
            }
        }
    }
}
