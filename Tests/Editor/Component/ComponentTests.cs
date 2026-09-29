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
    }
}
