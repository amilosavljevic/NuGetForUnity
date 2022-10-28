using System.IO;
using System.Text.RegularExpressions;
using Nordeus.Nuget.Utility;
using UnityEditor;

namespace NugetForUnity
{
    internal class SymLinkCodeLinker : IPackageCodeLinker
    {
        public bool IsLinked(NugetPackage package)
        {
            var packagePath = GetPackageLinkedFolderPath(package);
            if (SymbolicLink.Exists(packagePath))
                return true;
            
            var packageSources = Path.Combine(packagePath, "lib");
            if (SymbolicLink.Exists(packageSources))
                return true;

            var packageEditorSources = Path.Combine(packagePath, "Editor");
            return SymbolicLink.Exists(packageEditorSources);
        }

        public void LinkCode(NugetPackage package)
        {
            var packagePath = GetPackageLinkedFolderPath(package);
            var sourcePath = GetSourceProjPath(package);
			if (string.IsNullOrEmpty(sourcePath)) return;
            
			var sourceFolderName = Path.GetFileName(sourcePath);
			if (sourceFolderName == "Source" || sourceFolderName == "Editor") sourcePath = Path.GetDirectoryName(sourcePath);
			if (string.IsNullOrEmpty(sourcePath)) return;

			var sourcesDir = Path.Combine(sourcePath, "Source");
			var editorDir = Path.Combine(sourcePath, "Editor");
			var sourcesDirExists = Directory.Exists(sourcesDir);
			var editorDirExists = Directory.Exists(editorDir);
			if (!sourcesDirExists && !editorDirExists)
			{
				EditorUtility.DisplayDialog("Error", $"No Source dir found under {sourcePath}", "OK");
				return;
			}
			
			var spath = Path.Combine(packagePath, "Source");
			if (!Directory.Exists(spath)) spath = Path.Combine(packagePath, "Editor");
			if (!Directory.Exists(spath)) spath = Path.Combine(packagePath, "Content");
			var libpath = Path.Combine(packagePath, "lib");
			if (Directory.Exists(spath))
			{
				// If Source dir exists in package than we want to replace the whole package folder with a link
				var packageParentPath = Path.GetDirectoryName(packagePath);
				if (packageParentPath == null) return; // Should never happen
				var packageDirName = Path.GetFileName(packagePath);

				var hidePath = Path.Combine(packageParentPath, "." + packageDirName);

				Directory.Move(packagePath, hidePath);

				SymbolicLink.Create(packagePath, sourcePath);
			}
			else
			{
				// Otherwise we want to replace lib subfolder with a link to Source folder
				if (Directory.Exists(libpath))
				{
					Directory.Move(libpath, Path.Combine(packagePath, ".lib"));
					if (sourcesDirExists) SymbolicLink.Create(libpath, sourcesDir);
					else if (File.Exists(libpath + ".meta"))
					{
						File.Move(libpath + ".meta", Path.Combine(packagePath, ".lib.meta"));
					}
				}

				// And if it exists also replace Editor folder with a link
				var path = Path.Combine(packagePath, "Editor");
				if (Directory.Exists(path)) Directory.Move(path, Path.Combine(packagePath, ".Editor"));
				if (editorDirExists) SymbolicLink.Create(path, editorDir);
				else if (File.Exists(path + ".meta"))
				{
					File.Move(path + ".meta", Path.Combine(packagePath, ".Editor.meta"));
				}
			}

			AssetDatabase.Refresh();
        }

        public void UnlinkCode(NugetPackage package)
        {
            var packagePath = GetPackageLinkedFolderPath(package);
            var packageSources = packagePath;
            var packageEditorSources = Path.Combine(packagePath, "Editor");

            var sourcesExists = SymbolicLink.Exists(packagePath);
            if (!sourcesExists)
            {
                packageSources = Path.Combine(packagePath, "lib");
                sourcesExists = SymbolicLink.Exists(packageSources);
            }
            var sourcesEditorExists = SymbolicLink.Exists(packageEditorSources);

            NugetHelper.UnlinkSource(sourcesExists, packageSources, sourcesEditorExists, packageEditorSources, package, packagePath);
            AssetDatabase.Refresh();
        }
        
        private static string GetSourceProjPath(NugetPackage package)
        {
            var openFolderTitle = $"Select source folder of {package.Id} package";
            var parts = new Regex("/browse/?|/tree/master/?").Split(package.ProjectUrl, 2);

            var subfolder = "";
            var projName = Path.GetFileName(parts[0]);
            if (projName == null) return EditorUtility.OpenFolderPanel(openFolderTitle, "", "");

            if (parts.Length > 1) subfolder = parts[1];

            var pathToSearch = Path.GetDirectoryName(Directory.GetCurrentDirectory());
            if (pathToSearch == null) return EditorUtility.OpenFolderPanel(openFolderTitle, "", "");

            for (var i = 0; i < 2; i++)
            {
                var newPath = Path.Combine(Path.Combine(pathToSearch, projName), subfolder);
                if (Directory.Exists(newPath))
                {
                    var subPath = Path.Combine(newPath, Path.GetFileName(newPath)); // In case of csproj subfolder named same as sln folder
                    if (Directory.Exists(subPath)) return subPath;
                    return newPath;
                }

                pathToSearch = Path.GetDirectoryName(pathToSearch);
                if (pathToSearch == null) break;
            }

            return EditorUtility.OpenFolderPanel(openFolderTitle, "", "");
        }


        private static string GetPackageLinkedFolderPath(NugetPackage package) =>
            Path.Combine(NugetHelper.NugetConfigFile.RepositoryPath, $"{package.Id}.{package.Version}");

    }
}