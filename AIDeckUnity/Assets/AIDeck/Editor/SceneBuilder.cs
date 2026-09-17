using System.IO;
using AIDeck.App;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AIDeck.Editor
{
    /// <summary>
    /// Generates the single scene.
    ///
    /// The scene holds one object with <see cref="AppBootstrap"/> on it and nothing else; the
    /// entire interface and audio graph are built in code at runtime. Generating it rather
    /// than hand-editing YAML means the file is reproducible and its one script reference is
    /// always correct.
    /// </summary>
    public static class SceneBuilder
    {
        public const string ScenePath = "Assets/Scenes/AIDeck.unity";

        [MenuItem("AI Deck/Rebuild Scene")]
        public static void Rebuild()
        {
            var folder = Path.GetDirectoryName(ScenePath);
            if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var root = new GameObject("AI Deck");
            root.AddComponent<AppBootstrap>();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);

            AddToBuildSettings();
            AssetDatabase.SaveAssets();
            Debug.Log($"[AI Deck] Scene written to {ScenePath}.");
        }

        /// <summary>Makes sure the scene is the one and only entry in the build.</summary>
        public static void AddToBuildSettings()
        {
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(ScenePath, true)
            };
        }

        /// <summary>Batch-mode entry point: <c>-executeMethod AIDeck.Editor.SceneBuilder.BuildScene</c>.</summary>
        public static void BuildScene() => Rebuild();
    }
}
