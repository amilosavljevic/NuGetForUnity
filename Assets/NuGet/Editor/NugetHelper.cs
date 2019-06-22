﻿namespace NugetForUnity
{
	using System;
	using System.Collections;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.IO;
	using System.IO.Compression;
	using System.Linq;
	using System.Net;
	using System.Text;
	using UnityEditor;
	using UnityEngine;
	using UnityEngine.Networking;
	using Debug = UnityEngine.Debug;
	using System.Security.Cryptography;
	using System.Text.RegularExpressions;

	/// <summary>
	/// A set of helper methods that act as a wrapper around nuget.exe
	/// 
	/// TIP: It's incredibly useful to associate .nupkg files as compressed folder in Windows (View like .zip files).  To do this:
	///      1) Open a command prompt as admin (Press Windows key. Type "cmd".  Right click on the icon and choose "Run as Administrator"
	///      2) Enter this command: cmd /c assoc .nupkg=CompressedFolder
	/// </summary>
	public static class NugetHelper
	{
		/// <summary>
		/// The path to the nuget.config file.
		/// </summary>
		public static readonly string NugetConfigFilePath = Path.Combine(Application.dataPath, "./NuGet.config");

		/// <summary>
		/// The path to the packages.config file.
		/// </summary>
		private static readonly string PackagesConfigFilePath = Path.Combine(Application.dataPath, "./packages.config");

		/// <summary>
		/// The path where to put created (packed) and downloaded (not installed yet) .nupkg files.
		/// </summary>
		public static readonly string PackOutputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Path.Combine("NuGet", "Cache"));

		/// <summary>
		/// The amount of time, in milliseconds, before the nuget.exe process times out and is killed.
		/// </summary>
		private const int TimeOut = 60000;

		/// <summary>
		/// The loaded NuGet.config file that holds the settings for NuGet.
		/// </summary>
		public static NugetConfigFile NugetConfigFile
		{
			get
			{
				if (nugetConfigFile != null) return nugetConfigFile;
				nugetConfigFile = LoadNugetConfigFile();
				return nugetConfigFile;
			}
		}

		/// <summary>
		/// Backing field for the packages.config file.
		/// </summary>
		private static PackagesConfigFile packagesConfigFile;

		/// <summary>
		/// Gets the loaded packages.config file that hold the dependencies for the project.
		/// </summary>
		public static PackagesConfigFile PackagesConfigFile
		{
			get
			{
				if (packagesConfigFile == null)
				{
					packagesConfigFile = PackagesConfigFile.Load(PackagesConfigFilePath);
				}

				return packagesConfigFile;
			}
		}

		/// <summary>
		/// The list of <see cref="NugetPackageSource"/>s to use.
		/// </summary>
		private static readonly List<NugetPackageSource> packageSources = new List<NugetPackageSource>();

		/// <summary>
		/// The dictionary of currently installed <see cref="NugetPackage"/>s keyed off of their ID string.
		/// </summary>
		private static readonly Dictionary<string, NugetPackage> installedPackages = new Dictionary<string, NugetPackage>();

		/// <summary>
		/// The current .NET version being used (2.0 [actually 3.5], 4.6, etc).
		/// </summary>
		internal static ApiCompatibilityLevel DotNetVersion;

		private static NugetConfigFile nugetConfigFile;

		/// <summary>
		/// Static constructor used by Unity to initialize NuGet and restore packages defined in packages.config.
		/// </summary>
		static NugetHelper()
		{
			// if we are entering playmode, don't do anything
			if (EditorApplication.isPlayingOrWillChangePlaymode)
			{
				return;
			}

			DotNetVersion = PlayerSettings.GetApiCompatibilityLevel(EditorUserBuildSettings.selectedBuildTargetGroup);

			// Load the NuGet.config file
			nugetConfigFile = LoadNugetConfigFile();

			// create the nupkgs directory, if it doesn't exist
			if (!Directory.Exists(PackOutputDirectory))
			{
				Directory.CreateDirectory(PackOutputDirectory);
			}

			// restore packages - this will be called EVERY time the project is loaded or a code-file changes.
			// But we in Nordeus want to commit our packages so builds are faster and we don't want to run auto Restore.
			// Note that this class also had [InitializeOnLoad] attribute so it runs right after each compilation
			// Restore(); 
		}

		/// <summary>
		/// Reloads the file
		/// </summary>
		public static void ForceReloadNugetConfig()
		{
			nugetConfigFile = LoadNugetConfigFile();
		}

		/// <summary>
		/// Loads the NuGet.config file.
		/// </summary>
		private static NugetConfigFile LoadNugetConfigFile()
		{
			NugetConfigFile result;
			if (File.Exists(NugetConfigFilePath))
			{
				result = NugetConfigFile.Load(NugetConfigFilePath);
			}
			else
			{
				Debug.LogFormat("No NuGet.config file found. Creating default at {0}", NugetConfigFilePath);

				result = NugetConfigFile.CreateDefaultFile(NugetConfigFilePath);
				AssetDatabase.Refresh();
			}

			// parse any command line arguments
			//LogVerbose("Command line: {0}", Environment.CommandLine);
			packageSources.Clear();
			var readingSources = false;
			var useCommandLineSources = false;
			foreach (var arg in Environment.GetCommandLineArgs())
			{
				if (readingSources)
				{
					if (arg.StartsWith("-"))
					{
						readingSources = false;
					}
					else
					{
						var source = new NugetPackageSource("CMD_LINE_SRC_" + packageSources.Count, arg);
						LogVerbose("Adding command line package source {0} at {1}", "CMD_LINE_SRC_" + packageSources.Count, arg);
						packageSources.Add(source);
					}
				}

				if (arg == "-Source")
				{
					// if the source is being forced, don't install packages from the cache
					result.InstallFromCache = false;
					readingSources = true;
					useCommandLineSources = true;
				}
			}

			// if there are not command line overrides, use the NuGet.config package sources
			if (!useCommandLineSources)
			{
				if (result.ActivePackageSource.ExpandedPath == "(Aggregate source)")
				{
					packageSources.AddRange(result.PackageSources);
				}
				else
				{
					packageSources.Add(result.ActivePackageSource);
				}
			}

			return result;
		}

		/// <summary>
		/// Runs nuget.exe using the given arguments.
		/// </summary>
		/// <param name="arguments">The arguments to run nuget.exe with.</param>
		/// <param name="logOuput">True to output debug information to the Unity console.  Defaults to true.</param>
		/// <returns>The string of text that was output from nuget.exe following its execution.</returns>
		private static void RunNugetProcess(string arguments, bool logOuput = true)
		{
			// Try to find any nuget.exe in the package tools installation location
			var toolsPackagesFolder = Path.Combine(Application.dataPath, "../Packages");

			// create the folder to prevent an exception when getting the files
			Directory.CreateDirectory(toolsPackagesFolder);

			var files = Directory.GetFiles(toolsPackagesFolder, "nuget.exe", SearchOption.AllDirectories);
			if (files.Length > 1)
			{
				Debug.LogWarningFormat("More than one nuget.exe found. Using first one.");
			}
			else if (files.Length < 1)
			{
				Debug.LogWarningFormat("No nuget.exe found! Attemping to install the NuGet.CommandLine package.");
				InstallIdentifier(new NugetPackageIdentifier("NuGet.CommandLine", "2.8.6"));
				files = Directory.GetFiles(toolsPackagesFolder, "nuget.exe", SearchOption.AllDirectories);
				if (files.Length < 1)
				{
					Debug.LogErrorFormat("nuget.exe still not found. Quiting...");
					return;
				}
			}

			LogVerbose("Running: {0} \nArgs: {1}", files[0], arguments);

#if UNITY_EDITOR_OSX
			// ATTENTION: you must install mono running on your mac, we use this mono to run `nuget.exe`
			var fileName = "/Library/Frameworks/Mono.framework/Versions/Current/Commands/mono";
			var commandLine = " " + files[0] + " " + arguments;
			LogVerbose("command: " + commandLine);
#else
			var fileName = files[0];
			var commandLine = arguments;
#endif
			var process = Process.Start(
										 new ProcessStartInfo(fileName, commandLine)
										 {
											 RedirectStandardError = true,
											 RedirectStandardOutput = true,
											 UseShellExecute = false,
											 CreateNoWindow = true,
											 // WorkingDirectory = Path.GetDirectoryName(files[0]),

											 // http://stackoverflow.com/questions/16803748/how-to-decode-cmd-output-correctly
											 // Default = 65533, ASCII = ?, Unicode = nothing works at all, UTF-8 = 65533, UTF-7 = 242 = WORKS!, UTF-32 = nothing works at all
											 StandardOutputEncoding = Encoding.GetEncoding(850)
										 });

			if (!process.WaitForExit(TimeOut))
			{
				Debug.LogWarning("NuGet took too long to finish.  Killing operation.");
				process.Kill();
			}

			var error = process.StandardError.ReadToEnd();
			if (!string.IsNullOrEmpty(error))
			{
				Debug.LogError(error);
			}

			var output = process.StandardOutput.ReadToEnd();
			if (logOuput && !string.IsNullOrEmpty(output))
			{
				Debug.Log(output);
			}
		}

		/// <summary>
		/// Replace all %20 encodings with a normal space.
		/// </summary>
		/// <param name="directoryPath">The path to the directory.</param>
		private static void FixSpaces(string directoryPath)
		{
			if (directoryPath.Contains("%20"))
			{
				LogVerbose("Removing %20 from {0}", directoryPath);
				Directory.Move(directoryPath, directoryPath.Replace("%20", " "));
				directoryPath = directoryPath.Replace("%20", " ");
			}

			var subdirectories = Directory.GetDirectories(directoryPath);
			foreach (var subDir in subdirectories)
			{
				FixSpaces(subDir);
			}

			var files = Directory.GetFiles(directoryPath);
			foreach (var file in files)
			{
				if (file.Contains("%20"))
				{
					LogVerbose("Removing %20 from {0}", file);
					File.Move(file, file.Replace("%20", " "));
				}
			}
		}

		/// <summary>
		/// Cleans up a package after it has been installed.
		/// Since we are in Unity, we can make certain assumptions on which files will NOT be used, so we can delete them.
		/// </summary>
		/// <param name="package">The NugetPackage to clean.</param>
		private static void Clean(NugetPackageIdentifier package)
		{
			var packageInstallDirectory = Path.Combine(NugetConfigFile.RepositoryPath, $"{package.Id}.{package.Version}");

			LogVerbose("Cleaning {0}", packageInstallDirectory);

			FixSpaces(packageInstallDirectory);

			// delete a remnant .meta file that may exist from packages created by Unity
			DeleteFile(packageInstallDirectory + "/" + package.Id + ".nuspec.meta");

			// delete directories & files that NuGet normally deletes, but since we are installing "manually" they exist
			DeleteDirectory(packageInstallDirectory + "/_rels");
			DeleteDirectory(packageInstallDirectory + "/package");
			DeleteFile(packageInstallDirectory + "/" + package.Id + ".nuspec");
			DeleteFile(packageInstallDirectory + "/[Content_Types].xml");

			// Unity has no use for the build directory
			DeleteDirectory(packageInstallDirectory + "/build");

			// For now, delete src.  We may use it later...
			DeleteDirectory(packageInstallDirectory + "/src");

			// Since we don't automatically fix up the runtime dll platforms, remove them until we improve support
			// for this newer feature of nuget packages.
			DeleteDirectory(Path.Combine(packageInstallDirectory, "runtimes"));

			// Delete documentation folders since they sometimes have HTML docs with JavaScript, which Unity tried to parse as "UnityScript"
			DeleteDirectory(packageInstallDirectory + "/docs");

			if (Directory.Exists(packageInstallDirectory + "/lib"))
			{
				var intDotNetVersion = (int)DotNetVersion; // c
				//bool using46 = DotNetVersion == ApiCompatibilityLevel.NET_4_6; // NET_4_6 option was added in Unity 5.6
				var using46 = intDotNetVersion == 3; // NET_4_6 = 3 in Unity 5.6 and Unity 2017.1 - use the hard-coded int value to ensure it works in earlier versions of Unity
				var usingStandard2 = intDotNetVersion == 6; // using .net standard 2.0                

				var selectedDirectories = new List<string>();

				// go through the library folders in descending order (highest to lowest version)
				var libDirectories = Directory.GetDirectories(packageInstallDirectory + "/lib").Select(s => new DirectoryInfo(s))
											.OrderByDescending(di => di.Name.ToLower()).ToList();
				foreach (var directory in libDirectories)
				{
					var directoryName = directory.Name.ToLower();

					// Select the highest .NET library available that is supported
					// See here: https://docs.nuget.org/ndocs/schema/target-frameworks
					if (usingStandard2 && directoryName == "netstandard2.0")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (usingStandard2 && directoryName == "netstandard1.6")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (using46 && directoryName == "net462")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (usingStandard2 && directoryName == "netstandard1.5")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (using46 && directoryName == "net461")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (usingStandard2 && directoryName == "netstandard1.4")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (using46 && directoryName == "net46")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (usingStandard2 && directoryName == "netstandard1.3")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (using46 && directoryName == "net452")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (using46 && directoryName == "net451")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (usingStandard2 && directoryName == "netstandard1.2")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (using46 && directoryName == "net45")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (usingStandard2 && directoryName == "netstandard1.1")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (usingStandard2 && directoryName == "netstandard1.0")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (using46 && directoryName == "net403")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (using46 && (directoryName == "net40" || directoryName == "net4"))
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (
						directoryName == "unity" ||
						directoryName == "net35-unity full v3.5" ||
						directoryName == "net35-unity subset v3.5")
					{
						// Keep all directories targeting Unity within a package
						var parentName = directory.Parent.FullName;
						selectedDirectories.Add(Path.Combine(parentName, "unity"));
						selectedDirectories.Add(Path.Combine(parentName, "net35-unity full v3.5"));
						selectedDirectories.Add(Path.Combine(parentName, "net35-unity subset v3.5"));
						break;
					}
					else if (directoryName == "net35")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (directoryName == "net20")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
					else if (directoryName == "net11")
					{
						selectedDirectories.Add(directory.FullName);
						break;
					}
				}


				foreach (var dir in selectedDirectories)
				{
					LogVerbose("Using {0}", dir);
				}

				// delete all of the libaries except for the selected one
				foreach (var directory in libDirectories)
				{
					if (!selectedDirectories.Contains(directory.FullName))
					{
						DeleteDirectory(directory.FullName);
					}
				}
			}

			if (Directory.Exists(packageInstallDirectory + "/tools"))
			{
				// Move the tools folder outside of the Unity Assets folder
				var toolsInstallDirectory = Path.Combine(Application.dataPath, $"../Packages/{package.Id}.{package.Version}/tools");

				LogVerbose("Moving {0} to {1}", packageInstallDirectory + "/tools", toolsInstallDirectory);

				// create the directory to create any of the missing folders in the path
				Directory.CreateDirectory(toolsInstallDirectory);

				// delete the final directory to prevent the Move operation from throwing exceptions.
				DeleteDirectory(toolsInstallDirectory);

				Directory.Move(packageInstallDirectory + "/tools", toolsInstallDirectory);
			}

			// delete all PDB files since Unity uses Mono and requires MDB files, which causes it to output "missing MDB" errors
			DeleteAllFiles(packageInstallDirectory, "*.pdb");

			// if there are native DLLs, copy them to the Unity project root (1 up from Assets)
			if (Directory.Exists(packageInstallDirectory + "/output"))
			{
				var files = Directory.GetFiles(packageInstallDirectory + "/output");
				foreach (var file in files)
				{
					var newFilePath = Directory.GetCurrentDirectory() + "/" + Path.GetFileName(file);
					LogVerbose("Moving {0} to {1}", file, newFilePath);
					DeleteFile(newFilePath);
					File.Move(file, newFilePath);
				}

				LogVerbose("Deleting {0}", packageInstallDirectory + "/output");

				DeleteDirectory(packageInstallDirectory + "/output");
			}

			// if there are Unity plugin DLLs, copy them to the Unity Plugins folder (Assets/Plugins)
			if (Directory.Exists(packageInstallDirectory + "/unityplugin"))
			{
				var pluginsDirectory = Application.dataPath + "/Plugins/";

				if (!Directory.Exists(pluginsDirectory))
				{
					Directory.CreateDirectory(pluginsDirectory);
				}

				var files = Directory.GetFiles(packageInstallDirectory + "/unityplugin");
				foreach (var file in files)
				{
					var newFilePath = pluginsDirectory + Path.GetFileName(file);

					try
					{
						LogVerbose("Moving {0} to {1}", file, newFilePath);
						DeleteFile(newFilePath);
						File.Move(file, newFilePath);
					}
					catch (UnauthorizedAccessException)
					{
						Debug.LogWarningFormat("{0} couldn't be overwritten. It may be a native plugin already locked by Unity. Please close Unity and manually delete it.", newFilePath);
					}
				}

				LogVerbose("Deleting {0}", packageInstallDirectory + "/unityplugin");

				DeleteDirectory(packageInstallDirectory + "/unityplugin");
			}

			// if there are Unity StreamingAssets, copy them to the Unity StreamingAssets folder (Assets/StreamingAssets)
			if (Directory.Exists(packageInstallDirectory + "/StreamingAssets"))
			{
				var streamingAssetsDirectory = Application.dataPath + "/StreamingAssets/";

				if (!Directory.Exists(streamingAssetsDirectory))
				{
					Directory.CreateDirectory(streamingAssetsDirectory);
				}

				// move the files
				var files = Directory.GetFiles(packageInstallDirectory + "/StreamingAssets");
				foreach (var file in files)
				{
					var newFilePath = streamingAssetsDirectory + Path.GetFileName(file);

					try
					{
						LogVerbose("Moving {0} to {1}", file, newFilePath);
						DeleteFile(newFilePath);
						File.Move(file, newFilePath);
					}
					catch (Exception e)
					{
						Debug.LogWarningFormat("{0} couldn't be moved. \n{1}", newFilePath, e.ToString());
					}
				}

				// move the directories
				var directories = Directory.GetDirectories(packageInstallDirectory + "/StreamingAssets");
				foreach (var directory in directories)
				{
					var newDirectoryPath = streamingAssetsDirectory + new DirectoryInfo(directory).Name;

					try
					{
						LogVerbose("Moving {0} to {1}", directory, newDirectoryPath);
						if (Directory.Exists(newDirectoryPath))
						{
							DeleteDirectory(newDirectoryPath);
						}

						Directory.Move(directory, newDirectoryPath);
					}
					catch (Exception e)
					{
						Debug.LogWarningFormat("{0} couldn't be moved. \n{1}", newDirectoryPath, e.ToString());
					}
				}

				// delete the package's StreamingAssets folder and .meta file
				LogVerbose("Deleting {0}", packageInstallDirectory + "/StreamingAssets");
				DeleteDirectory(packageInstallDirectory + "/StreamingAssets");
				DeleteFile(packageInstallDirectory + "/StreamingAssets.meta");
			}
		}

		/// <summary>
		/// Calls "nuget.exe pack" to create a .nupkg file based on the given .nuspec file.
		/// </summary>
		/// <param name="nuspecFilePath">The full filepath to the .nuspec file to use.</param>
		public static void Pack(string nuspecFilePath)
		{
			if (!Directory.Exists(PackOutputDirectory))
			{
				Directory.CreateDirectory(PackOutputDirectory);
			}

			// Use -NoDefaultExcludes to allow files and folders that start with a . to be packed into the package
			// This is done because if you want a file/folder in a Unity project, but you want Unity to ignore it, it must start with a .
			// This is especially useful for .cs and .js files that you don't want Unity to compile as game scripts
			var arguments = $"pack \"{nuspecFilePath}\" -OutputDirectory \"{PackOutputDirectory}\" -NoDefaultExcludes";

			RunNugetProcess(arguments);
		}

		/// <summary>
		/// Calls "nuget.exe push" to push a .nupkf file to the the server location defined in the NuGet.config file.
		/// Note: This differs slightly from NuGet's Push command by automatically calling Pack if the .nupkg doesn't already exist.
		/// </summary>
		/// <param name="nuspec">The NuspecFile which defines the package to push.  Only the ID and Version are used.</param>
		/// <param name="nuspecFilePath">The full filepath to the .nuspec file to use.  This is required by NuGet's Push command.</param>
		/// /// <param name="apiKey">The API key to use when pushing a package to the server.  This is optional.</param>
		public static void Push(NuspecFile nuspec, string nuspecFilePath, string apiKey = "")
		{
			var packagePath = Path.Combine(PackOutputDirectory, $"{nuspec.Id}.{nuspec.Version}.nupkg");
			if (!File.Exists(packagePath))
			{
				LogVerbose("Attempting to Pack.");
				Pack(nuspecFilePath);

				if (!File.Exists(packagePath))
				{
					Debug.LogErrorFormat("NuGet package not found: {0}", packagePath);
					return;
				}
			}

			var arguments = $"push \"{packagePath}\" {apiKey} -configfile \"{NugetConfigFilePath}\"";

			RunNugetProcess(arguments);
		}

		/// <summary>
		/// Recursively deletes the folder at the given path.
		/// NOTE: Directory.Delete() doesn't delete Read-Only files, whereas this does.
		/// </summary>
		/// <param name="directoryPath">The path of the folder to delete.</param>
		private static void DeleteDirectory(string directoryPath)
		{
			if (!Directory.Exists(directoryPath))
				return;

			var directoryInfo = new DirectoryInfo(directoryPath);

			// delete any sub-folders first
			foreach (var childInfo in directoryInfo.GetFileSystemInfos())
			{
				DeleteDirectory(childInfo.FullName);
			}

			// remove the read-only flag on all files
			var files = directoryInfo.GetFiles();
			foreach (var file in files)
			{
				file.Attributes = FileAttributes.Normal;
			}

			// remove the read-only flag on the directory
			directoryInfo.Attributes = FileAttributes.Normal;

			// recursively delete the directory
			directoryInfo.Delete(true);
		}

		/// <summary>
		/// Deletes a file at the given filepath.
		/// </summary>
		/// <param name="filePath">The filepath to the file to delete.</param>
		private static void DeleteFile(string filePath)
		{
			if (File.Exists(filePath))
			{
				File.SetAttributes(filePath, FileAttributes.Normal);
				File.Delete(filePath);
			}
		}

		/// <summary>
		/// Deletes all files in the given directory or in any sub-directory, with the given extension.
		/// </summary>
		/// <param name="directoryPath">The path to the directory to delete all files of the given extension from.</param>
		/// <param name="extension">The extension of the files to delete, in the form "*.ext"</param>
		private static void DeleteAllFiles(string directoryPath, string extension)
		{
			var files = Directory.GetFiles(directoryPath, extension, SearchOption.AllDirectories);
			foreach (var file in files)
			{
				DeleteFile(file);
			}
		}

		/// <summary>
		/// Uninstalls all of the currently installed packages.
		/// </summary>
		internal static void UninstallAll()
		{
			foreach (var package in installedPackages.Values.ToList())
			{
				Uninstall(package, false);
			}

			AssetDatabase.Refresh();
		}

		/// <summary>
		/// "Uninstalls" the given package by simply deleting its folder.
		/// </summary>
		/// <param name="package">The NugetPackage to uninstall.</param>
		/// <param name="refreshAssets">True to force Unity to refesh its Assets folder.  False to temporarily ignore the change.  Defaults to true.</param>
		public static void Uninstall(NugetPackageIdentifier package, bool refreshAssets = true, bool deleteDependencies = false)
		{
			LogVerbose("Uninstalling: {0} {1}", package.Id, package.Version);

			var foundPackage = package as NugetPackage ?? GetSpecificPackage(package);

			// update the package.config file
			PackagesConfigFile.RemovePackage(foundPackage);
			PackagesConfigFile.Save(PackagesConfigFilePath);

			var packageInstallDirectory = Path.Combine(NugetConfigFile.RepositoryPath, $"{foundPackage.Id}.{foundPackage.Version}");
			DeleteDirectory(packageInstallDirectory);

			var metaFile = Path.Combine(NugetConfigFile.RepositoryPath, $"{foundPackage.Id}.{foundPackage.Version}.meta");
			DeleteFile(metaFile);

			var toolsInstallDirectory = Path.Combine(Application.dataPath, $"../Packages/{foundPackage.Id}.{foundPackage.Version}");
			DeleteDirectory(toolsInstallDirectory);

			installedPackages.Remove(foundPackage.Id);

			if (deleteDependencies)
			{
				foreach (var dependency in foundPackage.Dependencies)
				{
					var actualData = PackagesConfigFile.Packages.Find(pkg => pkg.Id == dependency.Id);
					if (actualData == null || actualData.IsManuallyInstalled) continue;

					var hasMoreParents = false;
					foreach (var pkg in installedPackages.Values)
					{
						foreach (var dep in pkg.Dependencies)
						{
							if (dep.Id != dependency.Id) continue;
							hasMoreParents = true;
							break;
						}

						if (hasMoreParents) break;
					}

					if (!hasMoreParents) Uninstall(dependency, false, true);
				}
			}

			if (refreshAssets)
				AssetDatabase.Refresh();
		}

		/// <summary>
		/// Updates a package by uninstalling the currently installed version and installing the "new" version.
		/// </summary>
		/// <param name="currentVersion">The current package to uninstall.</param>
		/// <param name="newVersion">The package to install.</param>
		/// <param name="refreshAssets">True to refresh the assets inside Unity.  False to ignore them (for now).  Defaults to true.</param>
		public static bool Update(NugetPackageIdentifier currentVersion, NugetPackage newVersion, bool refreshAssets = true)
		{
			LogVerbose("Updating {0} {1} to {2}", currentVersion.Id, currentVersion.Version, newVersion.Version);
			Uninstall(currentVersion, false);
			newVersion.IsManuallyInstalled = currentVersion.IsManuallyInstalled;
			return InstallIdentifier(newVersion, refreshAssets);
		}

		/// <summary>
		/// Installs all of the given updates, and uninstalls the corresponding package that is already installed.
		/// </summary>
		/// <param name="updates">The list of all updates to install.</param>
		/// <param name="packagesToUpdate">The list of all packages currently installed.</param>
		public static void UpdateAll(List<NugetPackage> updates, ICollection<NugetPackage> packagesToUpdate)
		{
			var progressStep = 1.0f / updates.Count;
			float currentProgress = 0;

			foreach (var update in updates)
			{
				EditorUtility.DisplayProgressBar($"Updating to {update.Id} {update.Version}", "Installing All Updates", currentProgress);

				var installedPackage = packagesToUpdate.FirstOrDefault(p => p.Id == update.Id);
				if (installedPackage != null)
				{
					Update(installedPackage, update, false);
				}
				else
				{
					Debug.LogErrorFormat("Trying to update {0} to {1}, but no version is installed!", update.Id, update.Version);
				}

				currentProgress += progressStep;
			}

			AssetDatabase.Refresh();

			EditorUtility.ClearProgressBar();
		}

		/// <summary>
		/// Gets the dictionary of packages that are actually installed in the project, keyed off of the ID.
		/// </summary>
		/// <returns>A dictionary of installed <see cref="NugetPackage"/>s.</returns>
		public static ICollection<NugetPackage> InstalledPackages => installedPackages.Values;

		/// <summary>
		/// Updates the dictionary of packages that are actually installed in the project based on the files that are currently installed.
		/// </summary>
		public static void UpdateInstalledPackages()
		{
			var stopwatch = new Stopwatch();
			stopwatch.Start();

			installedPackages.Clear();

			// loops through the packages that are actually installed in the project
			if (Directory.Exists(NugetConfigFile.RepositoryPath))
			{
				// a package that was installed via NuGet will have the .nupkg it came from inside the folder
				var nupkgFiles = Directory.GetFiles(NugetConfigFile.RepositoryPath, "*.nupkg", SearchOption.AllDirectories);
				foreach (var nupkgFile in nupkgFiles)
				{
					var package = NugetPackage.FromNupkgFile(nupkgFile);
					if (!installedPackages.ContainsKey(package.Id))
					{
						var actualData = PackagesConfigFile.Packages.Find(pkg => pkg.Id == package.Id);
						if (actualData != null) package.IsManuallyInstalled = actualData.IsManuallyInstalled;
						installedPackages.Add(package.Id, package);
					}
					else
					{
						Debug.LogErrorFormat("Package is already in installed list: {0}", package.Id);
					}
				}

				// if the source code & assets for a package are pulled directly into the project (ex: via a symlink/junction) it should have a .nuspec defining the package
				var nuspecFiles = Directory.GetFiles(NugetConfigFile.RepositoryPath, "*.nuspec", SearchOption.AllDirectories);
				foreach (var nuspecFile in nuspecFiles)
				{
					var package = NugetPackage.FromNuspec(NuspecFile.Load(nuspecFile));
					if (!installedPackages.ContainsKey(package.Id))
					{
						var actualData = PackagesConfigFile.Packages.Find(pkg => pkg.Id == package.Id);
						if (actualData != null) package.IsManuallyInstalled = actualData.IsManuallyInstalled;
						installedPackages.Add(package.Id, package);
					}
				}
			}

			stopwatch.Stop();
			LogVerbose("Getting installed packages took {0} ms", stopwatch.ElapsedMilliseconds);
		}

		/// <summary>
		/// Gets a list of NuGetPackages via the HTTP Search() function defined by NuGet.Server and NuGet Gallery.
		/// This allows searching for partial IDs or even the empty string (the default) to list ALL packages.
		/// 
		/// NOTE: See the functions and parameters defined here: https://www.nuget.org/api/v2/$metadata
		/// </summary>
		/// <param name="searchTerm">The search term to use to filter packages. Defaults to the empty string.</param>
		/// <param name="includeAllVersions">True to include older versions that are not the latest version.</param>
		/// <param name="includePrerelease">True to include prerelease packages (alpha, beta, etc).</param>
		/// <param name="numberToGet">The number of packages to fetch.</param>
		/// <param name="numberToSkip">The number of packages to skip before fetching.</param>
		/// <returns>The list of available packages.</returns>
		public static List<NugetPackage> Search(string searchTerm = "", bool includeAllVersions = false, bool includePrerelease = false,
												int numberToGet = 15, int numberToSkip = 0)
		{
			var packages = new List<NugetPackage>();

			// Loop through all active sources and combine them into a single list
			foreach (var source in packageSources.Where(s => s.IsEnabled))
			{
				var newPackages = source.Search(searchTerm, includeAllVersions, includePrerelease, numberToGet, numberToSkip);
				packages.AddRange(newPackages);
				packages = packages.Distinct().ToList();
			}

			return packages;
		}

		/// <summary>
		/// Queries the server with the given list of installed packages to get any updates that are available.
		/// </summary>
		/// <param name="packagesToUpdate">The list of currently installed packages.</param>
		/// <param name="includePrerelease">True to include prerelease packages (alpha, beta, etc).</param>
		/// <param name="includeAllVersions">True to include older versions that are not the latest version.</param>
		/// <param name="targetFrameworks">The specific frameworks to target?</param>
		/// <param name="versionContraints">The version constraints?</param>
		/// <returns>A list of all updates available.</returns>
		public static List<NugetPackage> GetUpdates(ICollection<NugetPackage> packagesToUpdate, bool includePrerelease = false,
													bool includeAllVersions = false, string targetFrameworks = "",
													string versionContraints = "")
		{
			var packages = new List<NugetPackage>();

			// Loop through all active sources and combine them into a single list
			foreach (var source in packageSources.Where(s => s.IsEnabled))
			{
				var newPackages = source.GetUpdates(packagesToUpdate, includePrerelease, includeAllVersions, targetFrameworks, versionContraints);
				packages.AddRange(newPackages);
				packages = packages.Distinct().ToList();
			}

			return packages;
		}

		/// <summary>
		/// Gets a NugetPackage from the NuGet server with the exact ID and Version given.
		/// If an exact match isn't found, it selects the next closest version available.
		/// </summary>
		/// <param name="packageId">The <see cref="NugetPackageIdentifier"/> containing the ID and Version of the package to get.</param>
		/// <returns>The retrieved package, if there is one.  Null if no matching package was found.</returns>
		private static NugetPackage GetSpecificPackage(NugetPackageIdentifier packageId)
		{
			// First look to see if the package is already installed
			var package = GetInstalledPackage(packageId);

			if (package == null)
			{
				// That package isn't installed yet, so look in the cache next
				package = GetCachedPackage(packageId);
			}

			if (package == null)
			{
				// It's not in the cache, so we need to look in the active sources
				package = GetOnlinePackage(packageId);
			}

			return package;
		}

		/// <summary>
		/// Tries to find an already installed package that matches (or is in the range of) the given package ID.
		/// </summary>
		/// <param name="packageId">The <see cref="NugetPackageIdentifier"/> of the <see cref="NugetPackage"/> to find.</param>
		/// <returns>The best <see cref="NugetPackage"/> match, if there is one, otherwise null.</returns>
		private static NugetPackage GetInstalledPackage(NugetPackageIdentifier packageId)
		{
			if (installedPackages.TryGetValue(packageId.Id, out var installedPackage))
			{
				if (packageId.Version != installedPackage.Version)
				{
					if (packageId.InRange(installedPackage))
					{
						LogVerbose("Requested {0} {1}, but {2} is already installed, so using that.", packageId.Id, packageId.Version, installedPackage.Version);
					}
					else
					{
						LogVerbose("Requested {0} {1}. {2} is already installed, but it is out of range.", packageId.Id, packageId.Version, installedPackage.Version);
						installedPackage = null;
					}
				}
				else
				{
					LogVerbose("Found exact package already installed: {0} {1}", installedPackage.Id, installedPackage.Version);
				}
			}


			return installedPackage;
		}

		/// <summary>
		/// Tries to find an already cached package that matches the given package ID.
		/// </summary>
		/// <param name="packageId">The <see cref="NugetPackageIdentifier"/> of the <see cref="NugetPackage"/> to find.</param>
		/// <returns>The best <see cref="NugetPackage"/> match, if there is one, otherwise null.</returns>
		private static NugetPackage GetCachedPackage(NugetPackageIdentifier packageId)
		{
			NugetPackage package = null;

			if (NugetConfigFile.InstallFromCache)
			{
				var cachedPackagePath = Path.Combine(PackOutputDirectory, $"./{packageId.Id}.{packageId.Version}.nupkg");

				if (File.Exists(cachedPackagePath))
				{
					LogVerbose("Found exact package in the cache: {0}", cachedPackagePath);
					package = NugetPackage.FromNupkgFile(cachedPackagePath);
				}
			}

			return package;
		}

		/// <summary>
		/// Tries to find an "online" (in the package sources - which could be local) package that matches (or is in the range of) the given package ID.
		/// </summary>
		/// <param name="packageId">The <see cref="NugetPackageIdentifier"/> of the <see cref="NugetPackage"/> to find.</param>
		/// <returns>The best <see cref="NugetPackage"/> match, if there is one, otherwise null.</returns>
		private static NugetPackage GetOnlinePackage(NugetPackageIdentifier packageId)
		{
			NugetPackage package = null;

			// Loop through all active sources and stop once the package is found
			foreach (var source in packageSources.Where(s => s.IsEnabled))
			{
				var foundPackage = source.GetSpecificPackage(packageId);
				if (foundPackage == null)
				{
					continue;
				}

				if (foundPackage.Version == packageId.Version)
				{
					LogVerbose("{0} {1} was found in {2}", foundPackage.Id, foundPackage.Version, source.Name);
					return foundPackage;
				}

				LogVerbose("{0} {1} was found in {2}, but wanted {3}", foundPackage.Id, foundPackage.Version, source.Name, packageId.Version);
				if (package == null)
				{
					// if another package hasn't been found yet, use the current found one
					package = foundPackage;
				}
				// another package has been found previously, but neither match identically
				else if (foundPackage > package)
				{
					// use the new package if it's closer to the desired version
					package = foundPackage;
				}
			}

			if (package != null)
			{
				LogVerbose("{0} {1} not found, using {2}", packageId.Id, packageId.Version, package.Version);
			}
			else
			{
				LogVerbose("Failed to find {0} {1}", packageId.Id, packageId.Version);
			}

			return package;
		}

		/// <summary>
		/// Copies the contents of input to output. Doesn't close either stream.
		/// </summary>
		private static void CopyStream(Stream input, Stream output)
		{
			var buffer = new byte[8 * 1024];
			int len;
			while ((len = input.Read(buffer, 0, buffer.Length)) > 0)
			{
				output.Write(buffer, 0, len);
			}
		}

		/// <summary>
		/// Installs the package given by the identifer.  It fetches the appropriate full package from the installed packages, package cache, or package sources and installs it.
		/// </summary>
		/// <param name="package">The identifer of the package to install.</param>
		/// <param name="refreshAssets">True to refresh the Unity asset database.  False to ignore the changes (temporarily).</param>
		internal static bool InstallIdentifier(NugetPackageIdentifier package, bool refreshAssets = true)
		{
			var foundPackage = GetSpecificPackage(package);

			if (foundPackage != null)
			{
				foundPackage.IsManuallyInstalled = package.IsManuallyInstalled;
				return Install(foundPackage, refreshAssets);
			}
			else
			{
				Debug.LogErrorFormat("Could not find {0} {1} or greater.", package.Id, package.Version);
				return false;
			}
		}

		/// <summary>
		/// Outputs the given message to the log only if verbose mode is active.  Otherwise it does nothing.
		/// </summary>
		/// <param name="format">The formatted message string.</param>
		/// <param name="args">The arguments for the formattted message string.</param>
		public static void LogVerbose(string format, params object[] args)
		{
			if (NugetConfigFile.Verbose)
			{
				var stackTraceLogType = Application.GetStackTraceLogType(LogType.Log);
				Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
				Debug.LogFormat(format, args);

				Application.SetStackTraceLogType(LogType.Log, stackTraceLogType);
			}
		}

		/// <summary>
		/// Installs the given package.
		/// </summary>
		/// <param name="package">The package to install.</param>
		/// <param name="refreshAssets">True to refresh the Unity asset database.  False to ignore the changes (temporarily).</param>
		public static bool Install(NugetPackage package, bool refreshAssets = true)
		{
			if (installedPackages.TryGetValue(package.Id, out var installedPackage))
			{
				if (installedPackage < package)
				{
					LogVerbose("{0} {1} is installed, but need {2} or greater. Updating to {3}", installedPackage.Id,
								installedPackage.Version, package.Version, package.Version);
					return Update(installedPackage, package, false);
				}
				else if (installedPackage > package)
				{
					LogVerbose("{0} {1} is installed. {2} or greater is needed, so using installed version.", installedPackage.Id,
								installedPackage.Version, package.Version);
				}
				else
				{
					LogVerbose("Already installed: {0} {1}", package.Id, package.Version);
				}

				return true;
			}

			bool installSuccess;
			try
			{
				LogVerbose("Installing: {0} {1}", package.Id, package.Version);

				// look to see if the package (any version) is already installed


				if (refreshAssets)
					EditorUtility.DisplayProgressBar($"Installing {package.Id} {package.Version}", "Installing Dependencies", 0.1f);

				// install all dependencies
				foreach (var dependency in package.Dependencies)
				{
					LogVerbose("Installing Dependency: {0} {1}", dependency.Id, dependency.Version);
					var installed = InstallIdentifier(dependency);
					if (!installed)
					{
						throw new Exception($"Failed to install dependency: {dependency.Id} {dependency.Version}.");
					}
				}

				// update packages.config
				PackagesConfigFile.AddPackage(package);
				PackagesConfigFile.Save(PackagesConfigFilePath);

				var cachedPackagePath = Path.Combine(PackOutputDirectory, $"./{package.Id}.{package.Version}.nupkg");
				if (NugetConfigFile.InstallFromCache && File.Exists(cachedPackagePath))
				{
					LogVerbose("Cached package found for {0} {1}", package.Id, package.Version);
				}
				else
				{
					if (package.PackageSource.IsLocalPath)
					{
						LogVerbose("Caching local package {0} {1}", package.Id, package.Version);

						// copy the .nupkg from the local path to the cache
						File.Copy(Path.Combine(package.PackageSource.ExpandedPath, $"./{package.Id}.{package.Version}.nupkg"), cachedPackagePath, true);
					}
					else
					{
						// Mono doesn't have a Certificate Authority, so we have to provide all validation manually.  Currently just accept anything.
						// See here: http://stackoverflow.com/questions/4926676/mono-webrequest-fails-with-https

						// remove all handlers
						//if (ServicePointManager.ServerCertificateValidationCallback != null)
						//    foreach (var d in ServicePointManager.ServerCertificateValidationCallback.GetInvocationList())
						//        ServicePointManager.ServerCertificateValidationCallback -= (d as System.Net.Security.RemoteCertificateValidationCallback);
						ServicePointManager.ServerCertificateValidationCallback = null;

						// add anonymous handler
						ServicePointManager.ServerCertificateValidationCallback += (sender, certificate, chain, policyErrors) => true;

						LogVerbose("Downloading package {0} {1}", package.Id, package.Version);

						if (refreshAssets)
							EditorUtility.DisplayProgressBar($"Installing {package.Id} {package.Version}", "Downloading Package", 0.3f);

						var objStream = RequestUrl(package.DownloadUrl, package.PackageSource.UserName,
													package.PackageSource.ExpandedPassword, timeOut: null);
						using (Stream file = File.Create(cachedPackagePath))
						{
							CopyStream(objStream, file);
						}
					}
				}

				if (refreshAssets)
					EditorUtility.DisplayProgressBar($"Installing {package.Id} {package.Version}", "Extracting Package", 0.6f);

				string initTemplatePath = null;

				if (File.Exists(cachedPackagePath))
				{
					var baseDirectory = Path.Combine(NugetConfigFile.RepositoryPath, $"{package.Id}.{package.Version}");

					// unzip the package
					using (var zip = ZipFile.OpenRead(cachedPackagePath))
					{
						foreach (var entry in zip.Entries)
						{
							var destPath = Path.Combine(baseDirectory, entry.FullName);
							// Skip entries that want to unpack somewhere outside our destination
							if (!destPath.StartsWith(baseDirectory, StringComparison.Ordinal)) continue;
							var dirName = Path.GetDirectoryName(destPath);
							if (dirName != null && !Directory.Exists(dirName)) Directory.CreateDirectory(dirName);
							entry.ExtractToFile(destPath, true);
							if (entry.FullName == "Init.template") initTemplatePath = destPath;
							if (NugetConfigFile.ReadOnlyPackageFiles)
							{
								var extractedFile = new FileInfo(destPath);
								extractedFile.Attributes |= FileAttributes.ReadOnly;
							}
						}
					}

					// copy the .nupkg inside the Unity project
					File.Copy(cachedPackagePath, Path.Combine(NugetConfigFile.RepositoryPath, $"{package.Id}.{package.Version}/{package.Id}.{package.Version}.nupkg"), true);
				}
				else
				{
					Debug.LogErrorFormat("File not found: {0}", cachedPackagePath);
				}

				if (refreshAssets)
					EditorUtility.DisplayProgressBar($"Installing {package.Id} {package.Version}", "Cleaning Package", 0.9f);

				try
				{
					if (initTemplatePath != null)
					{
						ProcessInitTemplate(initTemplatePath, package);
						DeleteFile(initTemplatePath);
						DeleteFile(initTemplatePath + ".meta");
					}
				}
				catch (Exception e)
				{
					Debug.LogError("Failed processing init template " + e);
				}

				// clean
				Clean(package);

				// update the installed packages list
				installedPackages.Add(package.Id, package);
				installSuccess = true;
			}
			catch (Exception e)
			{
				Debug.LogErrorFormat("Unable to install package {0} {1}\n{2}", package.Id, package.Version, e.ToString());
				installSuccess = false;
			}
			finally
			{
				if (refreshAssets)
				{
					EditorUtility.DisplayProgressBar($"Installing {package.Id} {package.Version}", "Importing Package", 0.95f);
					AssetDatabase.Refresh();
					EditorUtility.ClearProgressBar();
				}
			}

			return installSuccess;
		}

		private static void ProcessInitTemplate(string initTemplatePath, NugetPackage package)
		{
			string LoadInitClassFile(string path, string name)
			{
				if (File.Exists(path)) return File.ReadAllText(path);

				var stream = typeof(NugetHelper).Assembly.GetManifestResourceStream("CreateDLL..Templates." + name);
				if (stream == null)
				{
					Debug.LogError("Failed to load embedded CreateDLL..Templates." + name);
					return null;
				}

				using (var streamReader = new StreamReader(stream, Encoding.UTF8))
				{
					return streamReader.ReadToEnd();
				}
			}

			var initCsDir = Path.Combine(Application.dataPath, "Scripts/Initialization");
			var initCsPath = Path.Combine(initCsDir, "AppInitializer.cs");
			var generatedInitCsPath = Path.Combine(initCsDir, "AppInitializer.Generated.cs");


			var initCs = LoadInitClassFile(initCsPath, "AppInitializer.cs");
			var generatedInitCs = LoadInitClassFile(generatedInitCsPath, "AppInitializer.Generated.cs");

			if (initCs == null || generatedInitCs == null) return;
			
			var packageId = package.Id.Replace("nordeus.", "").Replace("unity.", "");
			packageId = packageId.Substring(0, 1).ToUpper() + packageId.Substring(1);
			
			var initMethodName = "Init" + packageId;
			// If init code for this package already exists skip injection
			if (generatedInitCs.Contains("\t" + initMethodName + "()")) return;

			// We guarantee Windows line breaks
			var newLinesRegex = new Regex(@"\r\n?|\n");

			initCs = newLinesRegex.Replace(initCs, "\r\n");
			generatedInitCsPath = newLinesRegex.Replace(generatedInitCsPath, "\r\n");
			
			var initTemplate = File.ReadAllLines(initTemplatePath);

			// Init template file should be written like this:
			// InitDependencies: a, b, c <in case the init code depends on packages the package itself doesn't depend on>
			// Uses: use1, use2 <using clauses that need to exist at the top of the file>
			// CustomExceptionLogging: <code to insert into CustomExceptionLogging if package wants to handle init exceptions>
			// {...}
			// InitCode:|SceneInitCode: <this is the only required section>
			// {...}

			const string INIT_DEPENDENCIES_KEY = "InitDependencies:";
			const string USES_KEY = "Uses:";
			const string CUSTOM_EXCEPTION_LOGGING_KEY = "CustomExceptionLogging:";
			const string INIT_CODE_KEY = "InitCode:";
			const string INIT_SCENE_CODE_KEY = "SceneInitCode:";

			var dependencies = package.Dependencies.Select(identifier => identifier.Id.Replace("nordeus.", "").Replace("unity.", "")).ToList();
			var uses = new List<string>();
			var customExceptionLoggingCode = "";
			var initCode = "";

			var line = 0;
			var initSceneCodeFound = false;
			for (; line < initTemplate.Length; line++)
			{
				if (initTemplate[line].StartsWith(INIT_DEPENDENCIES_KEY))
				{
					var additionalDeps = initTemplate[line].Substring(INIT_DEPENDENCIES_KEY.Length).Split(new []{','}, StringSplitOptions.RemoveEmptyEntries);
					foreach (var additionalDep in additionalDeps)
					{
						dependencies.Add(additionalDep.Trim());
					}
					continue;
				}

				if (initTemplate[line].StartsWith(USES_KEY))
				{
					var useModules = initTemplate[line].Substring(USES_KEY.Length).Split(new []{','}, StringSplitOptions.RemoveEmptyEntries);
					foreach (var useModule in useModules)
					{
						uses.Add(useModule.Trim());
					}
					continue;
				}

				if (initTemplate[line].StartsWith(CUSTOM_EXCEPTION_LOGGING_KEY))
				{
					line++;
					while (line + 1 < initTemplate.Length && !initTemplate[line + 1].StartsWith("}"))
					{
						line++;
						customExceptionLoggingCode += "\t\t" + initTemplate[line] + "\r\n";
					}
					continue;
				}

				var initCodeFound = initTemplate[line].StartsWith(INIT_CODE_KEY);
				initSceneCodeFound = initTemplate[line].StartsWith(INIT_SCENE_CODE_KEY);
				if (initCodeFound || initSceneCodeFound)
				{
					line++;
					while (line < initTemplate.Length)
					{
						initCode += "\t\t" + initTemplate[line] + "\r\n";
						line++;
					}
				}
			}

			foreach (var use in uses)
			{
				var usingLine = "using " + use + ";";
				if (initCs.Contains(usingLine)) continue;
				initCs = usingLine + "\r\n" + initCs;
			}

			var insertPos = 0;
			
			if (initSceneCodeFound)
			{
				insertPos = generatedInitCs.LastIndexOf("\t\t}", StringComparison.Ordinal);
			}
			else
			{
				foreach (var dependency in dependencies)
				{
					var depMethod = "Init" + dependency.Substring(0, 1).ToUpper() + dependency.Substring(1);
					var depIndex = generatedInitCs.IndexOf(depMethod, StringComparison.Ordinal);
					if (depIndex < 0) continue;
					var newInsertPos = generatedInitCs.IndexOf("\r\n", depIndex, StringComparison.Ordinal) + 2;
					if (newInsertPos > insertPos) insertPos = newInsertPos;
				}

				if (insertPos == 0)
				{
					const string NonSceneInit = "private static void DoNonSceneInits()\r\n\t\t{\r\n";
					insertPos = generatedInitCs.IndexOf(NonSceneInit, StringComparison.Ordinal) + NonSceneInit.Length;
				}
			}

			generatedInitCs = generatedInitCs.Substring(0, insertPos) + "\t\t\t" + initMethodName + "();\r\n" + generatedInitCs.Substring(insertPos);

			if (customExceptionLoggingCode.Length > 0)
			{
				insertPos = initCs.IndexOf("\t\t}", StringComparison.Ordinal);
				initCs = initCs.Substring(0, insertPos) + customExceptionLoggingCode + initCs.Substring(insertPos);
			}

			initCode = "\r\n\t\tprivate static void " + initMethodName + "()\r\n" + initCode;
			insertPos = initCs.LastIndexOf("\t}", StringComparison.Ordinal);
			initCs = initCs.Substring(0, insertPos) + initCode + initCs.Substring(insertPos);
			
			// Make sure the dir exists
			Directory.CreateDirectory(initCsDir);

			File.WriteAllText(initCsPath, initCs);
			File.WriteAllText(generatedInitCsPath, generatedInitCs);
		}

		private struct AuthenticatedFeed
		{
			public string AccountUrlPattern;
			public string ProviderUrlTemplate;

			public string GetAccount(string url)
			{
				var match = Regex.Match(url, AccountUrlPattern, RegexOptions.IgnoreCase);
				if (!match.Success) { return null; }

				return match.Groups["account"].Value;
			}

			public string GetProviderUrl(string account)
			{
				return ProviderUrlTemplate.Replace("{account}", account);
			}
		}

		// TODO: Move to ScriptableObjet
		private static readonly List<AuthenticatedFeed> knownAuthenticatedFeeds = new List<AuthenticatedFeed>()
		{
			new AuthenticatedFeed()
			{
				AccountUrlPattern = @"^https:\/\/(?<account>[a-zA-z0-9]+).pkgs.visualstudio.com",
				ProviderUrlTemplate = "https://{account}.pkgs.visualstudio.com/_apis/public/nuget/client/CredentialProviderBundle.zip"
			},
			new AuthenticatedFeed()
			{
				AccountUrlPattern = @"^https:\/\/pkgs.dev.azure.com\/(?<account>[a-zA-z0-9]+)\/",
				ProviderUrlTemplate = "https://pkgs.dev.azure.com/{account}/_apis/public/nuget/client/CredentialProviderBundle.zip"
			}
		};

		/// <summary>
		/// Get the specified URL from the web. Throws exceptions if the request fails.
		/// </summary>
		/// <param name="url">URL that will be loaded.</param>
		/// <param name="password">Password that will be passed in the Authorization header or the request. If null, authorization is omitted.</param>
		/// <param name="timeOut">Timeout in milliseconds or null to use the default timeout values of HttpWebRequest.</param>
		/// <returns>Stream containing the result.</returns>
		public static Stream RequestUrl(string url, string userName, string password, int? timeOut)
		{
			var getRequest = (HttpWebRequest)WebRequest.Create(url);
			if (timeOut.HasValue)
			{
				getRequest.Timeout = timeOut.Value;
				getRequest.ReadWriteTimeout = timeOut.Value;
			}

			if (string.IsNullOrEmpty(password))
			{
				var creds = GetCredentialFromProvider(getRequest.RequestUri, true);
				if (creds.HasValue)
				{
					userName = creds.Value.Username;
					password = creds.Value.Password;
				}
			}

			if (password != null)
			{
				// Send password as described by https://docs.microsoft.com/en-us/vsts/integrate/get-started/rest/basics.
				// This works with Visual Studio Team Services, but hasn't been tested with other authentication schemes so there may be additional work needed if there
				// are different kinds of authentication.
				getRequest.Headers.Add("Authorization", "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes($"{userName}:{password}")));
			}

			LogVerbose("HTTP GET {0}", url);
			var objStream = getRequest.GetResponse().GetResponseStream();
			return objStream;
		}

		/// <summary>
		/// Restores all packages defined in packages.config.
		/// </summary>
		public static void Restore()
		{
			UpdateInstalledPackages();

			var stopwatch = new Stopwatch();
			stopwatch.Start();

			try
			{
				var progressStep = 1.0f / PackagesConfigFile.Packages.Count;
				float currentProgress = 0;

				// copy the list since the InstallIdentifier operation below changes the actual installed packages list
				var packagesToInstall = new List<NugetPackageIdentifier>(PackagesConfigFile.Packages);

				LogVerbose("Restoring {0} packages.", packagesToInstall.Count);

				foreach (var package in packagesToInstall)
				{
					if (package != null)
					{
						EditorUtility.DisplayProgressBar("Restoring NuGet Packages", $"Restoring {package.Id} {package.Version}", currentProgress);

						if (!IsInstalled(package))
						{
							LogVerbose("---Restoring {0} {1}", package.Id, package.Version);
							InstallIdentifier(package);
						}
						else
						{
							LogVerbose("---Already installed: {0} {1}", package.Id, package.Version);
						}
					}

					currentProgress += progressStep;
				}

				CheckForUnnecessaryPackages();
			}
			catch (Exception e)
			{
				Debug.LogErrorFormat("{0}", e.ToString());
			}
			finally
			{
				stopwatch.Stop();
				LogVerbose("Restoring packages took {0} ms", stopwatch.ElapsedMilliseconds);

				AssetDatabase.Refresh();
				EditorUtility.ClearProgressBar();
			}
		}

		internal static void CheckForUnnecessaryPackages()
		{
			if (!Directory.Exists(NugetConfigFile.RepositoryPath))
				return;

			var directories = Directory.GetDirectories(NugetConfigFile.RepositoryPath, "*", SearchOption.TopDirectoryOnly);
			foreach (var folder in directories)
			{
				var name = Path.GetFileName(folder);
				var installed = false;
				foreach (var package in PackagesConfigFile.Packages)
				{
					var packageName = $"{package.Id}.{package.Version}";
					if (name == packageName)
					{
						installed = true;
						break;
					}
				}

				if (!installed)
				{
					LogVerbose("---DELETE unnecessary package {0}", name);

					DeleteDirectory(folder);
					DeleteFile(folder + ".meta");
				}
			}

		}

		/// <summary>
		/// Checks if a given package is installed.
		/// </summary>
		/// <param name="package">The package to check if is installed.</param>
		/// <returns>True if the given package is installed.  False if it is not.</returns>
		internal static bool IsInstalled(NugetPackageIdentifier package)
		{
			var isInstalled = false;

			if (installedPackages.TryGetValue(package.Id, out var installedPackage))
			{
				isInstalled = package.Version == installedPackage.Version;
			}

			return isInstalled;
		}

		public static void DownloadAndSetIcon(NugetPackage package, string url)
		{
			StartCoroutine(DownloadAndSetIconRoutine(package, url));
		}

		private static readonly List<IEnumerator> activeEnumerators = new List<IEnumerator>();
		private static readonly List<IEnumerator> toRemove = new List<IEnumerator>();

		private static void StartCoroutine(IEnumerator enumerator)
		{
			if (!enumerator.MoveNext()) return;

			if (activeEnumerators.Count == 0)
			{
				EditorApplication.update -= RunCoroutines;
				EditorApplication.update += RunCoroutines;
			}

			activeEnumerators.Add(enumerator);
		}

		private static void RunCoroutines()
		{
			foreach (var enumerator in activeEnumerators)
			{
				if (!enumerator.MoveNext()) toRemove.Add(enumerator);
			}

			foreach (var enumerator in toRemove)
			{
				activeEnumerators.Remove(enumerator);
			}

			toRemove.Clear();

			if (activeEnumerators.Count == 0)
			{
				EditorApplication.update -= RunCoroutines;
				LogVerbose("All Nuget coroutines done");
			}
		}

		/// <summary>
		/// Downloads an image at the given URL and converts it to a Unity Texture2D.
		/// </summary>
		/// <param name="package">The package to set the icon on</param>
		/// <param name="url">The URL of the image to download.</param>
		private static IEnumerator DownloadAndSetIconRoutine(NugetPackage package, string url)
		{
			var stopwatch = new Stopwatch();
			stopwatch.Start();

			var fromCache = false;
			if (ExistsInDiskCache(url))
			{
				url = "file:///" + GetFilePath(url);
				fromCache = true;
			}

			Texture2D result = null;

			using (var request = UnityWebRequestTexture.GetTexture(url, false))
			{
				const int timeout = 5;
				request.timeout = timeout;
				// Since we are handling coroutines by ourselves we can't yield return this directly
				request.SendWebRequest();
				while (!request.isDone && stopwatch.ElapsedMilliseconds < timeout * 1000) yield return null;

				if (request.isDone && !request.isNetworkError && !request.isHttpError)
				{
					result = DownloadHandlerTexture.GetContent(request);
					LogVerbose("Downloading image {0} took {1} ms", url, stopwatch.ElapsedMilliseconds);
				}
				else if (!string.IsNullOrEmpty(request.error))
				{
					LogVerbose("Request {0} error after {1} ms: {2}", url, stopwatch.ElapsedMilliseconds, request.error);
				}
				else LogVerbose("Request {0} timed out after {1} ms", url, stopwatch.ElapsedMilliseconds);


				if (result != null && !fromCache)
				{
					CacheTextureOnDisk(url, request.downloadHandler.data);
				}
			}

			package.Icon = result;
		}

		private static void CacheTextureOnDisk(string url, byte[] bytes)
		{
			var diskPath = GetFilePath(url);
			File.WriteAllBytes(diskPath, bytes);
		}

		private static bool ExistsInDiskCache(string url)
		{
			return File.Exists(GetFilePath(url));
		}

		private static string GetFilePath(string url)
		{
			return Path.Combine(Application.temporaryCachePath, GetHash(url));
		}

		private static string GetHash(string s)
		{
			if (string.IsNullOrEmpty(s))
				return null;
			var md5 = new MD5CryptoServiceProvider();
			var data = md5.ComputeHash(Encoding.Default.GetBytes(s));
			var sBuilder = new StringBuilder();
			for (var i = 0; i < data.Length; i++)
			{
				sBuilder.Append(data[i].ToString("x2"));
			}

			return sBuilder.ToString();
		}

		/// <summary>
		/// Data class returned from nuget credential providers in a JSON format. As described here:
		/// https://docs.microsoft.com/en-us/nuget/reference/extensibility/nuget-exe-credential-providers#creating-a-nugetexe-credential-provider
		/// </summary>
		[Serializable]
		private struct CredentialProviderResponse
		{
#pragma warning disable 0649
			public string Username;
			public string Password;
#pragma warning disable 0649
		}

		/// <summary>
		/// Possible response codes returned by a Nuget credential provider as described here:
		/// https://docs.microsoft.com/en-us/nuget/reference/extensibility/nuget-exe-credential-providers#creating-a-nugetexe-credential-provider
		/// </summary>
		private enum CredentialProviderExitCode
		{
			Success = 0,
			ProviderNotApplicable = 1,
			Failure = 2
		}

		private static void DownloadCredentialProviders(Uri feedUri)
		{
			foreach (var feed in knownAuthenticatedFeeds)
			{
				var account = feed.GetAccount(feedUri.ToString());
				if (string.IsNullOrEmpty(account)) { continue; }

				var providerUrl = feed.GetProviderUrl(account);

				var credentialProviderRequest = (HttpWebRequest)WebRequest.Create(providerUrl);

				try
				{
					var credentialProviderDownloadStream = credentialProviderRequest.GetResponse().GetResponseStream();

					var tempFileName = Path.GetTempFileName();
					LogVerbose("Writing {0} to {1}", providerUrl, tempFileName);

					using (var file = File.Create(tempFileName))
					{
						CopyStream(credentialProviderDownloadStream, file);
					}

					var providerDestination = Environment.GetEnvironmentVariable("NUGET_CREDENTIALPROVIDERS_PATH");
					if (string.IsNullOrEmpty(providerDestination))
					{
						providerDestination = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nuget/CredentialProviders");
					}

					// Unzip the bundle and extract any credential provider exes
					using (var zip = ZipFile.OpenRead(tempFileName))
					{
						foreach (var entry in zip.Entries)
						{
							if (Regex.IsMatch(entry.FullName, @"^credentialprovider.+\.exe$", RegexOptions.IgnoreCase))
							{
								LogVerbose("Extracting {0} to {1}", entry.FullName, providerDestination);
								entry.ExtractToFile(Path.Combine(providerDestination, entry.FullName), true);
							}
						}
					}

					// Delete the bundle
					File.Delete(tempFileName);
				}
				catch (Exception e)
				{
					Debug.LogErrorFormat("Failed to download credential provider from {0}: {1}", credentialProviderRequest.Address, e.Message);
				}
			}

		}

		/// <summary>
		/// Helper function to aquire a token to access VSTS hosted nuget feeds by using the CredentialProvider.VSS.exe
		/// tool. Downloading it from the VSTS instance if needed.
		/// See here for more info on nuget Credential Providers:
		/// https://docs.microsoft.com/en-us/nuget/reference/extensibility/nuget-exe-credential-providers
		/// </summary>
		/// <returns>The password in the form of a token, or null if the password could not be aquired</returns>
		private static CredentialProviderResponse? GetCredentialFromProvider(Uri feedUri, bool downloadIfMissing)
		{
			while (true)
			{
				// Build the list of possible locations to find the credential provider. In order it should be local app data, paths set on the
				// environment varaible, and lastly look at the root of the pacakges save location.
				var possibleCredentialProviderPaths = new List<string>
				{
					Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nuget"), "CredentialProviders")
				};

				var environmentCredentialProviderPaths = Environment.GetEnvironmentVariable("NUGET_CREDENTIALPROVIDERS_PATH");
				if (!string.IsNullOrEmpty(environmentCredentialProviderPaths))
				{
					possibleCredentialProviderPaths.AddRange(environmentCredentialProviderPaths.Split(new[] {";"}, StringSplitOptions.RemoveEmptyEntries));
				}

				// Try to find any nuget.exe in the package tools installation location
				var toolsPackagesFolder = Path.Combine(Application.dataPath, "../Packages");
				possibleCredentialProviderPaths.Add(toolsPackagesFolder);

				// Search through all possible paths to find the credential provider.
				var providerPaths = new List<string>();
				foreach (var possiblePath in possibleCredentialProviderPaths)
				{
					if (Directory.Exists(possiblePath))
					{
						providerPaths.AddRange(Directory.GetFiles(possiblePath, "credentialprovider*.exe", SearchOption.AllDirectories));
					}
				}

				foreach (var providerPath in providerPaths.Distinct())
				{
					// Launch the credential provider executable and get the json encoded response from the std output
					var process = new Process
					{
						StartInfo =
						{
							UseShellExecute = false,
							CreateNoWindow = true,
							RedirectStandardOutput = true,
							RedirectStandardError = true,
							FileName = providerPath,
							Arguments = $"-uri \"{feedUri}\"",
							StandardOutputEncoding = Encoding.GetEncoding(850)
						}
					};

					// http://stackoverflow.com/questions/16803748/how-to-decode-cmd-output-correctly
					// Default = 65533, ASCII = ?, Unicode = nothing works at all, UTF-8 = 65533, UTF-7 = 242 = WORKS!, UTF-32 = nothing works at all
					process.Start();
					process.WaitForExit();

					var output = process.StandardOutput.ReadToEnd();
					var errors = process.StandardError.ReadToEnd();

					switch ((CredentialProviderExitCode)process.ExitCode)
					{
						case CredentialProviderExitCode.ProviderNotApplicable:
							break; // Not the right provider
						case CredentialProviderExitCode.Failure: // Right provider, failure to get creds
						{
							Debug.LogErrorFormat("Failed to get credentials from {0}!\n\tOutput\n\t{1}\n\tErrors\n\t{2}", providerPath, output, errors);
							return null;
						}
						case CredentialProviderExitCode.Success:
						{
							return JsonUtility.FromJson<CredentialProviderResponse>(output);
						}
						default:
						{
							Debug.LogWarningFormat("Unrecognized exit code {0} from {1} {2}", process.ExitCode, providerPath, process.StartInfo.Arguments);
							break;
						}
					}
				}

				if (downloadIfMissing)
				{
					DownloadCredentialProviders(feedUri);
					downloadIfMissing = false;
					continue;
				}

				return null;
			}
		}
	}
}
