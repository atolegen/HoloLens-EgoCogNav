// EgoCogNavSceneSetup.cs
// Run once from:  Mixed Reality → EgoCogNav → Setup MRTK3 Scene
//
// What it does:
//   1. Removes the old "XR Origin (Mobile AR)" rig (not MRTK3-aware).
//   2. Instantiates the MRTK XR Rig prefab (hand ray, gaze, near-touch, etc.).
//   3. Adds ARSession + ARAnchorManager so AR Foundation anchors still work.
//   4. Adds MRTKInputSimulator for Editor play-mode hand simulation.
//   5. Re-links SpatialAnchorDemo.anchorManager to the new ARAnchorManager.

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace EgoCogNav.Editor
{
    public static class EgoCogNavSceneSetup
    {
        private const string RigPath = "Packages/org.mixedrealitytoolkit.input/Assets/Prefabs/MRTK XR Rig.prefab";
        private const string SimPath = "Packages/org.mixedrealitytoolkit.input/Simulation/Prefabs/MRTKInputSimulator.prefab";

        [MenuItem("Mixed Reality/EgoCogNav/Setup MRTK3 Scene")]
        private static void SetupScene()
        {
            // ── 1. Load MRTK XR Rig prefab ────────────────────────────────────
            var rigPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(RigPath);
            if (rigPrefab == null)
            {
                EditorUtility.DisplayDialog("EgoCogNav Setup — Error",
                    $"MRTK XR Rig prefab not found at:\n{RigPath}\n\n" +
                    "Make sure org.mixedrealitytoolkit.input is installed.",
                    "OK");
                return;
            }

            // ── 2. Remove old rigs ─────────────────────────────────────────────
            foreach (var name in new[] { "XR Origin (Mobile AR)", "XR Origin (AR)", "XR Origin" })
            {
                var old = GameObject.Find(name);
                if (old != null)
                {
                    Debug.Log($"[EgoCogNav] Removing old rig: {old.name}");
                    Undo.DestroyObjectImmediate(old);
                    break;
                }
            }

            // Remove standalone XR Interaction Manager — MRTK XR Rig has its own
            var xrim = GameObject.Find("XR Interaction Manager");
            if (xrim != null)
            {
                Debug.Log("[EgoCogNav] Removing standalone XR Interaction Manager.");
                Undo.DestroyObjectImmediate(xrim);
            }

            // ── 3. Instantiate MRTK XR Rig ────────────────────────────────────
            var rig = (GameObject)PrefabUtility.InstantiatePrefab(rigPrefab);
            Undo.RegisterCreatedObjectUndo(rig, "Add MRTK XR Rig");
            Debug.Log("[EgoCogNav] MRTK XR Rig added.");

            // ── 4. Ensure ARSession is in the scene ───────────────────────────
            if (Object.FindObjectOfType<ARSession>() == null)
            {
                var arSessionGo = new GameObject("AR Session");
                Undo.RegisterCreatedObjectUndo(arSessionGo, "Add AR Session");
                Undo.AddComponent<ARSession>(arSessionGo);
                Debug.Log("[EgoCogNav] ARSession added.");
            }

            // ── 5. Add ARAnchorManager to the MRTK XR Rig ────────────────────
            var anchorMgr = rig.GetComponent<ARAnchorManager>();
            if (anchorMgr == null)
            {
                anchorMgr = Undo.AddComponent<ARAnchorManager>(rig);
                Debug.Log("[EgoCogNav] ARAnchorManager added to MRTK XR Rig.");
            }

            // ── 6. Add MRTKInputSimulator (Editor hand sim) ───────────────────
            if (GameObject.Find("MRTKInputSimulator") == null)
            {
                var simPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(SimPath);
                if (simPrefab != null)
                {
                    var sim = (GameObject)PrefabUtility.InstantiatePrefab(simPrefab);
                    Undo.RegisterCreatedObjectUndo(sim, "Add MRTKInputSimulator");
                    Debug.Log("[EgoCogNav] MRTKInputSimulator added.");
                }
                else
                {
                    Debug.LogWarning($"[EgoCogNav] MRTKInputSimulator not found at: {SimPath}");
                }
            }

            // ── 7. Re-link SpatialAnchorDemo.anchorManager ───────────────────
            var demo = Object.FindObjectOfType<Demo.SpatialAnchorDemo>();
            if (demo != null && anchorMgr != null)
            {
                var so   = new SerializedObject(demo);
                var prop = so.FindProperty("anchorManager");
                if (prop != null)
                {
                    prop.objectReferenceValue = anchorMgr;
                    so.ApplyModifiedProperties();
                    Debug.Log("[EgoCogNav] Linked ARAnchorManager → SpatialAnchorDemo.");
                }
            }

            // ── Done ──────────────────────────────────────────────────────────
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

            EditorUtility.DisplayDialog("EgoCogNav Setup Complete",
                "Scene is ready:\n\n" +
                "✓  MRTK XR Rig  (hand ray, near-touch, gaze)\n" +
                "✓  ARSession\n" +
                "✓  ARAnchorManager\n" +
                "✓  MRTKInputSimulator  (Editor hand sim)\n\n" +
                "In Play Mode, hold  Space  to show the right hand,\n" +
                "Left Shift  for the left hand.\n\n" +
                "Save the scene (Ctrl+S) before building.",
                "Got it");
        }
    }
}
