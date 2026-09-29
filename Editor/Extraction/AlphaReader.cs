using Ryan6Vrc.MeshOptimizer.Runtime;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Extraction
{
    /// <summary>How a shader family combines its alpha inputs.</summary>
    internal enum AlphaFormula
    {
        /// <summary>_MainTex alpha times _Color alpha, times _AlphaMask red where the shader has one.</summary>
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
        public readonly TextureWrapMode Wrap;
        /// <summary>The material's _Cutoff, 0 when it has none. Below it an alpha-tested surface is discarded.</summary>
        public readonly float Cutoff;

        public AlphaMap(int width, int height, float[] alpha, Vector4 mainST, TextureWrapMode wrap, float cutoff)
        {
            Width = width;
            Height = height;
            Alpha = alpha;
            MainST = mainST;
            Wrap = wrap;
            Cutoff = cutoff;
        }

        /// <summary>The alpha at a mesh UV0, nearest texel.</summary>
        public float Sample(Vector2 uv0)
        {
            var u = Wrapped(uv0.x * MainST.x + MainST.z);
            var v = Wrapped(uv0.y * MainST.y + MainST.w);
            var x = Mathf.Min((int)(u * Width), Width - 1);
            var y = Mathf.Min((int)(v * Height), Height - 1);
            return Alpha[y * Width + x];
        }

        float Wrapped(float t) => Wrap switch
        {
            TextureWrapMode.Clamp => Mathf.Clamp01(t),
            TextureWrapMode.Mirror => Mathf.PingPong(t, 1f),
            TextureWrapMode.MirrorOnce => Mathf.Clamp01(Mathf.Abs(t)),
            _ => t - Mathf.Floor(t),
        };
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

            var inputs = ReadInputs(material);
            var main = material.HasProperty("_MainTex") ? material.GetTexture("_MainTex") : null;
            var mainST = material.HasProperty("_MainTex")
                ? ScaleOffset(material, "_MainTex")
                : new Vector4(1, 1, 0, 0);
            var mask = inputs.MaskMode != 0 && material.HasProperty("_AlphaMask") ? material.GetTexture("_AlphaMask") : null;

            // A setup this reader cannot place in one UV square counts as see-through everywhere, which only ever
            // keeps triangles that could have gone.
            if (!Supported(material, inputs, mainST, main, mask)) return OccluderSurface.Clear;

            var size = main != null ? main : mask;
            var width = size != null ? Mathf.Min(size.width, MaxResolution) : 1;
            var height = size != null ? Mathf.Min(size.height, MaxResolution) : 1;
            var mainPixels = main != null ? ReadPixels(main, width, height) : null;
            var maskPixels = mask != null ? ReadPixels(mask, width, height) : null;

            var alpha = new float[width * height];
            for (var i = 0; i < alpha.Length; i++)
            {
                var a = mainPixels != null ? mainPixels[i].a / 255f : 1f;
                var r = maskPixels != null ? maskPixels[i].r / 255f : 1f;
                alpha[i] = Combine(inputs, a, r);
            }
            var cutoff = material.HasProperty("_Cutoff") ? material.GetFloat("_Cutoff") : 0f;
            var wrap = main != null ? main.wrapModeU : TextureWrapMode.Repeat;
            return new OccluderSurface(true, new AlphaMap(width, height, alpha, mainST, wrap, cutoff));
        }

        internal static AlphaInputs ReadInputs(Material m)
        {
            var p = new AlphaInputs
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
            }
            else if (m.HasProperty("_MainAlphaMaskMode"))
            {
                p.Formula = AlphaFormula.Poiyomi;
                p.MaskMode = (int)m.GetFloat("_MainAlphaMaskMode");
                p.MaskScale = Float(m, "_AlphaMaskBlendStrength", 1f);
                p.MaskOffset = Float(m, "_AlphaMaskValue", 0f);
                p.MaskInvert = Float(m, "_AlphaMaskInvert", 0f) != 0f;
                p.MainAlphaFloor = Float(m, "_MainIgnoreTexAlpha", 0f);
            }
            else if (m.HasProperty("_AlphaMask"))
            {
                p.Formula = AlphaFormula.Plain;
                p.MaskMode = 2;
            }
            return p;
        }

        static bool Supported(Material m, in AlphaInputs p, Vector4 mainST, Texture main, Texture mask)
        {
            if (main != null && main.dimension != UnityEngine.Rendering.TextureDimension.Tex2D) return false;
            if (mask != null && mask.dimension != UnityEngine.Rendering.TextureDimension.Tex2D) return false;
            if (p.Formula == AlphaFormula.Poiyomi && Float(m, "_MainTexUV", 0f) != 0f) return false;
            // A scrolling or rotating main texture has no one alpha per mesh UV.
            if (Vector(m, "_MainTex_ScrollRotate") != Vector4.zero || Vector(m, "_MainTexPan") != Vector4.zero) return false;
            if (p.MaskMode == 0 || mask == null) return true;

            if (m.GetTextureScale("_AlphaMask") != Vector2.one || m.GetTextureOffset("_AlphaMask") != Vector2.zero) return false;
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
