using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Core.Occlusion;
using Ryan6Vrc.MeshOptimizer.Occlusion;
using UnityEditor;
using UnityEngine;
using static Ryan6Vrc.MeshOptimizer.Tests.Integration.AvatarFixture;

namespace Ryan6Vrc.MeshOptimizer.Tests.Integration
{
    // lilToon and Poiyomi draw an outline as a second pass over the same triangles, pushed out along the normal and
    // culled front by default, so what shows of it is its back. A cup inside an always-on bowl, open to the top like a
    // skirt, has its outside covered and its inside facing the open top: drawn back-culled it goes, but its outline
    // draws that inside, so an outlined cup stays. The stand-in shaders declare only the properties and passes each
    // family's own source uses to switch and cull its outline.
    public class OutlineTests
    {
        public enum Family
        {
            /// <summary>lilToon: an outline variant is a shader of its own, named with "Outline", carrying a FORWARD_OUTLINE pass.</summary>
            LilToon,
            /// <summary>A shader named like no family, with a pass named for an outline.</summary>
            OutlinePass,
            /// <summary>Poiyomi 9: one shader, the outline switched by _EnableOutlines, clipping a zero width by default.</summary>
            Poiyomi9,
            /// <summary>Poiyomi 8: an outline shader, switched by _EnableOutlines, its passes unnamed, drawing a zero width by default.</summary>
            Poiyomi8,
            /// <summary>Poiyomi 7: switched by _commentIfZero_EnableOutlinePass, always clipping a zero width.</summary>
            Poiyomi7,
            /// <summary>A locked Poiyomi material: the generated shader keeps the toggle and the pass only when the outline was on.</summary>
            PoiyomiLocked,
        }

        AvatarFixture a;
        Transform chest;

        [SetUp]
        public void SetUp()
        {
            a = new AvatarFixture();
            chest = a.Bone("Armature/Hips/Chest");
        }

        [TearDown]
        public void TearDown() => a.Dispose();

        Material Stand(string shaderName, string properties, params string[] passes)
        {
            var source = $"Shader \"{shaderName}\" {{ Properties {{ {properties} }} SubShader {{ Tags {{ \"RenderType\"=\"Opaque\" }} " +
                         string.Join(" ", passes.Select(p => "Pass { " + p + " }")) + " } }";
            return a.Made(new Material(a.Made(ShaderUtil.CreateShaderAsset(source, true))));
        }

        const string LilProperties =
            "_Cull (\"Cull\", Float) = 2 _OutlineCull (\"Outline Cull\", Float) = 1 " +
            "_OutlineWidth (\"Outline Width\", Range(0, 1)) = 0.08 _OutlineDeleteMesh (\"Delete Mesh\", Float) = 0";

        const string PoiProperties =
            "_Cull (\"Cull\", Float) = 2 _LineWidth (\"Outline Size\", Float) = 1 _OutlineCull (\"Outline Cull\", Float) = 1 " +
            "_OutlineClipAtZeroWidth (\"Clip 0 Width\", Float) = 1 _AudioLinkOutlineSize (\"Size Mod\", Vector) = (0, 0, 0, 0)";

