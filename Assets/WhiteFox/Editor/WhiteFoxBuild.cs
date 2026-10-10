// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace WhiteFox.Editor
{
    /// <summary>Build entry points (menu and batch mode: -executeMethod WhiteFox.Editor.WhiteFoxBuild.BuildWindows).</summary>
    public static class WhiteFoxBuild
    {
        [MenuItem("WhiteFox/Build Windows player")]
        public static void BuildWindows()
        {
            var options = new BuildPlayerOptions
            {
                scenes = new[] { "Assets/Scenes/SampleScene.unity" },
                locationPathName = "Builds/Windows/WhiteFox.exe",
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            };
            var report = BuildPipeline.BuildPlayer(options);
            Debug.Log("WhiteFox build: " + report.summary.result + " (" + report.summary.totalErrors + " errors)");
            if (Application.isBatchMode && report.summary.result != BuildResult.Succeeded) EditorApplication.Exit(1);
        }
    }
}
