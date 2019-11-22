using System;
using UnityEditor;

namespace NugetForUnity
{
	public class UnityAssetProcessor: AssetPostprocessor
	{
		private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets,
		                                           string[] movedFromAssetPaths)
		{
			if (Array.IndexOf(importedAssets, "Assets/packages.config") < 0) return;

			// If Any code file is changed as well then recompile will take care of everything
			if (Array.Exists(importedAssets, s => s.EndsWith(".cs")) ||
			    Array.Exists(deletedAssets, s => s.EndsWith(".cs")) ||
			    Array.Exists(movedAssets, s => s.EndsWith(".cs"))) return;

			NugetHelper.ReloadPackagesConfig();
			NugetHelper.Restore();
		}
	}
}