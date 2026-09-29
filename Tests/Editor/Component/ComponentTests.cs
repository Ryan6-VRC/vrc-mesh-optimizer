using NUnit.Framework;
using Ryan6Vrc.MeshOptimizer.Runtime;
using UnityEditor;
using UnityEngine;

namespace Ryan6Vrc.MeshOptimizer.Tests.Component
{
    // The class is named apart from its namespace so consumers need no alias; the user must still see
    // "Mesh Optimizer". Unity takes the Inspector title from AddComponentMenu, which this pins.
    public class ComponentTests
    {
        const string Fixture = "Packages/com.ryan6vrc.mesh-optimizer/Tests/Editor/Component/Fixtures/ComponentFixture.prefab";

        [Test]
        public void InspectorTitle_IsMeshOptimizer()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Fixture);
            Assert.That(prefab, Is.Not.Null, "the fixture prefab must import");
            var component = prefab.GetComponent<AvatarMeshOptimizer>();
            Assert.That(component, Is.Not.Null, "the fixture must carry the component");

            Assert.That(ObjectNames.GetInspectorTitle(component), Is.EqualTo("Mesh Optimizer"));
        }

        // A component added fresh targets a tolerance of 1 mm, with a budget of 70,000 ready if the user picks one. A
        // saved component holds its target as the enum's number and keeps it, so the numbers must not move.
        [Test]
        public void AFreshComponent_TargetsATolerance_AndASavedOneKeepsItsTarget()
        {
            var fresh = new GameObject("Fresh");
            var saved = new GameObject("Saved");
            try
            {
                var c = fresh.AddComponent<AvatarMeshOptimizer>();
                Assert.That(c.target, Is.EqualTo(TargetKind.Tolerance));
                Assert.That(c.tolerance, Is.EqualTo(1f));
                Assert.That(c.triangleBudget, Is.EqualTo(70000));

                Assert.That((int)TargetKind.TriangleBudget, Is.EqualTo(0));
                Assert.That((int)TargetKind.Tolerance, Is.EqualTo(1));
                // A component saved under the old defaults, loaded into one that starts from the new.
                c.target = TargetKind.TriangleBudget;
                c.tolerance = 0.5f;
                var json = EditorJsonUtility.ToJson(c);
                Assert.That(json, Does.Contain("\"target\":0"));
                var old = saved.AddComponent<AvatarMeshOptimizer>();
                EditorJsonUtility.FromJsonOverwrite(json, old);
                Assert.That(old.target, Is.EqualTo(TargetKind.TriangleBudget));
                Assert.That(old.tolerance, Is.EqualTo(0.5f));
            }
            finally
            {
                Object.DestroyImmediate(fresh);
                Object.DestroyImmediate(saved);
            }
        }

        // Outlines take no part in hidden-triangle removal unless the user turns them on. A component saved before the
        // setting existed, as the fixture was, holds no value for it and loads with the field's own default, off.
        [Test]
        public void OutlinesShowInside_IsOff_FreshAndForAComponentSavedBeforeIt()
        {
            Assert.That(System.IO.File.ReadAllText(System.IO.Path.GetFullPath(Fixture)), Does.Not.Contain("outlinesShowInside"),
                "the fixture stands for a component saved before the setting");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Fixture);
            Assert.That(prefab.GetComponent<AvatarMeshOptimizer>().outlinesShowInside, Is.False);

            var fresh = new GameObject("Fresh");
            try
            {
                Assert.That(fresh.AddComponent<AvatarMeshOptimizer>().outlinesShowInside, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(fresh);
            }
        }
    }
}
