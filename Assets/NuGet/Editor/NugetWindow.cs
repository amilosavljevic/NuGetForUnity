using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JetBrains.Annotations;
using Nordeus.Nuget.Utility;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace NugetForUnity
{
	/// <summary>
	/// Represents the NuGet Package Manager Window in the Unity Editor.
	/// </summary>
	public class NugetWindow : EditorWindow
	{
		private const string url = "https://github.com/Nordeus/NuGetForUnity/releases";

		/// <summary>
		/// True when the NugetWindow has initialized. This is used to skip time-consuming reloading operations when the assembly is reloaded.
		/// </summary>
		[SerializeField]
		private bool hasRefreshed;

		/// <summary>
		/// The current position of the scroll bar in the GUI.
		/// </summary>
		private Vector2 scrollPosition;

		/// <summary>
		/// The list of NugetPackages available to install.
		/// </summary>
		[SerializeField]
		private List<NugetPackage> availablePackages = new List<NugetPackage>();

		/// <summary>
		/// The list of package updates available, based on the already installed packages.
		/// </summary>
		[SerializeField]
		private List<NugetPackage> updatePackages = new List<NugetPackage>();

		/// <summary>
		/// The filtered list of package updates available.
		/// </summary>
		private List<NugetPackage> filteredUpdatePackages = new List<NugetPackage>();

		/// <summary>
		/// True to show all old package versions.  False to only show the latest version.
		/// </summary>
		private bool showAllOnlineVersions;

		/// <summary>
		/// True to show beta and alpha package versions.  False to only show stable versions.
		/// </summary>
		private bool showOnlinePrerelease;

		/// <summary>
		/// By default we are only showing the packages you manually installed but this options allows to show all
		/// </summary>
		private bool showAllInstalledPackages;

		/// <summary>
		/// True to show all old package versions.  False to only show the latest version.
		/// </summary>
		private bool showAllUpdateVersions;

		/// <summary>
		/// True to show beta and alpha package versions.  False to only show stable versions.
		/// </summary>
		private bool showPrereleaseUpdates;

		/// <summary>
		/// The width to use for the install/uninstall/update/downgrade button
		/// </summary>
		private readonly GUILayoutOption installButtonWidth = GUILayout.Width(180);

		/// <summary>
		/// The width to use for the Link Source button
		/// </summary>
		private readonly GUILayoutOption linkSourceButtonWidth = GUILayout.Width(100);

		/// <summary>
		/// The height to use for the install/uninstall/update/downgrade button
		/// </summary>
		private readonly GUILayoutOption installButtonHeight = GUILayout.Height(27);

		/// <summary>
		/// The search term to search the online packages for.
		/// </summary>
		private string onlineSearchTerm = "Search";

		private List<NugetPackage> filteredInstalledPackages;
		
		private string lastInstalledSearchTerm = "Search";
		
		/// <summary>
		/// The search term to search the installed packages for.
		/// </summary>
		private string installedSearchTerm = "Search";

		/// <summary>
		/// The search term in progress while it is being typed into the search box.
		/// We wait until the Enter key or Search button is pressed before searching in order
		/// to match the way that the Online and Updates searches work.
		/// </summary>
		private string installedSearchTermEditBox = "Search";

		/// <summary>
		/// The search term to search the update packages for.
		/// </summary>
		private string updatesSearchTerm = "Search";

		/// <summary>
		/// The number of packages to get from the request to the server.
		/// </summary>
		private const int numberToGet = 15;

		/// <summary>
		/// The number of packages to skip when requesting a list of packages from the server.  This is used to get a new group of packages.
		/// </summary>
		[SerializeField]
		private int numberToSkip;

		/// <summary>
		/// The currently selected tab in the window.
		/// </summary>
		private int currentTab;

		/// <summary>
		/// The titles of the tabs in the window.
		/// </summary>
		private readonly string[] tabTitles = { "Online", "Installed", "Updates" };

		/// <summary>
		/// The default icon to display for packages.
		/// </summary>
		[SerializeField]
		private Texture2D defaultIcon;

		/// <summary>
		/// Used to keep track of which packages the user has opened the clone window on.
		/// </summary>
		private readonly HashSet<NugetPackage> openCloneWindows = new HashSet<NugetPackage>();


		private List<NugetPackage> FilteredInstalledPackages
		{
			get
			{
				if (filteredInstalledPackages == null || installedSearchTerm != lastInstalledSearchTerm)
				{
					filteredInstalledPackages = NugetHelper.InstalledPackages.Where(x => x.IsManuallyInstalled || showAllInstalledPackages).ToList();
				}
				if (installedSearchTerm == lastInstalledSearchTerm || installedSearchTerm == "Search")
					return filteredInstalledPackages;

				bool Filter(NugetPackage x) => (x.IsManuallyInstalled || showAllInstalledPackages)
												&& (x.Id.ToLower().Contains(installedSearchTerm)
													|| x.Title.ToLower().Contains(installedSearchTerm));

				filteredInstalledPackages = NugetHelper.InstalledPackages.Where(Filter).ToList();
				lastInstalledSearchTerm = installedSearchTerm;
				return filteredInstalledPackages;
			}
		}

		/// <summary>
		/// Opens the NuGet Package Manager Window.
		/// </summary>
		[MenuItem("NuGet/Manage NuGet Packages", false, 0)]
		protected static void DisplayNugetWindow()
		{
			GetWindow<NugetWindow>();
		}

		/// <summary>
		/// Restores all packages defined in packages.config
		/// </summary>
		[MenuItem("NuGet/Restore Packages", false, 1)]
		protected static void RestorePackages()
		{
			NugetHelper.Restore();
		}

		/// <summary>
		/// Displays the version number of NuGetForUnity.
		/// </summary>
		[MenuItem("NuGet/Version " + NugetPreferences.NuGetForUnityVersion, false, 10)]
		protected static void DisplayVersion()
		{
			Application.OpenURL(url);
		}

		/// <summary>
		/// Checks/launches the Releases page to update NuGetForUnity with a new version.
		/// </summary>
		[MenuItem("NuGet/Check for Updates...", false, 10)]
		protected static void CheckForUpdates()
		{
			using (var request = UnityWebRequest.Get(url))
			{
				request.SendWebRequest();

				NugetHelper.LogVerbose("HTTP GET {0}", url);
				while (!request.isDone)
				{
					EditorUtility.DisplayProgressBar("Checking updates", null, 0.0f);
				}
				EditorUtility.ClearProgressBar();

				string latestVersion = null;
				string latestVersionDownloadUrl = null;

				string response = null;
				if (!request.isNetworkError && !request.isHttpError)
				{
					response = request.downloadHandler.text;
				}

				if (response != null)
				{
					latestVersion = GetLatestVersonFromReleasesHtml(response, out latestVersionDownloadUrl);
				}

				if (latestVersion == null)
				{
					EditorUtility.DisplayDialog(
							"Unable to Determine Updates",
							$"Couldn't find release information at {url}.",
							"OK");
					return;
				}

				var current = new NugetPackageIdentifier("NuGetForUnity", NugetPreferences.NuGetForUnityVersion);
				var latest = new NugetPackageIdentifier("NuGetForUnity", latestVersion);
				if (current >= latest)
				{
					EditorUtility.DisplayDialog(
							"No Updates Available",
							$"Your version of NuGetForUnity is up to date.\nVersion {NugetPreferences.NuGetForUnityVersion}.",
							"OK");
					return;
				}

				// New version is available. Give user options for installing it.
				switch (EditorUtility.DisplayDialogComplex(
						"Update Available",
						$"Current Version: {NugetPreferences.NuGetForUnityVersion}\nLatest Version: {latestVersion}",
						"Install Latest",
						"Open Releases Page",
						"Cancel"))
				{
					case 0: DownloadAndRunUpdate(latestVersionDownloadUrl); break;
					case 1: Application.OpenURL(url); break;
					case 2: break;
				}
			}
		}

		private static void DownloadAndRunUpdate(string latestVersionDownloadUrl)
		{
			using (var request = UnityWebRequest.Get(latestVersionDownloadUrl))
			{
				request.SendWebRequest();

				NugetHelper.LogVerbose("HTTP GET {0}", latestVersionDownloadUrl);
				while (!request.isDone)
				{
					EditorUtility.DisplayProgressBar("Checking updates", null, 0.0f);
				}

				EditorUtility.ClearProgressBar();

				if (request.isNetworkError || request.isHttpError)
				{
					EditorUtility.DisplayDialog(
												 "Failed update",
												 $"Couldn't download the update from {latestVersionDownloadUrl}.",
												 "OK");
					return;
				}

				var downloadFilePath = Path.Combine(Path.GetTempPath(), "NugetForUnity.unitypackage");
				File.WriteAllBytes(downloadFilePath, request.downloadHandler.data);

				AssetDatabase.ImportPackage(downloadFilePath, true);
			}
		}

		private static string GetLatestVersonFromReleasesHtml(string response, out string url)
		{
			var hrefRegex = new Regex(@"<a href=""(?<url>.*NuGetForUnity\.(?<version>\d+\.\d+\.\d+)\.unitypackage)""");
			var match = hrefRegex.Match(response);
			if (!match.Success)
			{
				url = null;
				return null;
			}
			url = "https://github.com/" + match.Groups["url"].Value;
			return match.Groups["version"].Value;
		}

		/// <summary>
		/// Called when enabling the window.
		/// </summary>
		[UsedImplicitly]
		private void OnEnable()
		{
			Refresh(false);
		}

		private void Refresh(bool forceFullRefresh)
		{
			var stopwatch = new Stopwatch();
			stopwatch.Start();

			try
			{
				// reload the NuGet.config file, in case it was changed after Unity opened, but before the manager window opened (now)
				NugetHelper.ForceReloadNugetConfig();

				// if we are entering playmode, don't do anything
				if (EditorApplication.isPlayingOrWillChangePlaymode)
				{
					return;
				}

				NugetHelper.LogVerbose(hasRefreshed ? "NugetWindow reloading config" : "NugetWindow reloading config and updating packages");

				// set the window title
				titleContent = new GUIContent("NuGet");
				filteredInstalledPackages = null;

				if (!hasRefreshed || forceFullRefresh)
				{
					// reset the number to skip
					numberToSkip = 0;

					// TODO: Do we even need to load ALL of the data, or can we just get the Online tab packages?

					EditorUtility.DisplayProgressBar("Opening NuGet", "Fetching packages from server...", 0.3f);
					UpdateOnlinePackages();

					EditorUtility.DisplayProgressBar("Opening NuGet", "Getting installed packages...", 0.6f);
					NugetHelper.UpdateInstalledPackages();

					EditorUtility.DisplayProgressBar("Opening NuGet", "Getting available updates...", 0.9f);
					UpdateUpdatePackages();

					// load the default icon from the Resources folder
					defaultIcon = (Texture2D)Resources.Load("defaultIcon", typeof(Texture2D));
				}
				else
				{
					NugetHelper.UpdateInstalledPackages();
				}

				hasRefreshed = true;
			}
			catch (Exception e)
			{
				UnityEngine.Debug.LogErrorFormat("{0}", e.ToString());
			}
			finally
			{
				EditorUtility.ClearProgressBar();

				NugetHelper.LogVerbose("NugetWindow reloading took {0} ms", stopwatch.ElapsedMilliseconds);
			}
		}

		/// <summary>
		/// Updates the list of available packages by running a search with the server using the currently set parameters (# to get, # to skip, etc).
		/// </summary>
		private void UpdateOnlinePackages()
		{
			availablePackages = NugetHelper.Search(onlineSearchTerm != "Search" ? onlineSearchTerm : string.Empty, showAllOnlineVersions, showOnlinePrerelease, numberToGet, numberToSkip);
		}

		/// <summary>
		/// Updates the list of update packages.
		/// </summary>
		private void UpdateUpdatePackages()
		{
			// get any available updates for the installed packages
			updatePackages = NugetHelper.GetUpdates(NugetHelper.InstalledPackages, showPrereleaseUpdates, showAllUpdateVersions);
			filteredUpdatePackages = updatePackages;

			if (updatesSearchTerm != "Search")
			{
				filteredUpdatePackages = updatePackages.Where(x => x.Id.ToLower().Contains(updatesSearchTerm) || x.Title.ToLower().Contains(updatesSearchTerm)).ToList();
			}
		}

		/// <summary>
		/// From here: http://forum.unity3d.com/threads/changing-the-background-color-for-beginhorizontal.66015/
		/// </summary>
		/// <param name="width"></param>
		/// <param name="height"></param>
		/// <param name="col"></param>
		/// <returns></returns>
		private static Texture2D MakeTex(int width, int height, Color col)
		{
			var pix = new Color[width * height];

			for (var i = 0; i < pix.Length; i++)
				pix[i] = col;

			var result = new Texture2D(width, height);
			result.SetPixels(pix);
			result.Apply();

			return result;
		}

		/// <summary>
		/// Automatically called by Unity to draw the GUI.
		/// </summary>
		protected void OnGUI()
		{
			var selectedTab = GUILayout.Toolbar(currentTab, tabTitles);

			if (selectedTab != currentTab)
				OnTabChanged();

			currentTab = selectedTab;

			switch (currentTab)
			{
				case 0:
					DrawOnline();
					break;
				case 1:
					DrawInstalled();
					break;
				case 2:
					DrawUpdates();
					break;
			}
		}

		private void OnTabChanged()
		{
			openCloneWindows.Clear();
		}

		/// <summary>
		/// Creates a GUI style with a contrasting background color based upon if the Unity Editor is the free (light) skin or the Pro (dark) skin.
		/// </summary>
		/// <returns>A GUI style with the appropriate background color set.</returns>
		private static GUIStyle GetContrastStyle()
		{
			var style = new GUIStyle();
			var backgroundColor = EditorGUIUtility.isProSkin ? new Color(0.3f, 0.3f, 0.3f) : new Color(0.6f, 0.6f, 0.6f);
			style.normal.background = MakeTex(16, 16, backgroundColor); 
			return style;
		}

		/// <summary>
		/// Creates a GUI style with a background color the same as the editor's current background color.
		/// </summary>
		/// <returns>A GUI style with the appropriate background color set.</returns>
		private static GUIStyle GetBackgroundStyle()
		{
			var style = new GUIStyle();
			var backgroundColor = EditorGUIUtility.isProSkin ? new Color32(56, 56, 56, 255) : new Color32(194, 194, 194, 255);
			style.normal.background = MakeTex(16, 16, backgroundColor); 
			return style;
		}

		/// <summary>
		/// Draws the list of installed packages that have updates available.
		/// </summary>
		private void DrawUpdates()
		{
			DrawUpdatesHeader();

			// display all of the installed packages
			scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
			using (new EditorGUILayout.VerticalScope())
			{
				if (filteredUpdatePackages != null && filteredUpdatePackages.Count > 0)
				{
					DrawPackages(filteredUpdatePackages);
				}
				else
				{
					EditorStyles.label.fontStyle = FontStyle.Bold;
					EditorStyles.label.fontSize = 14;
					EditorGUILayout.LabelField("There are no updates available!", GUILayout.Height(20));
					EditorStyles.label.fontSize = 10;
					EditorStyles.label.fontStyle = FontStyle.Normal;
				}
			}

			EditorGUILayout.EndScrollView();
		}

		/// <summary>
		/// Draws the list of installed packages.
		/// </summary>
		private void DrawInstalled()
		{
			DrawInstalledHeader();

			// display all of the installed packages
			scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
			using (new EditorGUILayout.VerticalScope())
			{
				var filteredInstalled = FilteredInstalledPackages;
				if (filteredInstalled.Count > 0)
				{
					DrawPackages(filteredInstalled);
				}
				else
				{
					EditorStyles.label.fontStyle = FontStyle.Bold;
					EditorStyles.label.fontSize = 14;
					EditorGUILayout.LabelField("There are no packages installed!", GUILayout.Height(20));
					EditorStyles.label.fontSize = 10;
					EditorStyles.label.fontStyle = FontStyle.Normal;
				}
			}

			EditorGUILayout.EndScrollView();
		}

		/// <summary>
		/// Draws the current list of available online packages.
		/// </summary>
		private void DrawOnline()
		{
			DrawOnlineHeader();

			// display all of the packages
			scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
			using (new EditorGUILayout.VerticalScope())
			{
				DrawPackages(availablePackages);

				var showMoreStyle = new GUIStyle();
				if (Application.HasProLicense())
				{
					showMoreStyle.normal.background = MakeTex(20, 20, new Color(0.05f, 0.05f, 0.05f));
				}
				else
				{
					showMoreStyle.normal.background = MakeTex(20, 20, new Color(0.4f, 0.4f, 0.4f));
				}

				using (new EditorGUILayout.VerticalScope(showMoreStyle))
				{
					// allow the user to dislay more results
					if (GUILayout.Button("Show More", GUILayout.Width(120)))
					{
						numberToSkip += numberToGet;
						availablePackages.AddRange(NugetHelper.Search(onlineSearchTerm != "Search" ? onlineSearchTerm : string.Empty,
																	showAllOnlineVersions, showOnlinePrerelease, numberToGet,
																	numberToSkip));
					}
				}
			}

			EditorGUILayout.EndScrollView();
		}

		private void DrawPackages(List<NugetPackage> packages)
		{
			var backgroundStyle = GetBackgroundStyle();
			var contrastStyle = GetContrastStyle();

			for (var i = 0; i < packages.Count; i++)
			{
				using (new EditorGUILayout.VerticalScope(backgroundStyle))
				{
					DrawPackage(packages[i], backgroundStyle, contrastStyle);
				}

				// swap styles
				var tempStyle = backgroundStyle;
				backgroundStyle = contrastStyle;
				contrastStyle = tempStyle;
			}
		}

		/// <summary>
		/// Draws the header which allows filtering the online list of packages.
		/// </summary>
		private void DrawOnlineHeader()
		{
			var headerStyle = new GUIStyle();
			if (Application.HasProLicense())
			{
				headerStyle.normal.background = MakeTex(20, 20, new Color(0.05f, 0.05f, 0.05f));
			}
			else
			{
				headerStyle.normal.background = MakeTex(20, 20, new Color(0.4f, 0.4f, 0.4f));
			}

			using (new EditorGUILayout.VerticalScope(headerStyle))
			{
				using (new EditorGUILayout.HorizontalScope())
				{
					var showAllVersionsTemp = EditorGUILayout.Toggle("Show All Versions", showAllOnlineVersions);
					if (showAllVersionsTemp != showAllOnlineVersions)
					{
						showAllOnlineVersions = showAllVersionsTemp;
						UpdateOnlinePackages();
					}

					if (GUILayout.Button("Refresh", GUILayout.Width(60)))
					{
						Refresh(true);
					}
				}

				var showPrereleaseTemp = EditorGUILayout.Toggle("Show Prerelease", showOnlinePrerelease);
				if (showPrereleaseTemp != showOnlinePrerelease)
				{
					showOnlinePrerelease = showPrereleaseTemp;
					UpdateOnlinePackages();
				}

				var enterPressed = Event.current.Equals(Event.KeyboardEvent("return"));

				using (new EditorGUILayout.HorizontalScope())
				{
					var oldFontSize = GUI.skin.textField.fontSize;
					GUI.skin.textField.fontSize = 25;
					onlineSearchTerm = EditorGUILayout.TextField(onlineSearchTerm, GUILayout.Height(30));

					if (GUILayout.Button("Search", GUILayout.Width(100), GUILayout.Height(28)))
					{
						// the search button emulates the Enter key
						enterPressed = true;
					}

					GUI.skin.textField.fontSize = oldFontSize;
				}

				// search only if the enter key is pressed
				if (enterPressed)
				{
					// reset the number to skip
					numberToSkip = 0;
					UpdateOnlinePackages();
				}
			}
		}

		/// <summary>
		/// Draws the header which allows filtering the installed list of packages.
		/// </summary>
		private void DrawInstalledHeader()
		{
			var headerStyle = new GUIStyle();
			if (Application.HasProLicense())
			{
				headerStyle.normal.background = MakeTex(20, 20, new Color(0.05f, 0.05f, 0.05f));
			}
			else
			{
				headerStyle.normal.background = MakeTex(20, 20, new Color(0.4f, 0.4f, 0.4f));
			}

			using (new EditorGUILayout.VerticalScope(headerStyle))
			{
				var enterPressed = Event.current.Equals(Event.KeyboardEvent("return"));

				using (new EditorGUILayout.HorizontalScope())
				{
					var newVal = EditorGUILayout.Toggle("Show Dependecies", showAllInstalledPackages);
					if (newVal != showAllInstalledPackages)
					{
						showAllInstalledPackages = newVal;
						lastInstalledSearchTerm = null;
					}

					if (NugetHelper.NugetConfigFile.AllowUninstallAll && GUILayout.Button("Uninstall All", GUILayout.Width(100)))
					{
						NugetHelper.UninstallAll();
					}
				}

				using (new EditorGUILayout.HorizontalScope())
				{
					var oldFontSize = GUI.skin.textField.fontSize;
					GUI.skin.textField.fontSize = 25;
					installedSearchTermEditBox = EditorGUILayout.TextField(installedSearchTermEditBox, GUILayout.Height(30));

					if (GUILayout.Button("Search", GUILayout.Width(100), GUILayout.Height(28)))
					{
						// the search button emulates the Enter key
						enterPressed = true;
					}

					GUI.skin.textField.fontSize = oldFontSize;
				}

				// search only if the enter key is pressed
				if (enterPressed)
				{
					installedSearchTerm = installedSearchTermEditBox.ToLower();
				}
			}
		}

		/// <summary>
		/// Draws the header for the Updates tab.
		/// </summary>
		private void DrawUpdatesHeader()
		{
			var headerStyle = new GUIStyle();
			if (Application.HasProLicense())
			{
				headerStyle.normal.background = MakeTex(20, 20, new Color(0.05f, 0.05f, 0.05f));
			}
			else
			{
				headerStyle.normal.background = MakeTex(20, 20, new Color(0.4f, 0.4f, 0.4f));
			}

			using (new EditorGUILayout.VerticalScope(headerStyle))
			{
				using (new EditorGUILayout.HorizontalScope())
				{
					var showAllVersionsTemp = EditorGUILayout.Toggle("Show All Versions", showAllUpdateVersions);
					if (showAllVersionsTemp != showAllUpdateVersions)
					{
						showAllUpdateVersions = showAllVersionsTemp;
						UpdateUpdatePackages();
					}

					if (GUILayout.Button("Install All Updates", GUILayout.Width(150)))
					{
						NugetHelper.UpdateAll(updatePackages, NugetHelper.InstalledPackages);
						NugetHelper.UpdateInstalledPackages();
						UpdateUpdatePackages();
					}
				}

				var showPrereleaseTemp = EditorGUILayout.Toggle("Show Prerelease", showPrereleaseUpdates);
				if (showPrereleaseTemp != showPrereleaseUpdates)
				{
					showPrereleaseUpdates = showPrereleaseTemp;
					UpdateUpdatePackages();
				}

				var enterPressed = Event.current.Equals(Event.KeyboardEvent("return"));

				using (new EditorGUILayout.HorizontalScope())
				{
					var oldFontSize = GUI.skin.textField.fontSize;
					GUI.skin.textField.fontSize = 25;
					updatesSearchTerm = EditorGUILayout.TextField(updatesSearchTerm, GUILayout.Height(30));

					if (GUILayout.Button("Search", GUILayout.Width(100), GUILayout.Height(28)))
					{
						// the search button emulates the Enter key
						enterPressed = true;
					}

					GUI.skin.textField.fontSize = oldFontSize;
				}

				// search only if the enter key is pressed
				if (enterPressed)
				{
					if (updatesSearchTerm != "Search")
					{
						filteredUpdatePackages = updatePackages.Where(x => x.Id.ToLower().Contains(updatesSearchTerm) || x.Title.ToLower().Contains(updatesSearchTerm)).ToList();
					}
				}
			}
		}

		/// <summary>
		/// Draws the given <see cref="NugetPackage"/>.
		/// </summary>
		/// <param name="package">The <see cref="NugetPackage"/> to draw.</param>
		private void DrawPackage(NugetPackage package, GUIStyle packageStyle, GUIStyle contrastStyle)
		{
			var installedPackages = NugetHelper.InstalledPackages;
			var installed = installedPackages.FirstOrDefault(p => p.Id == package.Id);

			using (new EditorGUILayout.HorizontalScope())
			{
				// The Unity GUI system (in the Editor) is terrible.  This probably requires some explanation.
				// Every time you use a Horizontal block, Unity appears to divide the space evenly.
				// (i.e. 2 components have half of the window width, 3 components have a third of the window width, etc)
				// GUILayoutUtility.GetRect is SUPPOSED to return a rect with the given height and width, but in the GUI layout.  It doesn't.
				// We have to use GUILayoutUtility to get SOME rect properties, but then manually calculate others.
				using (new EditorGUILayout.HorizontalScope())
				{
					const int iconSize = 32;
					var padding = 5;
					var rect = GUILayoutUtility.GetRect(iconSize, iconSize);
					// only use GetRect's Y position.  It doesn't correctly set the width, height or X position.
					rect.x = padding;
					rect.y += 3;
					rect.width = iconSize;
					rect.height = iconSize;

					if (package.Icon != null)
					{
						GUI.DrawTexture(rect, package.Icon, ScaleMode.StretchToFill);
					}
					else
					{
						GUI.DrawTexture(rect, defaultIcon, ScaleMode.StretchToFill);
					}

					rect = GUILayoutUtility.GetRect(position.width / 2 - (iconSize + padding), 20);
					rect.x = iconSize + padding;
					rect.y += 10;

					EditorStyles.label.fontStyle = FontStyle.Bold;
					EditorStyles.label.fontSize = 14;
					GUI.Label(rect, string.Format("{1} [{0}]", package.Version, package.Title), EditorStyles.label);
					EditorStyles.label.fontSize = 10;
					EditorStyles.label.fontStyle = FontStyle.Normal;
				}

				if (installedPackages.Contains(package))
				{
					// This specific version is installed
					LinkUnlinkSourceButton(package);

					if (GUILayout.Button("Uninstall", installButtonWidth, installButtonHeight))
					{
						NugetHelper.Uninstall(package, true, true);
						NugetHelper.UpdateInstalledPackages();
						UpdateUpdatePackages();
						lastInstalledSearchTerm = null;
					}
				}
				else
				{
					if (installed != null)
					{
						LinkUnlinkSourceButton(package);
						if (installed < package)
						{
							// An older version is installed
							if (GUILayout.Button($"Update to [{package.Version}]", installButtonWidth, installButtonHeight))
							{
								NugetHelper.Update(installed, package);
								NugetHelper.UpdateInstalledPackages();
								UpdateUpdatePackages();
								lastInstalledSearchTerm = null;
							}
						}
						else if (installed > package)
						{
							// A newer version is installed
							if (GUILayout.Button($"Downgrade to [{package.Version}]", installButtonWidth, installButtonHeight))
							{
								NugetHelper.Update(installed, package);
								NugetHelper.UpdateInstalledPackages();
								UpdateUpdatePackages();
								lastInstalledSearchTerm = null;
							}
						}
					}
					else
					{
						if (GUILayout.Button("Install", installButtonWidth, installButtonHeight))
						{
							package.IsManuallyInstalled = true;
							NugetHelper.InstallIdentifier(package);
							AssetDatabase.Refresh();
							NugetHelper.UpdateInstalledPackages();
							UpdateUpdatePackages();
							lastInstalledSearchTerm = null;
						}
					}
				}
			}

			EditorGUILayout.Separator();

			using (new EditorGUILayout.HorizontalScope())
			{
				using (new EditorGUILayout.VerticalScope())
				{
					// Show the package description
					EditorStyles.label.wordWrap = true;
					EditorStyles.label.fontStyle = FontStyle.Normal;
					EditorGUILayout.LabelField($"{package.Description}");

					// Show the package release notes
					if (!string.IsNullOrEmpty(package.ReleaseNotes))
					{
						EditorStyles.label.wordWrap = true;
						EditorGUILayout.LabelField($"Release Notes: {package.ReleaseNotes}");
					}

					// Show the dependencies
					if (package.Dependencies.Count > 0)
					{
						EditorStyles.label.wordWrap = true;
						EditorStyles.label.fontStyle = FontStyle.Italic;
						var builder = new StringBuilder();
						foreach (var dependency in package.Dependencies)
						{
							builder.Append($" {dependency.Id} {dependency.Version};");
						}
						EditorGUILayout.LabelField($"Depends on:{builder}");
						EditorStyles.label.fontStyle = FontStyle.Normal;
					}

					// Create the style for putting a box around the 'Clone' button
					var cloneButtonBoxStyle = new GUIStyle("box")
					{
						stretchWidth = false,
						margin =
						{
							top = 0,
							bottom = 0
						},
						padding = {bottom = 4}
					};

					var normalButtonBoxStyle = new GUIStyle(cloneButtonBoxStyle) {normal = {background = packageStyle.normal.background}};

					var showCloneWindow = openCloneWindows.Contains(package);
					cloneButtonBoxStyle.normal.background = showCloneWindow ? contrastStyle.normal.background : packageStyle.normal.background;

					// Create a simillar style for the 'Clone' window
					var cloneWindowStyle = new GUIStyle(cloneButtonBoxStyle) {padding = new RectOffset(6, 6, 2, 6)};

					// Show button bar
					using (new EditorGUILayout.HorizontalScope())
					{
						if (package.RepositoryType == RepositoryType.Git || package.RepositoryType == RepositoryType.TfsGit)
						{
							if (!string.IsNullOrEmpty(package.RepositoryUrl))
							{
								using (new EditorGUILayout.HorizontalScope(cloneButtonBoxStyle))
								{
									var cloneButtonStyle = new GUIStyle(GUI.skin.button);
									cloneButtonStyle.normal = showCloneWindow ? cloneButtonStyle.active : cloneButtonStyle.normal;
									if (GUILayout.Button("Clone", cloneButtonStyle, GUILayout.ExpandWidth(false)))
									{
										showCloneWindow = !showCloneWindow;
									}

									if (showCloneWindow)
										openCloneWindows.Add(package);
									else
										openCloneWindows.Remove(package);
								}
							}
						}

						if (!string.IsNullOrEmpty(package.LicenseUrl) && package.LicenseUrl != "http://your_license_url_here")
						{
							// Create a box around the license button to keep it aligned with Clone button
							using (new EditorGUILayout.HorizontalScope(normalButtonBoxStyle))
							{
								// Show the license button
								if (GUILayout.Button("View License", GUILayout.ExpandWidth(false)))
								{
									Application.OpenURL(package.LicenseUrl);
								}
							}
						}
					}

					if (showCloneWindow)
					{
						using (new EditorGUILayout.VerticalScope(cloneWindowStyle))
						{
							// Clone latest label
							using (new EditorGUILayout.HorizontalScope())
							{
								GUILayout.Space(20f);
								EditorGUILayout.LabelField("clone latest");
							}

							// Clone latest row
							using (new EditorGUILayout.HorizontalScope())
							{
								if (GUILayout.Button("Copy", GUILayout.ExpandWidth(false)))
								{
									GUI.FocusControl(package.Id + package.Version + "repoUrl");
									GUIUtility.systemCopyBuffer = package.RepositoryUrl;
								}

								GUI.SetNextControlName(package.Id + package.Version + "repoUrl");
								EditorGUILayout.TextField(package.RepositoryUrl);
							}

							// Clone @ commit label
							GUILayout.Space(4f);
							using (new EditorGUILayout.HorizontalScope())
							{
								GUILayout.Space(20f);
								EditorGUILayout.LabelField("clone @ commit");
							}

							// Clone @ commit row
							using (new EditorGUILayout.HorizontalScope())
							{
								// Create the three commands a user will need to run to get the repo @ the commit. Intentionally leave off the last newline for better UI appearance
								var commands = string.Format("git clone {0} {1} --no-checkout{2}cd {1}{2}git checkout {3}",  package.RepositoryUrl, package.Id, Environment.NewLine, package.RepositoryCommit);

								if (GUILayout.Button("Copy", GUILayout.ExpandWidth(false)))
								{
									GUI.FocusControl(package.Id + package.Version + "commands");

									// Add a newline so the last command will execute when pasted to the CL
									GUIUtility.systemCopyBuffer = (commands + Environment.NewLine);
								}

								using (new EditorGUILayout.VerticalScope())
								{
									GUI.SetNextControlName(package.Id + package.Version + "commands");
									EditorGUILayout.TextArea(commands);
								}
							}
						}
					}
					
					EditorGUILayout.Separator();
				}

				if (!string.IsNullOrEmpty(package.ProjectUrl))
				{
					if (installed != null) GUILayoutLink(package.ProjectUrl, $"currently [{installed.Version}]");
					else GUILayoutLink(package.ProjectUrl, "Project home");
				}
				else if (installed != null)
				{
					var labelStyle = new GUIStyle(EditorStyles.label) {alignment = TextAnchor.UpperRight};
					GUILayout.Label($"currently [{installed.Version}]", labelStyle, installButtonWidth);
				}
			}
		}

		private void LinkUnlinkSourceButton(NugetPackage package)
		{
			var packagePath = Path.Combine(NugetHelper.NugetConfigFile.RepositoryPath, $"{package.Id}.{package.Version}");
			var packageSources = packagePath;
			var packageEditorSources = Path.Combine(packagePath, "Editor");

			var sourcesExists = SymbolicLink.Exists(packagePath);
			if (!sourcesExists)
			{
				packageSources = Path.Combine(packagePath, "lib");
				sourcesExists = SymbolicLink.Exists(packageSources);
			}
			var sourcesEditorExists = SymbolicLink.Exists(packageEditorSources);
			var isLinked =  sourcesExists || sourcesEditorExists;

			if (isLinked)
			{
				if (GUILayout.Button("Unlink Source", linkSourceButtonWidth, installButtonHeight))
				{
					if (sourcesExists) SymbolicLink.Delete(packageSources);
					if (sourcesEditorExists) SymbolicLink.Delete(packageEditorSources);
					var path = Path.Combine(NugetHelper.NugetConfigFile.RepositoryPath, $".{package.Id}.{package.Version}");
					if (Directory.Exists(path))
					{
						Directory.Move(path, packagePath);
						if (File.Exists(path + ".meta")) File.Move(path + ".meta", packagePath + ".meta");
					}
					else
					{
						path = Path.Combine(packagePath, ".lib");
						if (Directory.Exists(path))
						{
							Directory.Move(path, Path.Combine(packagePath, "lib"));
							if (File.Exists(path + ".meta")) File.Move(path + ".meta", Path.Combine(packagePath, "lib.meta"));
						}

						path = Path.Combine(packagePath, ".Editor");
						if (Directory.Exists(path))
						{
							Directory.Move(path, Path.Combine(packagePath, "Editor"));
							if (File.Exists(path + ".meta")) File.Move(path + ".meta", Path.Combine(packagePath, "Editor.meta"));
						}
					}
				}

				AssetDatabase.Refresh();

				return;
			}

			if (GUILayout.Button("Link Source", linkSourceButtonWidth, installButtonHeight))
			{
				InstallPreCommitHook();
				var sourcePath = GetSourceProjPath(package);
				if (string.IsNullOrEmpty(sourcePath)) return;
				if (Path.GetFileName(sourcePath) == "Source") sourcePath = Path.GetDirectoryName(sourcePath);
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
		}

		public static void InstallPreCommitHook()
		{
			var gitFolder = Path.Combine(Directory.GetCurrentDirectory(), ".git/hooks");
			if (!Directory.Exists(gitFolder))
			{
				var parentFolder = Path.GetDirectoryName(Directory.GetCurrentDirectory());
				if (parentFolder == null) return;
				gitFolder = Path.Combine(parentFolder, ".git/hooks");
				if (!Directory.Exists(gitFolder)) return;
			}

			var preCommitHook = @"
has_link() {
	local path=""$1""
	if echo ""$path"" | grep -vq '/'; then
		return
	fi
	if [ -L ""$path"" ]; then
		echo ""Error: You can't commit paths with symbolic links: '$path'""
		exit 1
	else
		has_link ""${path%/*}""
	fi
}

while read path
do
	has_link ""$path""
done< <(git diff --name-only --cached)
";
			preCommitHook = preCommitHook.Replace("\r", "");
			var preCommitFile = Path.Combine(gitFolder, "pre-commit");
			var preCommitContent = "#!/bin/bash";
			if (File.Exists(preCommitFile))
			{
				preCommitContent = File.ReadAllText(preCommitFile);
				if (preCommitContent.Contains(preCommitHook)) return;
				preCommitContent = preCommitContent.Replace("#!/bin/sh", "#!/bin/bash");
			}

			preCommitContent += "\n\n" + preCommitHook;
			File.WriteAllText(preCommitFile, preCommitContent);
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

		public static void GUILayoutLink(string url, string text = null)
		{
			if (text == null) text = url;
			var hyperLinkStyle = new GUIStyle(GUI.skin.label)
			{
				stretchWidth = false,
				richText = true,
				alignment = TextAnchor.UpperRight
			};

			var colorFormatString = "<color=#add8e6ff>{0}</color>";

			var underline = new string('_', text.Length);

			var formattedUrl = string.Format(colorFormatString, text);
			var formattedUnderline = string.Format(colorFormatString, underline);
			var urlRect = GUILayoutUtility.GetRect(new GUIContent(text), hyperLinkStyle);
			GUI.Label(urlRect, formattedUrl, hyperLinkStyle);
			GUI.Label(urlRect, formattedUnderline, hyperLinkStyle);

			EditorGUIUtility.AddCursorRect(urlRect, MouseCursor.Link);
			if (urlRect.Contains(Event.current.mousePosition))
			{
				if (Event.current.type == EventType.MouseUp)
					Application.OpenURL(url);
			}
		}
	}
}