using System.Collections;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEditor.SceneManagement;

namespace Caelix.Tests
{
    public class HostSingletonTests
    {
        [Test]
        public void LookupBeforeAwake_DoesNotInitializeOrFindDisabledComponents()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject("singleton-early-lookup");
            try
            {
                var host = root.AddComponent<CaelixHost>();
                Assert.That(CaelixHost.Current, Is.SameAs(host));
                Assert.That(host.Server, Is.Null);
                Assert.That(host.Client, Is.Null);
                host.enabled = false;
                Assert.That(CaelixHost.Current, Is.Null);
                host.enabled = true;
                Assert.That(CaelixHost.Current, Is.SameAs(host));
                Assert.That(host.Server, Is.Null);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
            Assert.That(CaelixHost.Current, Is.Null);
        }

        [UnityTest]
        public IEnumerator DuplicateCannotInitializeOrTick_AndReplacementCanResume()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            float oldScale = Time.timeScale;
            float oldStep = Time.fixedDeltaTime;
            var first = new GameObject("singleton-owner");
            var second = new GameObject("singleton-duplicate");
            try
            {
                Time.timeScale = 0;
                Assert.That(CaelixHost.Current, Is.Null, "Lookup must not create a host.");
                var owner = first.AddComponent<CaelixHost>();
                var originalServer = owner.Server;
                LogAssert.Expect(LogType.Error, new Regex("another CaelixHost is enabled"));
                var duplicate = second.AddComponent<CaelixHost>();
                Assert.That(duplicate.enabled, Is.False);
                Assert.That(duplicate.Server, Is.Null);
                Assert.That(duplicate.Client, Is.Null);
                duplicate.Step();
                Assert.That(duplicate.Server, Is.Null);
                Assert.That(CaelixHost.Current, Is.SameAs(owner));
                Assert.Throws<System.InvalidOperationException>(() => duplicate.Save("unused.cxw"));

                owner.enabled = false;
                Assert.That(CaelixHost.Current, Is.Null, "Disabled components must be excluded from lookup.");
                uint tick = originalServer.TickIndex;
                owner.Step();
                Assert.That(originalServer.TickIndex, Is.EqualTo(tick));
                owner.enabled = true;
                Assert.That(owner.Server, Is.SameAs(originalServer));
                owner.Step();
                Assert.That(originalServer.TickIndex, Is.EqualTo(tick + 1));

                Object.DestroyImmediate(first);
                Assert.That(owner.Server, Is.Null);
                Assert.That(owner.Client, Is.Null);
                Assert.That(CaelixHost.Current, Is.Null);
                duplicate.enabled = true;
                Assert.That(CaelixHost.Current, Is.SameAs(duplicate));
                Assert.That(duplicate.Server, Is.Not.Null);
                Assert.That(duplicate.Server, Is.Not.SameAs(originalServer));
                duplicate.Step();
                Assert.That(duplicate.Server.TickIndex, Is.EqualTo(1));
                var scene = UnityEngine.SceneManagement.SceneManager.CreateScene("singleton-lifetime");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(second, scene);
                yield return UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(scene);
                Assert.That(duplicate == null, Is.True, "The host must follow its scene lifetime.");
                Assert.That(duplicate.Server, Is.Null);
            }
            finally
            {
                if (first != null) Object.DestroyImmediate(first);
                if (second != null) Object.DestroyImmediate(second);
                Time.timeScale = oldScale;
                Time.fixedDeltaTime = oldStep;
            }
            Assert.That(CaelixHost.Current, Is.Null);
            yield return new ExitPlayMode();
        }
    }
}
