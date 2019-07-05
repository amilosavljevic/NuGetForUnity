using JetBrains.Annotations;
using Nordeus.Nuget.Utility;

namespace NugetForUnity
{
	using System.Collections.Generic;
	using System.IO;
	using System.Linq;
	using UnityEditor;
	using UnityEngine;

	/// <summary>
	/// Represents a custom editor inside the Unity editor that allows easy editting of a .nuspec file.
	/// </summary>
	public class NuspecEditor : EditorWindow
	{
		/// <summary>
		/// The full filepath to the .nuspec file that is being edited.
		/// </summary>
		private string filepath;

		/// <summary>
		/// The NuspecFile that was loaded from the .nuspec file.
		/// </summary>
		private NuspecFile nuspec;

		/// <summary>
		/// True if the dependencies list is expanded in the GUI.  False if it is collapsed.
		/// </summary>
		private bool dependenciesExpanded = true;

		/// <summary>
		/// The API key used to verify an acceptable package being pushed to the server.
		/// </summary>
		private string apiKey = string.Empty;

		[MenuItem("Assets/NuGet/Create Package", false, 2000)]
		protected static void CreateNewPackage()
		{
			var filepath = Application.dataPath;

			if (Selection.activeObject != null && Selection.activeObject != Selection.activeGameObject)
			{
				var selectedFile = AssetDatabase.GetAssetPath(Selection.activeObject);
				filepath = selectedFile.Substring("Assets/".Length);
				filepath = Path.Combine(Application.dataPath, filepath);
			}

			if (!Directory.Exists(filepath) && File.Exists(filepath))
			{
				filepath = Path.GetDirectoryName(filepath);
			}

			if (!Directory.Exists(filepath))
			{
				EditorUtility.DisplayDialog("Error", "You must select a valid folder where you want link to the package to be created.", "OK");
				return;
			}

			var savePath = EditorUtility.SaveFolderPanel("Create and select actual package folder", "", "");
			if (string.IsNullOrEmpty(savePath)) return;

			Directory.CreateDirectory(Path.Combine(savePath, "Source"));
			File.WriteAllText(Path.Combine(savePath, "Readme.md"), "#Package readme");
			if (!savePath.StartsWith(Application.dataPath))
			{
				NugetWindow.InstallPreCommitHook();
				filepath = Path.Combine(filepath, Path.GetFileName(savePath));
				SymbolicLink.Create(filepath, savePath);
				savePath = filepath;
			}

			CreateNuspecAndOpenEditor(savePath);
		}

		private static void CreateNuspecAndOpenEditor(string filepath)
		{
			if (!string.IsNullOrEmpty(Path.GetExtension(filepath)))
			{
				filepath = Path.GetDirectoryName(filepath);
			}

			if (filepath == null) return;

			var packageName = Path.GetFileName(filepath);
			filepath = Path.Combine(filepath, packageName + ".nuspec");

			var file = new NuspecFile
			{
				Id = "company_name." + packageName.ToLower(),
				Title = packageName,
				Version = "0.0.1",
				Authors = "Your Name",
				Owners = "Your Name",
				LicenseUrl = "http://your_license_url_here",
				ProjectUrl = "http://your_project_url_here",
				Description = "A description of what this packages is and does.",
				ReleaseNotes = "Notes for this specific release",
				Copyright = "Copyright 2017",
				IconUrl = "https://www.nuget.org/Content/Images/packageDefaultIcon-50x50.png"
			};
			file.Save(filepath);

			AssetDatabase.Refresh();

			// select the newly created .nuspec file
			var dataPath = Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length);
			Selection.activeObject = AssetDatabase.LoadMainAssetAtPath(filepath.Replace(dataPath, string.Empty));

			// automatically display the editor with the newly created .nuspec file
			DisplayNuspecEditor();
		}

		/// <summary>
		/// Creates a new MyPackage.nuspec file.
		/// </summary>
		[MenuItem("Assets/NuGet/Create Nuspec File", false, 2000)]
		protected static void CreateNuspecFile()
		{
			var filepath = Application.dataPath;

			if (Selection.activeObject != null && Selection.activeObject != Selection.activeGameObject)
			{
				var selectedFile = AssetDatabase.GetAssetPath(Selection.activeObject);
				filepath = selectedFile.Substring("Assets/".Length);
				filepath = Path.Combine(Application.dataPath, filepath);
			}

			CreateNuspecAndOpenEditor(filepath);
		}