        /// <summary>A back-culled material of the family, drawing an outline or not by the family's own switch.</summary>
        Material Toon(Family family, bool outline)
        {
            switch (family)
            {
                case Family.LilToon:
                    // Every lilToon variant declares the outline's properties; only the outline variants draw it.
                    return outline
                        ? Stand("Hidden/MeshOptimizerTests/lilToonOutline", LilProperties, "Name \"FORWARD\" Cull [_Cull]", "Name \"FORWARD_OUTLINE\" Cull [_OutlineCull]")
                        : Stand("Hidden/MeshOptimizerTests/lilToon", LilProperties, "Name \"FORWARD\" Cull [_Cull]");
                case Family.OutlinePass:
                    return outline
                        ? Stand("Hidden/MeshOptimizerTests/Toon", LilProperties, "Name \"FORWARD\" Cull [_Cull]", "Name \"FORWARD_OUTLINE\" Cull [_OutlineCull]")
                        : Stand("Hidden/MeshOptimizerTests/Toon", LilProperties, "Name \"FORWARD\" Cull [_Cull]");
                case Family.Poiyomi9:
                {
                    var m = Stand("Hidden/MeshOptimizerTests/.poiyomi/Poiyomi Toon", PoiProperties + " _EnableOutlines (\"Enable Outlines\", Float) = 0",
                        "Name \"Base\" Cull [_Cull]", "Name \"Outline\" Cull [_OutlineCull]");
                    m.SetFloat("_EnableOutlines", outline ? 1 : 0);
                    return m;
                }
                case Family.Poiyomi8:
                {
                    var m = Stand("Hidden/MeshOptimizerTests/.poiyomi/Poiyomi Toon Outline",
                        PoiProperties.Replace("_OutlineClipAtZeroWidth (\"Clip 0 Width\", Float) = 1", "_OutlineClipAtZeroWidth (\"Clip 0 Width\", Float) = 0") +
                        " _EnableOutlines (\"Enable Outlines\", Float) = 0",
                        "Cull [_Cull]", "Cull [_OutlineCull]");
                    m.SetFloat("_EnableOutlines", outline ? 1 : 0);
                    return m;
                }
                case Family.Poiyomi7:
                {
                    var m = Stand("Hidden/MeshOptimizerTests/.poiyomi/Poiyomi Toon 7",
                        "_Cull (\"Cull\", Float) = 2 _LineWidth (\"Width\", Float) = 0 _OutlineCull (\"Cull\", Float) = 1 " +
                        "_commentIfZero_EnableOutlinePass (\"Enable Outlines\", Float) = 0",
                        "Name \"Base\" Cull [_Cull]", "Name \"Outline\" Cull [_OutlineCull]");
                    m.SetFloat("_commentIfZero_EnableOutlinePass", outline ? 1 : 0);
                    m.SetFloat("_LineWidth", 1);
                    return m;
                }
                default:
                    // The locker bakes the outline's cull into the pass and keeps the properties block.
                    return outline
                        ? Stand("Hidden/Locked/MeshOptimizerTests/Poiyomi Toon/0123456789abcdef0123456789abcdef", PoiProperties + " _EnableOutlines (\"Enable Outlines\", Float) = 1",
                            "Name \"Base\" Cull Back", "Name \"Outline\" Cull Front")
                        : Stand("Hidden/Locked/MeshOptimizerTests/Poiyomi Toon/0123456789abcdef0123456789abcdef", PoiProperties, "Name \"Base\" Cull Back");
            }
        }

        SkinnedMeshRenderer Cup(Material material) =>
            a.Skinned("Cup", a.Sphere(0.1f, 16, 48, 100f, 180f, name: "cup"), new[] { chest }, material: material);

        /// <summary>The always-on cover: a bowl open at the top, or a closed shell.</summary>
        SkinnedMeshRenderer Cover(bool closed = false, Material material = null, bool inward = false)
        {
            var cover = a.Skinned("Cover", a.Sphere(0.105f, 24, 48, closed ? 0f : 60f, 180f, inward, "cover"), new[] { chest }, material: material);
            a.Optimizer().alwaysOn.Add(cover);
            return cover;
        }

        [Test]
        public void AnOutlinedCup_WhoseFrontIsCoveredAndBackOpen_IsKept([Values] Family family)
        {
            var cup = Cup(Toon(family, true));
            Cover();

            var report = a.Run();

            var r = Of(report, cup);
            Assert.That(r.HiddenRemoved, Is.Zero, "the outline draws the cup's inside, open to the top");
            Assert.That(cup.sharedMesh.triangles.Length, Is.EqualTo(r.HiddenSourceTriangles.Length * 3), "every triangle of the cup is drawn");
        }

        [Test]
        public void TheSameCupWithoutAnOutline_IsRemoved([Values] Family family)
        {
            var cup = Cup(Toon(family, false));
            Cover();

            Assert.That(Of(a.Run(), cup).HiddenRemoved, Is.GreaterThan(0), "back-culled, the cup's covered outside is all that draws");
        }

        // Inside a closed shell the cup's inside is covered too: its back rays cross the cup and meet the shell's far side.
        [Test]
        public void AnOutlinedCup_EnclosedOnBothSides_IsRemoved([Values(Family.LilToon, Family.Poiyomi9)] Family family)
        {
            var cup = Cup(Toon(family, true));
            Cover(closed: true);

            Assert.That(Of(a.Run(), cup).HiddenRemoved, Is.GreaterThan(0));
        }

