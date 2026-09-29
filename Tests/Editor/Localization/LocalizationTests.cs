using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Runtime;
using UnityEditor;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Tests.Localization
{
    // A key missing from one language shows the English string, or the raw key, to a user reading the other.
    public class LocalizationTests
    {
        const string Folder = "Packages/com.ryan6vrc.mesh-optimizer/Editor/Localization/";

        static Dictionary<string, string> Entries(string file)
        {
            var text = File.ReadAllText(Path.GetFullPath(Folder + file));
            return Regex.Matches(text, "msgid \"(.*)\"\\s*\\nmsgstr \"(.*)\"")
                .Cast<Match>()
                .Where(m => m.Groups[1].Value != "")
                .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        }

        [Test]
        public void EveryEnglishKey_HasAJapaneseString_AndNoOtherKeys()
        {
            var en = Entries("en-us.po");
            var ja = Entries("ja-jp.po");

            Assert.That(ja.Keys, Is.EquivalentTo(en.Keys));
            Assert.That(ja.Where(e => string.IsNullOrWhiteSpace(e.Value)).Select(e => e.Key), Is.Empty);
        }

        [Test]
        public void EveryComponentField_HasALabel()
        {
            var en = Entries("en-us.po");
            var fields = typeof(AvatarMeshOptimizer).GetFields().Select(f => $"MeshOptimizer:{f.Name}")
                .Concat(typeof(RendererSettings).GetFields().Select(f => $"RendererSettings:{f.Name}"))
                .Concat(typeof(MaterialOcclusion).GetFields().Select(f => $"MaterialOcclusion:{f.Name}"))
                .Concat(System.Enum.GetNames(typeof(OcclusionMode)).Select(n => $"OcclusionMode:{n}"))
                .Concat(System.Enum.GetNames(typeof(TargetKind)).Select(n => $"TargetKind:{n}"));

            Assert.That(fields.Where(k => !en.ContainsKey(k)), Is.Empty);
        }

        [Test]
        public void BothFiles_ImportAsLocalizationAssets_InTheirLanguage()
        {
            var en = AssetDatabase.LoadAssetAtPath<LocalizationAsset>(Folder + "en-us.po");
            var ja = AssetDatabase.LoadAssetAtPath<LocalizationAsset>(Folder + "ja-jp.po");

            Assert.That(en, Is.Not.Null);
            Assert.That(ja, Is.Not.Null);
            Assert.That(ja.GetLocalizedString("MeshOptimizer:decimate"), Is.EqualTo("ポリゴンを削減する"));
            Assert.That(en.GetLocalizedString("MeshOptimizer:decimate"), Is.EqualTo("Decimate"));
        }
    }
}
