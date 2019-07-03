using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace NugetForUnity
{
	/// <summary>
	/// Handles the displaying, editing, and saving of the preferences for NuGet For Unity.
	/// </summary>
	public static class NugetPreferences
	{
		/// <summary>
		/// The current version of NuGet for Unity.
		/// </summary>
		public const string NuGetForUnityVersion = "1.1.9";

		/// <summary>
		/// The current position of the scroll bar in the GUI.
		/// </summary>
		private static Vector2 scrollPosition;

		[SettingsProvider]
		public static SettingsProvider CreateNugetSettingsProvider()
		{
			var provider = new SettingsProvider("Preferences/Nuget", SettingsScope.User)
			{
				label = "NuGet for Unity",
				keywords = new HashSet<string>(new [] {"nuget", "package", "nupkg"}),
				guiHandler = (searchContext) => PreferencesGUI()
			};

			return provider;
		}

		/// <summary>
		/// Draws the preferences GUI inside the Unity preferences window in the Editor.
		/// </summary>
		public static void PreferencesGUI()
		{
			EditorGUILayout.LabelField($"Version: {NuGetForUnityVersion}");

			var conf = NugetHelper.NugetConfigFile;

			conf.InstallFromCache = EditorGUILayout.Toggle("Install From the Cache", conf.InstallFromCache);

			conf.ReadOnlyPackageFiles = EditorGUILayout.Toggle("Read-Only Package Files", conf.ReadOnlyPackageFiles);

			conf.Verbose = EditorGUILayout.Toggle("Use Verbose Logging", conf.Verbose);

			conf.SavedRepositoryPath = EditorGUILayout.TextField("Packages Directory", conf.SavedRepositoryPath);

			conf.AllowUninstallAll = EditorGUILayout.Toggle("Allow Uninstall All", conf.AllowUninstallAll);

			EditorGUILayout.LabelField("Package Sources:");

			scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

			NugetPackageSource sourceToMoveUp = null;
			NugetPackageSource sourceToMoveDown = null;
			NugetPackageSource sourceToRemove = null;

			foreach (var source in conf.PackageSources)
			{
				EditorGUILayout.BeginVertical();
				{
					EditorGUILayout.BeginHorizontal();
					{
						EditorGUILayout.BeginVertical(GUILayout.Width(20));
						{
							GUILayout.Space(10);
							source.IsEnabled = EditorGUILayout.Toggle(source.IsEnabled, GUILayout.Width(20));
						}
						EditorGUILayout.EndVertical();

						EditorGUILayout.BeginVertical();
						{
							source.Name = EditorGUILayout.TextField(source.Name);
							source.SavedPath = EditorGUILayout.TextField(source.SavedPath);
						}
						EditorGUILayout.EndVertical();
					}
					EditorGUILayout.EndHorizontal();

					EditorGUILayout.BeginHorizontal();
					{
						GUILayout.Space(29);
						EditorGUIUtility.labelWidth = 75;
						EditorGUILayout.BeginVertical();
						source.HasPassword = EditorGUILayout.Toggle("Credentials", source.HasPassword);
						if (source.HasPassword)
						{
							source.UserName = EditorGUILayout.TextField("User Name", source.UserName);
							source.SavedPassword = EditorGUILayout.PasswordField("Password", source.SavedPassword);
						}
						else
						{
							source.UserName = null;
						}
						EditorGUIUtility.labelWidth = 0;
						EditorGUILayout.EndVertical();
					}
					EditorGUILayout.EndHorizontal();

					EditorGUILayout.BeginHorizontal();
					{
						if (GUILayout.Button("Move Up"))
						{
							sourceToMoveUp = source;
						}

						if (GUILayout.Button("Move Down"))
						{
							sourceToMoveDown = source;
						}

						if (GUILayout.Button("Remove"))
						{
							sourceToRemove = source;
						}
					}
					EditorGUILayout.EndHorizontal();
				}
				EditorGUILayout.EndVertical();
			}

			if (sourceToMoveUp != null)
			{
				var index = conf.PackageSources.IndexOf(sourceToMoveUp);
				if (index > 0)
				{
					conf.PackageSources[index] = conf.PackageSources[index - 1];
					conf.PackageSources[index - 1] = sourceToMoveUp;
				}
			}

			if (sourceToMoveDown != null)
			{
				var index = conf.PackageSources.IndexOf(sourceToMoveDown);
				if (index < conf.PackageSources.Count - 1)
				{
					conf.PackageSources[index] = conf.PackageSources[index + 1];
					conf.PackageSources[index + 1] = sourceToMoveDown;
				}
			}

			if (sourceToRemove != null)
			{
				conf.PackageSources.Remove(sourceToRemove);
			}

			if (GUILayout.Button("Add New Source"))
			{
				conf.PackageSources.Add(new NugetPackageSource("New Source", "source_path"));
			}

			EditorGUILayout.EndScrollView();

			if (GUILayout.Button("Save"))
			{
				conf.Save(NugetHelper.NugetConfigFilePath);
			}
		}
	}
}