        // A zero width draws nothing only where the family's source drops it. lilToon deletes a zero-width hull only with
        // _OutlineDeleteMesh; without it the hull lies flush on the surface and still draws its back. Poiyomi 9 clips a
        // zero width while _OutlineClipAtZeroWidth is on, its default, unless AudioLink can widen it; Poiyomi 8 has the
        // same switch off by default; Poiyomi 7 always clips.
        [TestCase(Family.LilToon, 1f, true, TestName = "{m}(lilToon, deleting the mesh)")]
        [TestCase(Family.LilToon, 0f, false, TestName = "{m}(lilToon, not deleting the mesh)")]
        [TestCase(Family.Poiyomi9, 1f, true, TestName = "{m}(Poiyomi 9, clipping)")]
        [TestCase(Family.Poiyomi9, 0f, false, TestName = "{m}(Poiyomi 9, not clipping)")]
        [TestCase(Family.Poiyomi8, 0f, false, TestName = "{m}(Poiyomi 8, by default)")]
        [TestCase(Family.Poiyomi7, -1f, true, TestName = "{m}(Poiyomi 7)")]
        public void AZeroWidthOutline_CountsAsNone_OnlyWhereTheShaderDropsIt(Family family, float drop, bool removed)
        {
            var material = Toon(family, true);
            if (family == Family.LilToon)
            {
                material.SetFloat("_OutlineWidth", 0);
                material.SetFloat("_OutlineDeleteMesh", drop);
            }
            else
            {
                material.SetFloat("_LineWidth", 0);
                if (drop >= 0) material.SetFloat("_OutlineClipAtZeroWidth", drop);
            }
            var cup = Cup(material);
            Cover();

            Assert.That(Of(a.Run(), cup).HiddenRemoved > 0, Is.EqualTo(removed));
        }

        [Test]
        public void AZeroWidthPoiyomiOutline_ThatAudioLinkCanWiden_IsDrawn()
        {
            var material = Toon(Family.Poiyomi9, true);
            material.SetFloat("_LineWidth", 0);
            material.SetVector("_AudioLinkOutlineSize", new Vector4(0, 0.5f, 0, 0));
            var cup = Cup(material);
            Cover();

            Assert.That(Of(a.Run(), cup).HiddenRemoved, Is.Zero);
        }

        // An outline culled back draws its front, the side the main pass already draws, so it adds nothing; culled off it
        // draws both.
        [TestCase(Family.LilToon, 2, true)]
        [TestCase(Family.LilToon, 0, false)]
        [TestCase(Family.Poiyomi9, 2, true)]
        [TestCase(Family.Poiyomi9, 0, false)]
        public void AnOutline_AddsOnlyTheSidesItsCullDraws(Family family, int outlineCull, bool removed)
        {
            var material = Toon(family, true);
            material.SetFloat("_OutlineCull", outlineCull);
            var cup = Cup(material);
            Cover();

            Assert.That(Of(a.Run(), cup).HiddenRemoved > 0, Is.EqualTo(removed));
        }

        // A shader the pipeline cannot read is already taken to draw both sides as a candidate, outline or not, so the
        // outline keeps nothing it would not.
        [Test]
        public void AnUnrecognisedShaderWithOutlineProperties_IsTakenAsDrawingBothSides()
        {
            var cup = Cup(Stand("Hidden/MeshOptimizerTests/Unknown", "_OutlineWidth (\"Outline Width\", Float) = 0.1 _OutlineCull (\"Outline Cull\", Float) = 2", "Cull Back"));
            Cover();

            var r = Of(a.Run(), cup);
            Assert.That(r.HiddenRemoved, Is.Zero);
            Assert.That(r.KeptForOutline, Is.Zero);
        }

        // The report counts what the rule kept. Here nothing moves, nothing the cup covers is deleted and its triangles
        // are too small for lattice samples, so the count is exact: what the same cup loses without its outline.
        [Test]
        public void TheReport_CountsWhatTheOutlineKept()
        {
            var cup = Cup(Toon(Family.LilToon, true));
            var cover = Cover();
            var outlined = a.Run(apply: false);
            cup.sharedMaterial = Toon(Family.LilToon, false);
            var plain = a.Run(apply: false);

            var removed = Of(plain, cup).HiddenRemoved;
            Assert.That(removed, Is.GreaterThan(0));
            Assert.That(Of(outlined, cup).KeptForOutline, Is.EqualTo(removed));
            Assert.That(outlined.KeptForOutline, Is.EqualTo(removed), "the avatar's count is its renderers'");
            Assert.That(outlined.Summary(), Does.Contain($"outlines kept up to {removed} triangles"));
            Assert.That(Of(plain, cup).KeptForOutline, Is.Zero, "without an outline the rule keeps nothing");
            Assert.That(Of(outlined, cover).KeptForOutline, Is.Zero, "the cover's front is open");
        }

