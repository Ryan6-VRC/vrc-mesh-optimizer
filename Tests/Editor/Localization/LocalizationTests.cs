using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Inspector;
using Ryan6Vrc.MeshOptimizer.Pipeline;
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

        // A placeholder one language drops loses that value for its readers, and one it adds that the English lacks throws.
        [Test]
        public void EveryJapaneseString_TakesTheSamePlaceholders_AsItsEnglish()
        {
            var en = Entries("en-us.po");
            var ja = Entries("ja-jp.po");
            string Placeholders(string s) => string.Join(",", Regex.Matches(s, @"\{\d+\}").Cast<Match>().Select(m => m.Value).Distinct().OrderBy(p => p));

            Assert.That(en.Keys.Where(k => ja.ContainsKey(k) && Placeholders(en[k]) != Placeholders(ja[k])), Is.Empty);
        }

        // A stray brace makes string.Format throw, which loses the build's summary from the console.
        [Test]
        public void EveryString_InEitherLanguage_Formats()
        {
            var args = Enumerable.Range(0, 16).Select(i => (object)i).ToArray();
            var bad = new[] { "en-us.po", "ja-jp.po" }.SelectMany(file => Entries(file).Where(e =>
            {
                try { string.Format(e.Value, args); return false; }
                catch (System.FormatException) { return true; }
            }).Select(e => $"{file} {e.Key}"));

            Assert.That(bad, Is.Empty);
        }

        // Keys built in code rather than drawn by the inspector: the stage a renderer failed at, and the summary's first line.
        // Their English in the file is the English the code falls back to when the localizer has no string, so a build logs
        // the same line either way.
        [Test]
        public void EveryStageAndSummaryKey_HasTheEnglishItsCodeFallsBackTo()
        {
            var en = Entries("en-us.po");
            var fallbacks = System.Enum.GetValues(typeof(PipelineStage)).Cast<PipelineStage>()
                .ToDictionary(DiagnosticKeys.Stage, s => s.ToString())
                .Concat(OptimizationReport.SummaryEnglish);

            Assert.That(fallbacks.Where(f => !en.TryGetValue(f.Key, out var s) || s != f.Value).Select(f => f.Key), Is.Empty);
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

        // The inspector's own keys: its headings, the preset names and the preview's lines. Each is a constant in
        // InspectorKeys or a preset's key, so none can be drawn without an entry here.
        [Test]
        public void EveryInspectorKey_HasAnEnglishString()
        {
            var en = Entries("en-us.po");
            var keys = typeof(InspectorKeys).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue())
                .Concat(System.Enum.GetValues(typeof(TolerancePreset)).Cast<TolerancePreset>().Select(TolerancePresets.Key))
                .ToList();

            Assert.That(keys, Is.Not.Empty);
            Assert.That(keys.Where(k => !en.ContainsKey(k)), Is.Empty);
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
