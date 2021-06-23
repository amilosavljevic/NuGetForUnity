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

			// We used to skip this if any code file is changed as well since recompile would take care of everything
			// but compile can fail exactly because we didn't execute Restore so we removed that.

			NugetHelper.ReloadPackagesConfig();
			NugetHelper.Restore();
		}
	}
}