		/// <summary>
		/// Opens the .nuspec file editor.
		/// </summary>
		[MenuItem("Assets/NuGet/Open Nuspec Editor", false, 2000)]
		protected static void DisplayNuspecEditor()
		{
			var nuspecEditor = GetWindow<NuspecEditor>();
			nuspecEditor.Reload();
		}

		/// <summary>
		/// Validates the opening of the .nuspec file editor.
		/// </summary>
		[MenuItem("Assets/NuGet/Open Nuspec Editor", true, 2000)]
		protected static bool DisplayNuspecEditorValidation()
		{
			var isNuspec = false;

			var defaultAsset = Selection.activeObject as DefaultAsset;
			if (defaultAsset != null)
			{
				var filepath = AssetDatabase.GetAssetPath(defaultAsset);
				var dataPath = Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length);
				filepath = Path.Combine(dataPath, filepath);

				isNuspec = Path.GetExtension(filepath) == ".nuspec";
			}

			return isNuspec;
		}

		/// <summary>
		/// Called when enabling the window.
		/// </summary>
		[UsedImplicitly]
		private void OnFocus()
		{
			Reload();
		}

		/// <summary>
		/// Reloads the .nuspec file when the selection changes.
		/// </summary>
		[UsedImplicitly]
		private void OnSelectionChange()
		{
			Reload();
		}

		/// <summary>
		/// Reload the currently selected asset as a .nuspec file.
		/// </summary>
		protected void Reload()
		{
			var defaultAsset = Selection.activeObject as DefaultAsset;
			if (defaultAsset != null)
			{
				var assetFilepath = AssetDatabase.GetAssetPath(defaultAsset);
				var dataPath = Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length);
				assetFilepath = Path.Combine(dataPath, assetFilepath);

				var isNuspec = Path.GetExtension(assetFilepath) == ".nuspec";

				if (isNuspec)
				{
					filepath = assetFilepath;
					nuspec = NuspecFile.Load(filepath);
					titleContent = new GUIContent(Path.GetFileNameWithoutExtension(filepath));

					// force a repaint
					Repaint();
				}
			}
		}

		/// <summary>
		/// Use the Unity GUI to draw the controls.
		/// </summary>
		protected void OnGUI()
		{
			if (nuspec == null)
			{
				Reload();
			}

			if (nuspec == null)
			{
				titleContent = new GUIContent("[NO NUSPEC]");
				EditorGUILayout.LabelField("There is no .nuspec file selected.");
			}
			else
			{
				EditorGUIUtility.labelWidth = 100;
				nuspec.Id = EditorGUILayout.TextField(new GUIContent("ID", "The id of the package."), nuspec.Id);
				nuspec.Title = EditorGUILayout.TextField(new GUIContent("Title", "The name of the package."), nuspec.Title);
				nuspec.Version = EditorGUILayout.TextField(new GUIContent("Version", "The semantic version of the package."), nuspec.Version);
				nuspec.Authors = EditorGUILayout.TextField(new GUIContent("Authors", "The authors of the package."), nuspec.Authors);
				nuspec.Owners = EditorGUILayout.TextField(new GUIContent("Owners", "The owners of the package."), nuspec.Owners);
				nuspec.LicenseUrl = EditorGUILayout.TextField(new GUIContent("License URL", "The URL for the license of the package."), nuspec.LicenseUrl);
				nuspec.ProjectUrl = EditorGUILayout.TextField(new GUIContent("Project URL", "The URL of the package project."), nuspec.ProjectUrl);
				nuspec.IconUrl = EditorGUILayout.TextField(new GUIContent("Icon URL", "The URL for the icon of the package."), nuspec.IconUrl);
				nuspec.RequireLicenseAcceptance = EditorGUILayout.Toggle(new GUIContent("Require License Acceptance", "Does the package license need to be accepted before use?"), nuspec.RequireLicenseAcceptance);
				nuspec.Description = EditorGUILayout.TextField(new GUIContent("Description", "The description of the package."), nuspec.Description);
				nuspec.ReleaseNotes = EditorGUILayout.TextField(new GUIContent("Release Notes", "The release notes for this specific version of the package."), nuspec.ReleaseNotes);
				nuspec.Copyright = EditorGUILayout.TextField(new GUIContent("Copyright", "The copyright details for the package."), nuspec.Copyright);
				nuspec.Tags = EditorGUILayout.TextField(new GUIContent("Tags", "The space-delimited list of tags and keywords that describe the package and aid discoverability of packages through search and filtering."), nuspec.Tags);

				dependenciesExpanded = EditorGUILayout.Foldout(dependenciesExpanded, new GUIContent("Dependencies", "The list of NuGet packages that this packages depends on."));

				if (dependenciesExpanded)
				{
					EditorGUILayout.BeginHorizontal();
					{
						GUILayout.Space(50);

						// automatically fill in the dependencies based upon the "root" packages currently installed in the project
						if (GUILayout.Button(new GUIContent("Automatically Fill Dependencies", "Populates the list of dependencies with the \"root\" NuGet packages currently installed in the project.")))
						{
							NugetHelper.UpdateInstalledPackages();
							var installedPackages = NugetHelper.InstalledPackages.ToList();

							// default all packages to being roots
							var roots = new List<NugetPackage>(installedPackages);

							// remove a package as a root if another package is dependent on it
							foreach (var package in installedPackages)
							{
								foreach (var dependency in package.Dependencies)
								{
									roots.RemoveAll(p => p.Id == dependency.Id);
								}
							}

							// remove all existing dependencies from the .nuspec
							nuspec.Dependencies.Clear();

							nuspec.Dependencies = roots.Cast<NugetPackageIdentifier>().ToList();
						}
					}
					EditorGUILayout.EndHorizontal();

					// display the dependencies
					NugetPackageIdentifier toDelete = null;
					foreach (var dependency in nuspec.Dependencies)
					{
						EditorGUILayout.BeginHorizontal();
						GUILayout.Space(75);
						var prevLabelWidth = EditorGUIUtility.labelWidth;
						EditorGUIUtility.labelWidth = 50;
						dependency.Id = EditorGUILayout.TextField(new GUIContent("ID", "The ID of the dependency package."), dependency.Id);
						EditorGUILayout.EndHorizontal();

						//int oldSeletedIndex = IndexOf(ref existingComponents, dependency.Id);
						//int newSelectIndex = EditorGUILayout.Popup("Name", oldSeletedIndex, existingComponents);
						//if (oldSeletedIndex != newSelectIndex)
						//{
						//    dependency.Name = existingComponents[newSelectIndex];
						//}

						EditorGUILayout.BeginHorizontal();
						GUILayout.Space(75);
						dependency.Version = EditorGUILayout.TextField(new GUIContent("Version", "The version number of the dependency package. (specify ranges with =><)"), dependency.Version);
						EditorGUILayout.EndHorizontal();

						EditorGUILayout.BeginHorizontal();
						{
							GUILayout.Space(75);

							if (GUILayout.Button("Remove " + dependency.Id))
							{
								toDelete = dependency;
							}
						}
						EditorGUILayout.EndHorizontal();

						EditorGUILayout.Separator();

						EditorGUIUtility.labelWidth = prevLabelWidth;
					}

					if (toDelete != null)
					{
						nuspec.Dependencies.Remove(toDelete);
					}

					EditorGUILayout.BeginHorizontal();
					{
						GUILayout.Space(50);

						if (GUILayout.Button("Add Dependency"))
						{
							nuspec.Dependencies.Add(new NugetPackageIdentifier());
						}
					}
					EditorGUILayout.EndHorizontal();
				}

				EditorGUILayout.Separator();

				if (GUILayout.Button($"Save {Path.GetFileName(filepath)}"))
				{
					nuspec.Save(filepath);
				}

				EditorGUILayout.Separator();

				if (GUILayout.Button($"Pack {Path.GetFileNameWithoutExtension(filepath)}.nupkg"))
				{
					NugetHelper.Pack(filepath);
				}

				EditorGUILayout.Separator();

				apiKey = EditorGUILayout.TextField(new GUIContent("API Key", "The API key to use when pushing the package to the server"), apiKey);

				if (GUILayout.Button("Push to Server"))
				{
					NugetHelper.Push(nuspec, filepath, apiKey);
				}
			}
		}
	}
}