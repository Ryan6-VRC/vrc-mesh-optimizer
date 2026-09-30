using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Extraction;
using Ryan6Vrc.MeshOptimizer.Inspector;
using Ryan6Vrc.MeshOptimizer.Occlusion;
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
            // Alpha rises with the texel index, so the least of the four texels a bilinear sample between texels
            // (1, 2) and (2, 3) blends is texel (1, 2), index 9; one tile over reads the same.
            Assert.That(surface.Alpha.Sample(new Vector2(2f / 4, 3f / 4)), Is.EqualTo(9 * 17 / 255f * 0.5f).Within(1f / 255f));
            Assert.That(surface.Alpha.Sample(new Vector2(1 + 2f / 4, 3f / 4)), Is.EqualTo(9 * 17 / 255f * 0.5f).Within(1f / 255f));
            // A Fade or Transparent Standard material blends, so only full alpha is solid.
            Assert.That(surface.Alpha.SolidFrom, Is.EqualTo(1f));
        }

        [Test]
        public void CutoutMaterial_IsSolidFromItsCutoff()
        {
            var material = Standard(2450);
            material.SetFloat("_Mode", 1f);
            material.SetFloat("_Cutoff", 0.3f);
            material.SetTexture("_MainTex", Pattern());

            var surface = AlphaReader.Read(material, OcclusionMode.Automatic);

            Assert.That(surface.Alpha.SolidFrom, Is.EqualTo(0.3f));
        }

        [Test]
        public void TransparentMaterialOfAnUnknownShader_NeverOccludes()
        {
            // Its alpha may come from anything; counting it solid could delete what shows through it.
            var material = new Material(Shader.Find("Sprites/Default"));
            _made.Add(material);
            material.renderQueue = 3000;
            material.SetTexture("_MainTex", Pattern());

            Assert.That(AlphaReader.Read(material, OcclusionMode.Automatic).Occludes, Is.False);
        }

        [Test]
        public void ShrinkingALargeMap_KeepsASingleTransparentTexel()
        {
            var width = 2048;
            var height = 2;
            var alpha = new float[width * height];
            for (var i = 0; i < alpha.Length; i++) alpha[i] = 1f;
            alpha[1001] = 0f;

            var shrunk = AlphaReader.Shrink(alpha, ref width, ref height);

            Assert.That((width, height), Is.EqualTo((1024, 2)));
            Assert.That(shrunk[500], Is.EqualTo(0f));
            Assert.That(shrunk.Count(a => a == 0f), Is.EqualTo(1));
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

        // Each family's properties as its shader declares them, every term that could lower alpha off.
        const string PoiyomiProperties =
            "_MainTex (\"Main\", 2D) = \"white\" {} _Color (\"Color\", Color) = (1, 1, 1, 1) _AlphaMask (\"Mask\", 2D) = \"white\" {} " +
            "_MainAlphaMaskMode (\"Mask Mode\", Int) = 0 _Mode (\"Mode\", Int) = 1 _Cutoff (\"Cutoff\", Float) = 0.5 " +
            "_AlphaMod (\"Mod\", Range(-1, 1)) = 0 _AlphaGlobalMask (\"Global\", Int) = 0 _AlphaGlobalMaskBlendType (\"Blend\", Int) = 2 _AlphaDistanceFade (\"x\", Float) = 0 " +
            "_AlphaFresnel (\"x\", Float) = 0 _AlphaAngular (\"x\", Float) = 0 _AlphaAudioLinkEnabled (\"x\", Float) = 0 _AlphaDithering (\"x\", Float) = 0 " +
            "_MainVertexColoringEnabled (\"x\", Float) = 0 _MainUseVertexColorAlpha (\"x\", Range(0, 1)) = 0 " +
            "_BackFaceEnabled (\"x\", Float) = 0 _BackFaceReplaceAlpha (\"x\", Float) = 0 _EnableDissolve (\"x\", Float) = 0 " +
            "_DecalEnabled (\"x\", Float) = 0 _DecalOverrideAlpha (\"x\", Float) = 0 _DecalEnabled3 (\"x\", Float) = 0 _DecalOverrideAlpha3 (\"x\", Float) = 0 " +
            "_MatcapEnable (\"x\", Float) = 0 _MatcapAlphaOverride (\"x\", Range(0, 1)) = 0 _MatcapApplyToAlphaEnabled (\"x\", Float) = 0 _MatcapApplyToAlphaBlendType (\"x\", Int) = 0 " +
            "_Matcap4Enable (\"x\", Float) = 0 _Matcap4AlphaOverride (\"x\", Range(0, 1)) = 0 " +
            "_EnableALDecal (\"x\", Float) = 0 _ALDecalControlsAlpha (\"x\", Range(0, 1)) = 0 _EnableFlipbook (\"x\", Float) = 0 _FlipbookAlphaControlsFinalAlpha (\"x\", Float) = 0 " +
            "_EnablePathing (\"x\", Float) = 0 _PathingOverrideAlpha (\"x\", Float) = 0 _EnableTouchGlow (\"x\", Float) = 0 _DepthAlphaToggle (\"x\", Float) = 0 " +
            "_VoronoiEnabled (\"x\", Int) = 0 _VoronoiAffectsMaterialAlpha (\"x\", Int) = 0 _EnableTruchet (\"x\", Float) = 0 _TruchetOverrideAlpha (\"x\", Float) = 0 " +
            "_EnableRimLighting (\"x\", Float) = 0 _RimApplyAlpha (\"x\", Int) = 0 _EnableRim2Lighting (\"x\", Float) = 0 _Rim2ApplyAlpha (\"x\", Int) = 0 _BSSEnabled (\"x\", Float) = 0";

        const string LilToonProperties =
            "_MainTex (\"Main\", 2D) = \"white\" {} _Color (\"Color\", Color) = (1, 1, 1, 1) _AlphaMask (\"Mask\", 2D) = \"white\" {} " +
            "_AlphaMaskMode (\"Mask Mode\", Int) = 0 _Cutoff (\"Cutoff\", Float) = 0.5 " +
            "_UseMain2ndTex (\"x\", Int) = 0 _Main2ndTexAlphaMode (\"x\", Int) = 0 _UseMain3rdTex (\"x\", Int) = 0 _Main3rdTexAlphaMode (\"x\", Int) = 0 " +
            "_DissolveParams (\"x\", Vector) = (0, 0, 0.5, 0.1) _DistanceFade (\"x\", Vector) = (0.1, 0.01, 0, 0) _DistanceFadeColor (\"x\", Color) = (0, 0, 0, 1) " +
            "_UseDither (\"x\", Int) = 0";

        Material Family(string shaderName, string properties)
        {
            var source = $"Shader \"{shaderName}\" {{ Properties {{ {properties} }} SubShader {{ Tags {{ \"RenderType\"=\"TransparentCutout\" }} Pass {{ }} }} }}";
            var shader = UnityEditor.ShaderUtil.CreateShaderAsset(source, true);
            _made.Add(shader);
            var material = new Material(shader) { renderQueue = 2450 };
            _made.Add(material);
            return material;
        }

        // A cutout material with an opaque main texture reads solid, until a term the reader does not compute is on in a
        // state that can lower its alpha; then it is see-through everywhere. A state that can only raise alpha, or a term
        // whose module is off, leaves it solid. Each setting is "name=value", a vector's value "x,y,z,w".
        [TestCase("", true, TestName = "{m}(nothing on)")]
        [TestCase("_AlphaMod=-1", false)]
        [TestCase("_AlphaMod=0.5", true)]
        [TestCase("_AlphaGlobalMask=1", false)]
        [TestCase("_AlphaGlobalMask=1;_AlphaGlobalMaskBlendType=5", false)]
        [TestCase("_AlphaGlobalMask=1;_AlphaGlobalMaskBlendType=7", false)]
        [TestCase("_BSSEnabled=1", false)]
        [TestCase("_AlphaGlobalMask=1;_AlphaGlobalMaskBlendType=0", false)]
        [TestCase("_AlphaDistanceFade=1", false)]
        [TestCase("_AlphaFresnel=1", false)]
        [TestCase("_AlphaAngular=1", false)]
        [TestCase("_AlphaAudioLinkEnabled=1", false)]
        [TestCase("_AlphaDithering=1", false)]
        [TestCase("_MainVertexColoringEnabled=1;_MainUseVertexColorAlpha=1", false)]
        [TestCase("_MainVertexColoringEnabled=0;_MainUseVertexColorAlpha=1", true)]
        [TestCase("_BackFaceEnabled=1;_BackFaceReplaceAlpha=1", false)]
        [TestCase("_EnableDissolve=1", false)]
        [TestCase("_DecalEnabled=1;_DecalOverrideAlpha=2", false)]
        [TestCase("_DecalEnabled3=1;_DecalOverrideAlpha3=1", false)]
        [TestCase("_DecalEnabled=1;_DecalOverrideAlpha=3", true)]
        [TestCase("_DecalEnabled=1;_DecalOverrideAlpha=6", true)]
        [TestCase("_DecalEnabled=0;_DecalOverrideAlpha=2", true)]
        [TestCase("_MatcapEnable=1;_MatcapAlphaOverride=1", false)]
        [TestCase("_MatcapEnable=1;_MatcapApplyToAlphaEnabled=1;_MatcapApplyToAlphaBlendType=1", false)]
        [TestCase("_MatcapEnable=1;_MatcapApplyToAlphaEnabled=1;_MatcapApplyToAlphaBlendType=0", true)]
        [TestCase("_Matcap4Enable=1;_Matcap4AlphaOverride=0.5", false)]
        [TestCase("_EnableALDecal=1;_ALDecalControlsAlpha=1", false)]
        [TestCase("_EnableFlipbook=1;_FlipbookAlphaControlsFinalAlpha=1", false)]
        [TestCase("_EnablePathing=1;_PathingOverrideAlpha=1", false)]
        [TestCase("_EnableTouchGlow=1;_DepthAlphaToggle=1", false)]
        [TestCase("_VoronoiEnabled=1;_VoronoiAffectsMaterialAlpha=1", false)]
        [TestCase("_EnableTruchet=1;_TruchetOverrideAlpha=1", false)]
        [TestCase("_EnableRimLighting=1;_RimApplyAlpha=2", false)]
        [TestCase("_EnableRimLighting=1;_RimApplyAlpha=1", true)]
        [TestCase("_EnableRim2Lighting=1;_Rim2ApplyAlpha=2", false)]
        public void APoiyomiTermTheReaderDoesNotCompute_ReadsAsSeeThrough(string settings, bool occludes)
        {
            var material = Family("Test/Poiyomi Alpha", PoiyomiProperties);
            Apply(material, settings);

            var surface = AlphaReader.Read(material, OcclusionMode.Automatic);

            Assert.That(surface.Occludes, Is.EqualTo(occludes));
            if (occludes) Assert.That(surface.Alpha.IsSolid(new Vector2(0.5f, 0.5f)), Is.True, "the opaque main texture reads solid");
        }

        [TestCase("", true, TestName = "{m}(nothing on)")]
        [TestCase("_UseMain2ndTex=1;_Main2ndTexAlphaMode=2", false)]
        [TestCase("_UseMain2ndTex=1;_Main2ndTexAlphaMode=3", true)]
        [TestCase("_UseMain2ndTex=0;_Main2ndTexAlphaMode=1", true)]
        [TestCase("_UseMain3rdTex=1;_Main3rdTexAlphaMode=4", false)]
        [TestCase("_DissolveParams=1,0,0.5,0.1", false)]
        [TestCase("_DistanceFade=0.1,0.01,1,0;_DistanceFadeColor=0,0,0,0", false)]
        [TestCase("_DistanceFade=0.1,0.01,1,0;_DistanceFadeColor=0,0,0,1", true)]
        [TestCase("_UseDither=1", false)]
        public void ALilToonTermTheReaderDoesNotCompute_ReadsAsSeeThrough(string settings, bool occludes)
        {
            var material = Family("Test/lilToon Alpha", LilToonProperties);
            Apply(material, settings);

            var surface = AlphaReader.Read(material, OcclusionMode.Automatic);

            Assert.That(surface.Occludes, Is.EqualTo(occludes));
        }

        // The inspector tells a user what Automatic makes of a material so they can override a wrong reading; a verdict
        // that differs from the build's own classification would send them to override a material the build reads right.
        [Test]
        public void TheInspectorsVerdict_IsTheBuildsClassification_ForEachKind()
        {
            var opaque = Standard(2000);
            var byAlpha = Standard(3000);
            byAlpha.SetTexture("_MainTex", Pattern());
            var seeThrough = new Material(Shader.Find("Sprites/Default")) { renderQueue = 3000 };
            _made.Add(seeThrough);

            foreach (var (material, expected) in new[] { (opaque, VerdictKind.Opaque), (byAlpha, VerdictKind.ByAlpha), (seeThrough, VerdictKind.SeeThrough) })
            {
                var holder = new GameObject(material.name);
                _made.Add(holder);
                var renderer = holder.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                var build = new OccluderMaterials(null).For(renderer, 1)[0];
                var buildKind = build.Mode == Core.Occlusion.OccluderMode.NeverOccludes ? VerdictKind.SeeThrough
                    : build.Alpha != null ? VerdictKind.ByAlpha
                    : VerdictKind.Opaque;

                var verdict = MaterialVerdicts.Of(material);

                Assert.That(buildKind, Is.EqualTo(expected), $"{expected}: the build's classification");
                Assert.That(verdict.Kind, Is.EqualTo(buildKind), $"{expected}: the inspector's verdict");
            }
            // Only the pattern's last texel reaches full alpha, the solid threshold of a blended material.
            Assert.That(MaterialVerdicts.Of(byAlpha).SolidShare, Is.EqualTo(1f / 16f));
        }

        static void Apply(Material material, string settings)
        {
            foreach (var setting in settings.Split(new[] { ';' }, System.StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = setting.Split('=');
                Assert.That(material.HasProperty(parts[0]), $"the test shader declares {parts[0]}");
                var values = parts[1].Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                if (values.Length == 1) material.SetFloat(parts[0], values[0]);
                else material.SetVector(parts[0], new Vector4(values[0], values[1], values[2], values[3]));
            }
        }
    }
}