        // The reader, one family's way of declaring an outline at a time. An outline culled front draws the back.
        [TestCase(Family.LilToon, true)]
        [TestCase(Family.LilToon, false)]
        [TestCase(Family.OutlinePass, true)]
        [TestCase(Family.OutlinePass, false)]
        [TestCase(Family.Poiyomi9, true)]
        [TestCase(Family.Poiyomi9, false)]
        [TestCase(Family.Poiyomi8, true)]
        [TestCase(Family.Poiyomi8, false)]
        [TestCase(Family.Poiyomi7, true)]
        [TestCase(Family.Poiyomi7, false)]
        [TestCase(Family.PoiyomiLocked, true)]
        [TestCase(Family.PoiyomiLocked, false)]
        public void TheReader_FindsEachFamilysOutline(Family family, bool outline)
        {
            Assert.That(MaterialOutline.SidesOf(Toon(family, outline)), Is.EqualTo(outline ? FaceCulling.Front : (FaceCulling?)null));
        }

        [Test]
        public void TheReader_TakesAnUndeclaredOutlineCullAsBothSides_AndPoiyomisToggleOverItsName()
        {
            var bare = Stand("Hidden/MeshOptimizerTests/Toon", "_Cull (\"Cull\", Float) = 2", "Name \"FORWARD\" Cull [_Cull]", "Name \"OUTLINE\" Cull Front");
            Assert.That(MaterialOutline.SidesOf(bare), Is.EqualTo(FaceCulling.Off));

            // Poiyomi's early-outline shader carries "Outline" in its name; its toggle still decides.
            var early = Stand("Hidden/MeshOptimizerTests/.poiyomi/Poiyomi Toon Outline Early", PoiProperties + " _EnableOutlines (\"Enable Outlines\", Float) = 0",
                "Name \"Outline\" Cull [_OutlineCull]", "Name \"Base\" Cull [_Cull]");
            Assert.That(MaterialOutline.SidesOf(early), Is.Null);
            Assert.That(MaterialOutline.SidesOf(null), Is.Null);
        }

        // lilToon's test for an outline variant is its name, which a variant carries without needing the pass.
        [TestCase("Hidden/lilToonOutline", true)]
        [TestCase("Hidden/lilToonTessellationTwoPassTransparentOutline", true)]
        [TestCase("_lil/[Optional] lilToonOutlineOnlyTransparent", true)]
        [TestCase("Custom/Toon/[Optional] OutlineOnly/Opaque", true)]
        [TestCase("Hidden/lilToonMultiOutline", true)]
        [TestCase("Hidden/lilToonLite", false)]
        [TestCase("_lil/lilToonMulti", false)]
        [TestCase("Outline/lilToon", false)]
        [TestCase("lilToonOutline", false)]
        [TestCase("Custom/Toon/OutlineOnly/Opaque", false)]
        public void TheReader_FollowsLilToonsNameForAnOutlineVariant(string name, bool outline)
        {
            Assert.That(MaterialOutline.IsLilToonOutlineName(name), Is.EqualTo(outline));
            if (!name.Contains("/")) return;
            var named = Stand("Hidden/MeshOptimizerTests/" + name, LilProperties, "Name \"FORWARD\" Cull [_Cull]");
            Assert.That(MaterialOutline.SidesOf(named) != null, Is.EqualTo(outline));
        }

        // As cover, an outlined material blocks as its main pass does. An inward-facing bowl culled back shows only its
        // back to a viewer outside, and its outline, culled front, draws only its back too: read as drawing its back it
        // would block, which deletes what shows through it.
        [TestCase(false, true)]
        [TestCase(true, false)]
        public void AnOutlinedCover_BlocksAsItsMainPassDoes(bool inward, bool hides)
        {
            var body = a.Skinned("Body", a.Sphere(0.1f, 16, 32, name: "body"), new[] { chest });
            Cover(material: Toon(Family.LilToon, true), inward: inward);

            Assert.That(Of(a.Run(), body).HiddenRemoved > 0, Is.EqualTo(hides));
        }
    }
}
