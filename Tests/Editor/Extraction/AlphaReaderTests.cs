using System.Collections.Generic;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Extraction;
using Ryan6Vrc.MeshOptimizer.Runtime;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Tests.Extraction
{
    // A see-through occluder that counts as solid deletes triangles a viewer can see through it; an opaque one
    // that counts as see-through only keeps triangles that could have gone. These pin both directions.
    public class AlphaReaderTests
    {
        readonly List<Object> _made = new List<Object>();

        [TearDown]
        public void DestroyMade()
        {
            foreach (var o in _made) Object.DestroyImmediate(o);
            _made.Clear();
        }

        [TestCase(2000, "Opaque", false)]
        [TestCase(2000, "", false)]
        [TestCase(2449, "Opaque", false)]
        [TestCase(2450, "Opaque", true)]
        [TestCase(2460, "TransparentCutout", true)]
        [TestCase(3000, "Transparent", true)]
        [TestCase(2000, "TransparentCutout", true)]
        public void SeeThrough_FromTheAlphaTestQueueOn_OrANonOpaqueRenderType(int queue, string renderType, bool expected)
        {
            Assert.That(AlphaReader.CanBeSeeThrough(queue, renderType), Is.EqualTo(expected));
        }

        static AlphaInputs Lil(int mode, float scale = 1f, float offset = 0f) => new AlphaInputs
        {
            Formula = AlphaFormula.LilToon, ColorAlpha = 1f, MaskMode = mode, MaskScale = scale, MaskOffset = offset,
        };

        [TestCase(1, 0.8f, 0.3f, 0.3f)]
        [TestCase(2, 0.8f, 0.5f, 0.4f)]
        [TestCase(3, 0.8f, 0.5f, 1f)]
        [TestCase(4, 0.8f, 0.5f, 0.3f)]
        [TestCase(0, 0.8f, 0.1f, 0.8f)]
        public void LilToonMaskModes_ReplaceMultiplyAddSubtract(int mode, float main, float mask, float expected)
        {
            Assert.That(AlphaReader.Combine(Lil(mode), main, mask), Is.EqualTo(expected).Within(1e-6f));
        }

        [Test]
        public void LilToonMask_IsScaledAndOffsetThenClamped_AfterColorAlpha()
        {
            var p = Lil(2, scale: 2f, offset: -0.5f);
            p.ColorAlpha = 0.5f;
            // mask 0.5 * 2 - 0.5 = 0.5; alpha 1 * 0.5 (color) * 0.5 (mask).
            Assert.That(AlphaReader.Combine(p, 1f, 0.5f), Is.EqualTo(0.25f).Within(1e-6f));
            // mask 0.1 * 2 - 0.5 clamps to 0.
            Assert.That(AlphaReader.Combine(p, 1f, 0.1f), Is.EqualTo(0f));
        }

        [Test]
        public void PoiyomiMask_InvertsAfterOffsetting_AndIgnoreTextureAlphaFloorsTheMain()
        {
            var p = new AlphaInputs
            {
                Formula = AlphaFormula.Poiyomi, ColorAlpha = 1f, MaskMode = 2, MaskScale = 1f, MaskOffset = 0.2f, MaskInvert = true,
            };
            // Inverted: mask = 1 - saturate(0.7 - 0.2) = 0.5.
            Assert.That(AlphaReader.Combine(p, 1f, 0.7f), Is.EqualTo(0.5f).Within(1e-6f));

            p.MaskMode = 0;
            p.MainAlphaFloor = 1f;
            Assert.That(AlphaReader.Combine(p, 0f, 0f), Is.EqualTo(1f), "ignore texture alpha makes the main alpha 1");
        }

        Texture2D Pattern()
        {
            var texture = new Texture2D(4, 4, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Point };
            _made.Add(texture);
            var pixels = new Color32[16];
            for (var i = 0; i < 16; i++) pixels[i] = new Color32(255, 255, 255, (byte)(i * 17));
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }

        Material Standard(int queue)
        {
            var material = new Material(Shader.Find("Standard"));
            _made.Add(material);
            material.renderQueue = queue;
            return material;
        }

        [Test]
        public void TransparentMaterial_IsSampled_ThroughTheGpu_AsTexelAlphaTimesColorAlpha()
        {
            var material = Standard(3000);
            material.SetTexture("_MainTex", Pattern());
            material.SetColor("_Color", new Color(1, 1, 1, 0.5f));

            var surface = AlphaReader.Read(material, OcclusionMode.Automatic);

            Assert.That(surface.Occludes, Is.True);
            Assert.That(surface.Alpha, Is.Not.Null);
            Assert.That((surface.Alpha.Width, surface.Alpha.Height), Is.EqualTo((4, 4)));
            for (var i = 0; i < 16; i++)
                Assert.That(surface.Alpha.Alpha[i], Is.EqualTo(i * 17 / 255f * 0.5f).Within(1f / 255f), $"texel {i}");
            // Texel (1, 2) holds 9 * 17; UV at its centre, and one tile over, sample it.
            Assert.That(surface.Alpha.Sample(new Vector2(1.5f / 4, 2.5f / 4)), Is.EqualTo(9 * 17 / 255f * 0.5f).Within(1f / 255f));
            Assert.That(surface.Alpha.Sample(new Vector2(1 + 1.5f / 4, 2.5f / 4)), Is.EqualTo(9 * 17 / 255f * 0.5f).Within(1f / 255f));
        }

        [Test]
        public void OpaqueMaterial_IsSolid_WithoutSampling()
        {
            var material = Standard(2000);
            material.SetTexture("_MainTex", Pattern());

            var surface = AlphaReader.Read(material, OcclusionMode.Automatic);

            Assert.That(surface.Occludes, Is.True);
            Assert.That(surface.Alpha, Is.Null);
        }

        [Test]
        public void Overrides_BeatTheMaterial()
        {
            var transparent = Standard(3000);
            var opaque = Standard(2000);

            var forced = AlphaReader.Read(transparent, OcclusionMode.ForceOpaque);
            Assert.That((forced.Occludes, forced.Alpha), Is.EqualTo((true, (AlphaMap)null)));
            Assert.That(AlphaReader.Read(opaque, OcclusionMode.NeverOccludes).Occludes, Is.False);
        }
    }
}
