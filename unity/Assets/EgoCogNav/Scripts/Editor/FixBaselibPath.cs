// FixBaselibPath.cs
// Unity Editor script that patches the generated IL2CPP vcxproj file
// to use the correct ARM64/release baselib path in Unity 2022.3.62f3+
// where the directory structure changed from il2cpp/Release/ to il2cpp/ARM64/release/

using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace EgoCogNav.Editor
{
    public class FixBaselibPath : IPostprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.WSAPlayer)
                return;

            string buildPath = report.summary.outputPath;
            string vcxprojPath = Path.Combine(buildPath,
                "Il2CppOutputProject", "Il2CppOutputProject.vcxproj");

            if (!File.Exists(vcxprojPath))
            {
                Debug.LogWarning($"[FixBaselibPath] vcxproj not found at: {vcxprojPath}");
                return;
            }

            string content = File.ReadAllText(vcxprojPath);

            // Pattern: --baselib-directory=...il2cpp\\Release
            // Replace with: --baselib-directory=...il2cpp\\ARM64\\release
            string pattern = @"(--baselib-directory=""?[^""]*il2cpp\\\\?)Release(""?)";
            string replacement = "$1ARM64\\\\release$2";

            string patched = Regex.Replace(content, pattern, replacement,
                RegexOptions.IgnoreCase);

            if (patched == content)
            {
                // Try alternate pattern
                pattern = @"(--baselib-directory=[^\s""]*il2cpp[/\\]+)Release";
                replacement = "${1}ARM64\\\\release";
                patched = Regex.Replace(content, pattern, replacement,
                    RegexOptions.IgnoreCase);
            }

            if (patched != content)
            {
                File.WriteAllText(vcxprojPath, patched);
                Debug.Log("[FixBaselibPath] Patched Il2CppOutputProject.vcxproj: " +
                          "baselib-directory now points to ARM64/release");
            }
            else
            {
                Debug.LogWarning("[FixBaselibPath] Pattern not found in vcxproj — " +
                                 "may already be correct or structure changed.");
            }
        }
    }
}
