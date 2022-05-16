using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Nordeus.Nuget.Utility;

namespace NugetForUnity
{
	/// <summary>
	/// A set of helper methods that act as a wrapper around nuget.exe
	/// 
	/// TIP: It's incredibly useful to associate .nupkg files as compressed folder in Windows (View like .zip files). To do this:
	///      1) Open a command prompt as admin (Press Windows key. Type "cmd". Right click on the icon and choose "Run as Administrator"
	///      2) Enter this command: cmd /c assoc .nupkg=CompressedFolder
	/// </summary>
	public static class NugetHelper
	{
		/// <summary>
		/// The path to the nuget.config file.
		/// </summary>
		public static readonly string NugetConfigFilePath = Path.Combine(SystemProxy.CurrentDir, "./NuGet.config");

		/// <summary>
		/// The path to the packages.config file.
		/// </summary>
		private static readonly string PackagesConfigFilePath = Path.Combine(SystemProxy.CurrentDir, "./packages.config");

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

		public static void ReloadPackagesConfig()
		{
			packagesConfigFile = null;
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
		/// The dictionary of cached credentials retrieved by credential providers, keyed by feed URI.
		/// </summary>
		private static readonly Dictionary<Uri, CredentialProviderResponse?> cachedCredentialsByFeedUri = new Dictionary<Uri, CredentialProviderResponse?>();

		/// <summary>
		/// The current .NET version being used (2.0 [actually 3.5], 4.6, etc).
		/// </summary>
		private static int DotNetVersion => SystemProxy.GetApiCompatibilityLevel();

		private static NugetConfigFile nugetConfigFile;

		/// <summary>
		/// Static constructor used by Unity to initialize NuGet and restore packages defined in packages.config.
		/// </summary>
		static NugetHelper()
		{
#if UNITY_EDITOR
			if (UnityEditor.SessionState.GetBool("NugetForUnity.FirstProjectOpen", false))
			{
				return;
			}

			UnityEditor.SessionState.SetBool("NugetForUnity.FirstProjectOpen", true);
#endif
                
			// if we are entering playmode, don't do anything
			if (SystemProxy.IsPlayingOrWillChangePlaymode)
			{
				return;
			}

			// Load the NuGet.config file
			nugetConfigFile = LoadNugetConfigFile();

			// create the nupkgs directory, if it doesn't exist
			if (!Directory.Exists(PackOutputDirectory))
			{
				Directory.CreateDirectory(PackOutputDirectory);
			}
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
				SystemProxy.Log($"No NuGet.config file found. Creating default at {NugetConfigFilePath}");

				result = NugetConfigFile.CreateDefaultFile(NugetConfigFilePath);
				SystemProxy.RefreshAssets();
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
					if (arg.StartsWith("-", StringComparison.Ordinal))
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
		/// <param name="logOuput">True to output debug information to the Unity console. Defaults to true.</param>
		/// <returns>The string of text that was output from nuget.exe following its execution.</returns>
		private static void RunNugetProcess(string arguments, bool logOuput = true)
		{
			// Try to find any nuget.exe in the package tools installation location
			var toolsPackagesFolder = Path.Combine(SystemProxy.CurrentDir, "../Packages");

			// create the folder to prevent an exception when getting the files
			Directory.CreateDirectory(toolsPackagesFolder);

			var files = Directory.GetFiles(toolsPackagesFolder, "nuget.exe", SearchOption.AllDirectories);
			if (files.Length > 1)
			{
				SystemProxy.LogWarning("More than one nuget.exe found. Using first one.");
			}
			else if (files.Length < 1)
			{
				SystemProxy.LogWarning("No nuget.exe found! Attemping to install the NuGet.CommandLine package.");
				InstallIdentifier(new NugetPackageIdentifier("NuGet.CommandLine", "2.8.6"));
				files = Directory.GetFiles(toolsPackagesFolder, "nuget.exe", SearchOption.AllDirectories);
				if (files.Length < 1)
				{
					SystemProxy.LogError("nuget.exe still not found. Quiting...");
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

			// ReSharper disable once PossibleNullReferenceException
			if (!process.WaitForExit(TimeOut))
			{
				SystemProxy.LogWarning("NuGet took too long to finish. Killing operation.");
				process.Kill();
			}

			var error = process.StandardError.ReadToEnd();
			if (!string.IsNullOrEmpty(error))
			{
				SystemProxy.LogError(error);
			}

			var output = process.StandardOutput.ReadToEnd();
			if (logOuput && !string.IsNullOrEmpty(output))
			{
				SystemProxy.Log(output);
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
		private static bool FrameworkNamesAreEqual(string tfm1, string tfm2)
		{
			return tfm1.Equals(tfm2, StringComparison.InvariantCultureIgnoreCase);
		}

		/// Cleans up a package after it has been installed.
		/// Since we are in Unity, we can make certain assumptions on which files will NOT be used, so we can delete them.
		/// </summary>
		/// <param name="package">The NugetPackage to clean.</param>
		private static void Clean(NugetPackageIdentifier package)
		{
			var packageInstallDirectory = Path.Combine(NugetConfigFile.RepositoryPath, $"{package.Id}.{package.Version}");

			LogVerbose("Cleaning {0}", packageInstallDirectory);

			FixSpaces(packageInstallDirectory);

			// Basic support for runtime folders. Might not work with all packages.
			// It doesn't unpack any x86 subfolders and removes x64 suffix from native libs.
			var runtimesDir = Path.Combine(packageInstallDirectory, "runtimes");
			if (Directory.Exists(runtimesDir))
			{
				foreach (var file in Directory.EnumerateFiles(runtimesDir, "*.x??.*", SearchOption.AllDirectories))
				{
					if (file.Contains(".x64.")) File.Move(file, file.Replace(".x64.", "."));
				}
			}

			if (Directory.Exists(packageInstallDirectory + "/lib"))
			{
				var selectedDirectories = new List<string>();

				// go through the library folders in descending order (highest to lowest version)
				var libDirectories = Directory.GetDirectories(packageInstallDirectory + "/lib").Select(s => new DirectoryInfo(s)).ToList();

				if (libDirectories.Count == 1)
				{
					LogVerbose("Using the only dir {0}", libDirectories[0].FullName);
					// If there is only one folder we will leave it no matter what it is
					selectedDirectories.Add(libDirectories[0].FullName);
				}
				else
				{
					var targetFrameworks = libDirectories.Select(x => x.Name.ToLower());
					bool isAlreadyImported = IsAlreadyImportedInEngine(package);
					var bestTargetFramework = TryGetBestTargetFrameworkForCurrentSettings(targetFrameworks);
					if (!isAlreadyImported && (bestTargetFramework != null))
					{
						DirectoryInfo bestLibDirectory = libDirectories
							.First(x => FrameworkNamesAreEqual(x.Name, bestTargetFramework));

						if (bestTargetFramework == "unity" ||
							bestTargetFramework == "net35-unity full v3.5" ||
							bestTargetFramework == "net35-unity subset v3.5")
						{
							selectedDirectories.Add(Path.Combine(bestLibDirectory.Parent.FullName, "unity"));
							selectedDirectories.Add(Path.Combine(bestLibDirectory.Parent.FullName, "net35-unity full v3.5"));
							selectedDirectories.Add(Path.Combine(bestLibDirectory.Parent.FullName, "net35-unity subset v3.5"));
						}
						else
						{
							selectedDirectories.Add(bestLibDirectory.FullName);
						}
					}
				}

				foreach (var dir in selectedDirectories)
				{
					LogVerbose("Using {0}", dir);
				}

				// delete all of the libraries except for the selected one
				foreach (var directory in libDirectories)
				{
					var validDirectory = selectedDirectories
										  .Any(d => string.Compare(d, directory.FullName, StringComparison.CurrentCultureIgnoreCase) == 0);
					if (!validDirectory)
					{
						LogVerbose("Deleting lib dir {0}", directory.FullName);
						DeleteDirectory(directory.FullName);
					}
				}
			}

			if (Directory.Exists(packageInstallDirectory + "/tools"))
			{
				// Move the tools folder outside of the Unity Assets folder
				var toolsInstallDirectory = Path.Combine(SystemProxy.CurrentDir, $"../Packages/{package.Id}.{package.Version}/tools");

				LogVerbose("Moving {0} to {1}", packageInstallDirectory + "/tools", toolsInstallDirectory);

				// create the directory to create any of the missing folders in the path
				Directory.CreateDirectory(toolsInstallDirectory);

				// delete the final directory to prevent the Move operation from throwing exceptions.
				DeleteDirectory(toolsInstallDirectory);

				Directory.Move(packageInstallDirectory + "/tools", toolsInstallDirectory);
			}

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
				var pluginsDirectory = SystemProxy.CurrentDir + "/Plugins/";

				DirectoryCopy(packageInstallDirectory + "/unityplugin", pluginsDirectory);

				LogVerbose("Deleting {0}", packageInstallDirectory + "/unityplugin");

				DeleteDirectory(packageInstallDirectory + "/unityplugin");
			}

			// if there are Unity StreamingAssets, copy them to the Unity StreamingAssets folder (Assets/StreamingAssets)
			if (Directory.Exists(packageInstallDirectory + "/StreamingAssets"))
			{
				var streamingAssetsDirectory = SystemProxy.CurrentDir + "/StreamingAssets/";

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
						SystemProxy.LogWarning($"{newFilePath} couldn't be moved. \n{e}");
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
						SystemProxy.LogWarning($"{newDirectoryPath} couldn't be moved. \n{e}");
					}
				}

				// delete the package's StreamingAssets folder and .meta file
				LogVerbose("Deleting {0}", packageInstallDirectory + "/StreamingAssets");
				DeleteDirectory(packageInstallDirectory + "/StreamingAssets");
				DeleteFile(packageInstallDirectory + "/StreamingAssets.meta");
			}
		}
		
		private static bool IsAlreadyImportedInEngine(NugetPackageIdentifier package)
		{
			HashSet<string> alreadyImportedLibs = GetAlreadyImportedLibs();
			bool isAlreadyImported = alreadyImportedLibs.Contains(package.Id);
			LogVerbose("Is package '{0}' already imported? {1}", package.Id, isAlreadyImported);
			return isAlreadyImported;
		}

		private static HashSet<string> alreadyImportedLibs = null;
		private static HashSet<string> GetAlreadyImportedLibs()
		{
			if (alreadyImportedLibs != null) return alreadyImportedLibs;
			
			var cachePath = Path.Combine(SystemProxy.CurrentDir, $"../Library/AllLibPaths{SystemProxy.UnityVersion}.txt");
			if (File.Exists(cachePath))
			{
				alreadyImportedLibs = new HashSet<string>(File.ReadAllLines(cachePath));
			}
			else
			{
				string[] lookupPaths = GetAllLookupPaths();
				IEnumerable<string> libNames = lookupPaths
					.SelectMany(directory => Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories))
					.Select(Path.GetFileName)
					.Select(p => Path.ChangeExtension(p, null));
				alreadyImportedLibs = new HashSet<string>(libNames);
				if (!Directory.Exists("../Library/")) Directory.CreateDirectory("../Library/");
				File.WriteAllLines(cachePath, alreadyImportedLibs);
				LogVerbose("Already imported libs: {0}", string.Join(", ", alreadyImportedLibs));
			}

			return alreadyImportedLibs;
		}

		private static string[] GetAllLookupPaths()
		{
			var executablePath = SystemProxy.AppDir;
			var roots = new[] {
				// MacOS directory layout
				Path.Combine(executablePath, "Contents"),
				Path.GetDirectoryName(Path.GetDirectoryName(executablePath)),
				// Windows directory layout
				Path.Combine(Directory.GetParent(executablePath).FullName, "Data")
			};
			var relativePaths = new[] {
				Path.Combine("NetStandard",	 "compat"),
				Path.Combine("MonoBleedingEdge", "lib", "mono")
			};
			var allPossiblePaths = roots
				.SelectMany(root => relativePaths
					.Select(relativePath => Path.Combine(root, relativePath)));
			var existingPaths = allPossiblePaths
				.Where(Directory.Exists)
				.ToArray();
			if (existingPaths.Length == 0)
			{
				LogVerbose("No existing path for dependency lookup found in roots: {0}", string.Join(", ", roots));
			}
			else
			{
				LogVerbose("All existing path to dependency lookup are: {0}", string.Join(", ", existingPaths));
			}
			return existingPaths;
		}

		public static NugetFrameworkGroup GetBestDependencyFrameworkGroupForCurrentSettings(NugetPackage package)
		{
			var targetFrameworks = package.Dependencies.Select(x => x.TargetFramework);
			string bestTargetFramework = TryGetBestTargetFrameworkForCurrentSettings(targetFrameworks);
			
			return package.Dependencies
				.FirstOrDefault(x => FrameworkNamesAreEqual(x.TargetFramework, bestTargetFramework)) ?? new NugetFrameworkGroup();
		}

		public static NugetFrameworkGroup GetBestDependencyFrameworkGroupForCurrentSettings(NuspecFile nuspec)
		{
			var targetFrameworks = nuspec.Dependencies.Select(x => x.TargetFramework);

			string bestTargetFramework = TryGetBestTargetFrameworkForCurrentSettings(targetFrameworks);
			return nuspec.Dependencies
				.FirstOrDefault(x => FrameworkNamesAreEqual(x.TargetFramework, bestTargetFramework)) ?? new NugetFrameworkGroup();
		}
		
		private readonly struct UnityVersion : IComparable<UnityVersion>
		{
			public readonly int Major;
			private readonly int Minor;
			private readonly int Revision;
			private readonly char Release;
			private readonly int Build;

			public static readonly UnityVersion Current = new UnityVersion(SystemProxy.UnityVersion);

			private UnityVersion(string version)
			{
				var match = Regex.Match(version, @"(\d+)\.(\d+)\.(\d+)([fpba])(\d+)");
				if (!match.Success) { throw new ArgumentException("Invalid unity version"); }

				Major = int.Parse(match.Groups[1].Value);
				Minor = int.Parse(match.Groups[2].Value);
				Revision = int.Parse(match.Groups[3].Value);
				Release = match.Groups[4].Value[0];
				Build = int.Parse(match.Groups[5].Value);
			}

			private static int Compare(in UnityVersion a, in UnityVersion b)
			{

				if (a.Major < b.Major) { return -1; }
				if (a.Major > b.Major) { return 1; }

				if (a.Minor < b.Minor) { return -1; }
				if (a.Minor > b.Minor) { return 1; }

				if (a.Revision < b.Revision) { return -1; }
				if (a.Revision > b.Revision) { return 1; }

				if (a.Release < b.Release) { return -1; }
				if (a.Release > b.Release) { return 1; }

				if (a.Build < b.Build) { return -1; }
				if (a.Build > b.Build) { return 1; }

				return 0;
			}

			public int CompareTo(UnityVersion other)
			{
				return Compare(this, other);
			}
		}

		private struct PriorityFramework { public int Priority; public string Framework; }
		private static readonly string[] unityFrameworks = {"unity"};
		private static readonly string[] netStandardFrameworks = {
			"netstandard20", "netstandard16", "netstandard15", "netstandard14", "netstandard13", "netstandard12", "netstandard11", "netstandard10" };
		
		private static readonly string[] net4Unity2018Frameworks = {"net471", "net47"};
		private static readonly string[] net4Unity2017Frameworks = {"net462", "net461", "net46", "net452", "net451", "net45", "net403", "net40", "net4"};
		private static readonly string[] net3Frameworks = {"net35-unity full v3.5", "net35-unity subset v3.5", "net35", "net20", "net11"};
		private static readonly string[] defaultFrameworks = {string.Empty};

		public static string TryGetBestTargetFrameworkForCurrentSettings(IEnumerable<string> targetFrameworks)
		{
			var intDotNetVersion = DotNetVersion; // c
			//bool using46 = DotNetVersion == ApiCompatibilityLevel.NET_4_6; // NET_4_6 option was added in Unity 5.6
			var using46 = intDotNetVersion == 3; // NET_4_6 = 3 in Unity 5.6 and Unity 2017.1 - use the hard-coded int value to ensure it works in earlier versions of Unity
			var usingStandard2 = intDotNetVersion == 6; // using .net standard 2.0

			var frameworkGroups = new List<string[]> { unityFrameworks };

			if (usingStandard2)
			{
				frameworkGroups.Add(netStandardFrameworks);
			}
			else if (using46)
			{
				if(UnityVersion.Current.Major >= 2018)
				{
					frameworkGroups.Add(net4Unity2018Frameworks);
				}

				if (UnityVersion.Current.Major >= 2017)
				{
					frameworkGroups.Add(net4Unity2017Frameworks);
				}

				frameworkGroups.Add(net3Frameworks);
				frameworkGroups.Add(netStandardFrameworks);
			}
			else
			{
				frameworkGroups.Add(net3Frameworks);
			}

			frameworkGroups.Add(defaultFrameworks);

			int GetTfmPriority(string tfm)
			{
				for (var i = 0; i < frameworkGroups.Count; ++i)
				{
					int index = Array.FindIndex(frameworkGroups[i], test =>
					{
						if (test.Equals(tfm, StringComparison.InvariantCultureIgnoreCase)) { return true; }
						if (test.Equals(tfm.Replace(".", string.Empty), StringComparison.InvariantCultureIgnoreCase)) { return true; }
						return false;
					});

					if (index >= 0)
					{
						return i * 1000 + index;
					}
				}

				return int.MaxValue;
			}

			// Select the highest .NET library available that is supported
			// See here: https://docs.nuget.org/ndocs/schema/target-frameworks
			var result = targetFrameworks
						 .Select(tfm => new PriorityFramework {Priority = GetTfmPriority(tfm), Framework = tfm})
						 .Where(pfm => pfm.Priority != int.MaxValue)
						 .ToArray() // Ensure we don't search for priorities again when sorting
						 .OrderBy(pfm => pfm.Priority)
						 .Select(pfm => pfm.Framework)
						 .FirstOrDefault();

			return result;
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
		/// <param name="nuspec">The NuspecFile which defines the package to push. Only the ID and Version are used.</param>
		/// <param name="nuspecFilePath">The full filepath to the .nuspec file to use. This is required by NuGet's Push command.</param>
		/// /// <param name="apiKey">The API key to use when pushing a package to the server. This is optional.</param>
		public static void Push(NuspecFile nuspec, string nuspecFilePath, string apiKey = "")
		{
			var packagePath = Path.Combine(PackOutputDirectory, $"{nuspec.Id}.{nuspec.Version}.nupkg");
			if (!File.Exists(packagePath))
			{
				LogVerbose("Attempting to Pack.");
				Pack(nuspecFilePath);

				if (!File.Exists(packagePath))
				{
					SystemProxy.LogError($"NuGet package not found: {packagePath}");
					return;
				}
			}

			var arguments = $"push \"{packagePath}\" {apiKey} -configfile \"{NugetConfigFilePath}\"";

			RunNugetProcess(arguments);
		}
		
		/// <summary>
		/// Recursively copies all files and sub-directories from one directory to another.
		/// </summary>
		/// <param name="sourceDirectoryPath">The filepath to the folder to copy from.</param>
		/// <param name="destDirectoryPath">The filepath to the folder to copy to.</param>
		private static void DirectoryCopy(string sourceDirectoryPath, string destDirectoryPath)
		{
			var dir = new DirectoryInfo(sourceDirectoryPath);

			if (!dir.Exists)
			{
				throw new DirectoryNotFoundException(
													 "Source directory does not exist or could not be found: "
													 + sourceDirectoryPath);
			}

			// if the destination directory doesn't exist, create it
			if (!Directory.Exists(destDirectoryPath))
			{
				LogVerbose("Creating new directory: {0}", destDirectoryPath);
				Directory.CreateDirectory(destDirectoryPath);
			}

			// get the files in the directory and copy them to the new location
			var files = dir.GetFiles();
			foreach (var file in files)
			{
				var newFilePath = Path.Combine(destDirectoryPath, file.Name);

				try
				{
					LogVerbose("Moving {0} to {1}", file.ToString(), newFilePath);
					file.CopyTo(newFilePath, true);
				}
				catch (Exception e)
				{
					SystemProxy.LogWarning($"{file} couldn't be moved to {newFilePath}. It may be a native plugin already locked by Unity. Please trying closing Unity and manually moving it. \n{e}");
				}
			}

			// copy sub-directories and their contents to new location
			var dirs = dir.GetDirectories();
			foreach (var subdir in dirs)
			{
				var temppath = Path.Combine(destDirectoryPath, subdir.Name);
				DirectoryCopy(subdir.FullName, temppath);
			}
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
		/// Deletes a file at the given filepath. Also deletes its meta file if found.
		/// </summary>
		/// <param name="filePath">The filepath to the file to delete.</param>
		private static void DeleteFile(string filePath)
		{
			if (!File.Exists(filePath)) return;
			File.SetAttributes(filePath, FileAttributes.Normal);
			File.Delete(filePath);
			var metaPath = filePath + ".meta";
			if (filePath.EndsWith(".meta") || !File.Exists(metaPath)) return;
			File.SetAttributes(metaPath, FileAttributes.Normal);
			File.Delete(metaPath);
		}
		
		private static string LoadInitClassFile(string name)
		{
			var stream = typeof(NugetHelper).Assembly.GetManifestResourceStream("CreateDLL..Templates." + name);
			if (stream == null && File.Exists("../../NuGetForUnity/CreateDLL/.Templates/" + name))
			{
				stream = File.OpenRead("../../NuGetForUnity/CreateDLL/.Templates/" + name);
			}
			if (stream == null)
			{
				SystemProxy.LogError("Failed to load embedded CreateDLL..Templates." + name);
				return null;
			}

			using (var streamReader = new StreamReader(stream, Encoding.UTF8))
			{
				return streamReader.ReadToEnd();
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

			// Reset Init files
			var initCsDir = Path.Combine(SystemProxy.CurrentDir, "Scripts/Initialization");
			var initCsPath = Path.Combine(initCsDir, "AppInitializer.cs");
			var initTxtPath = Path.Combine(initCsDir, "AppInitializerOriginal.txt");
			var generatedInitCsPath = Path.Combine(initCsDir, "AppInitializer.Generated.cs");

			var initCs = LoadInitClassFile("AppInitializer.cs");
			var generatedInitCs = LoadInitClassFile("AppInitializer.Generated.cs");
			var editorCs = LoadInitClassFile("EditorAppInitializer.cs");
			
			var editorInitPath = Path.Combine(SystemProxy.CurrentDir, "Editor/EditorAppInitializer.cs");

			if (initCs != null && generatedInitCs != null && editorCs != null)
			{
				// We guarantee Windows line breaks
				var newLinesRegex = new Regex(@"\r\n?|\n");

				initCs = newLinesRegex.Replace(initCs, "\r\n");
				generatedInitCs = newLinesRegex.Replace(generatedInitCs, "\r\n");
				editorCs = newLinesRegex.Replace(editorCs, "\r\n");

				// Make sure the dir exists
				Directory.CreateDirectory(initCsDir);
				Directory.CreateDirectory(Path.GetDirectoryName(editorInitPath));

				File.WriteAllText(initCsPath, initCs);
				DeleteFile(initTxtPath);
				File.WriteAllText(generatedInitCsPath, generatedInitCs);
				File.WriteAllText(editorInitPath, editorCs);

			}

			SystemProxy.RefreshAssets();
		}

		/// <summary>
		/// "Uninstalls" the given package by simply deleting its folder.
		/// </summary>
		/// <param name="package">The NugetPackage to uninstall.</param>
		/// <param name="refreshAssets">True to force Unity to refesh its Assets folder. False to temporarily ignore the change. Defaults to true.</param>
		public static void Uninstall(NugetPackageIdentifier package, bool refreshAssets = true, bool deleteDependencies = false)
		{
			LogVerbose("Uninstalling: {0} {1}", package.Id, package.Version);

			var foundPackage = package as NugetPackage ?? GetSpecificPackage(package);

			// check for symbolic link and remove it first
			var packagePath = Path.Combine(NugetConfigFile.RepositoryPath, $"{foundPackage.Id}.{foundPackage.Version}");
			var packageSourcePaths = packagePath;
			var packageEditorSources = Path.Combine(packagePath, "Editor");

			var sourcesExists = SymbolicLink.Exists(packagePath);
			if (!sourcesExists)
			{
				packageSourcePaths = Path.Combine(packagePath, "lib");
				sourcesExists = SymbolicLink.Exists(packageSourcePaths);
			}
			var sourcesEditorExists = SymbolicLink.Exists(packageEditorSources);
			var isLinked = sourcesExists || sourcesEditorExists;

			if (isLinked)
			{
				LogVerbose("Removing symbolic link for package {0} {1}", foundPackage.Id, foundPackage.Version);
				UnlinkSource(sourcesExists, packageSourcePaths, sourcesEditorExists, packageEditorSources, foundPackage, packagePath);
			}

			// update the package.config file
			PackagesConfigFile.RemovePackage(foundPackage);
			PackagesConfigFile.Save(PackagesConfigFilePath);

			var packageInstallDirectory = Path.Combine(NugetConfigFile.RepositoryPath, $"{foundPackage.Id}.{foundPackage.Version}");
			DeleteDirectory(packageInstallDirectory);

			var metaFile = Path.Combine(NugetConfigFile.RepositoryPath, $"{foundPackage.Id}.{foundPackage.Version}.meta");
			DeleteFile(metaFile);

			var toolsInstallDirectory = Path.Combine(SystemProxy.CurrentDir, $"../Packages/{foundPackage.Id}.{foundPackage.Version}");
			DeleteDirectory(toolsInstallDirectory);

			installedPackages.Remove(foundPackage.Id);

			if (deleteDependencies)
			{
				var frameworkGroup = GetBestDependencyFrameworkGroupForCurrentSettings(foundPackage);
				foreach (var dependency in frameworkGroup.Dependencies)
				{
					var actualData = PackagesConfigFile.Packages.Find(pkg => pkg.Id == dependency.Id);
					if (actualData == null || actualData.IsManuallyInstalled) continue;

					var hasMoreParents = false;
					foreach (var pkg in installedPackages.Values)
					{
						var defFrameworkGroup = GetBestDependencyFrameworkGroupForCurrentSettings(pkg);
						foreach (var dep in defFrameworkGroup.Dependencies)
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
				SystemProxy.RefreshAssets();
		}

		public static void UnlinkSource(bool sourcesExists, string packageSourcePaths, bool sourcesEditorExists, string packageEditorSources,
										NugetPackage package, string packagePath)
		{
			if (sourcesExists) SymbolicLink.Delete(packageSourcePaths);
			if (sourcesEditorExists) SymbolicLink.Delete(packageEditorSources);
			var path = Path.Combine(NugetConfigFile.RepositoryPath, $".{package.Id}.{package.Version}");
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

		/// <summary>
		/// Updates a package by uninstalling the currently installed version and installing the "new" version.
		/// </summary>
		/// <param name="currentVersion">The current package to uninstall.</param>
		/// <param name="newVersion">The package to install.</param>
		/// <param name="refreshAssets">True to refresh the assets inside Unity. False to ignore them (for now). Defaults to true.</param>
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
			var lastUpdatedId = "";

			foreach (var update in updates)
			{
				if (lastUpdatedId == update.Id) continue;
				lastUpdatedId = update.Id;
				SystemProxy.DisplayProgress($"Updating to {update.Id} {update.Version}", "Installing All Updates", currentProgress);

				var installedPackage = packagesToUpdate.FirstOrDefault(p => p.Id == update.Id);
				if (installedPackage != null)
				{
					Update(installedPackage, update, false);
				}
				else
				{
					SystemProxy.LogError($"Trying to update {update.Id} to {update.Version}, but no version is installed!");
				}

				currentProgress += progressStep;
			}

			SystemProxy.RefreshAssets();

			SystemProxy.ClearProgress();
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
						SystemProxy.LogError($"Package is already in installed list: {package.Id}");
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
												int numberToGet = 15, int numberToSkip = 0, int firstNumberToGet = 15)
		{
			var packages = new List<NugetPackage>();
			var packagesSet = new HashSet<NugetPackage>();

			// Loop through all active sources and combine them into a single list
			foreach (var source in packageSources.Where(s => s.IsEnabled))
			{
				var newPackages = source.Search(searchTerm, includeAllVersions, includePrerelease,
												source == packageSources.First() ? firstNumberToGet : numberToGet, numberToSkip);
				if (searchTerm == "") newPackages.Sort((p1, p2) => string.Compare(p1.Title, p2.Title, StringComparison.OrdinalIgnoreCase));
				foreach (var package in newPackages)
				{
					if (packagesSet.Add(package)) packages.Add(package);
				}
			}

			return packages;
		}

		/// <summary>
		/// Queries the server with the given list of installed packages to get any updates that are available.
		/// </summary>
		/// <param name="packagesToUpdate">The list of currently installed packages.</param>
		/// <param name="includePrerelease">True to include prerelease packages (alpha, beta, etc).</param>
		/// <param name="includeAllVersions">True to include older versions that are not the latest version.</param>
		/// <returns>A list of all updates available.</returns>
		public static List<NugetPackage> GetUpdates(ICollection<NugetPackage> packagesToUpdate, bool includePrerelease = false,
													bool includeAllVersions = true)
		{
			var packages = new List<NugetPackage>();
			var packagesSet = new HashSet<NugetPackage>();

			// Loop through all active sources and combine them into a single list
			foreach (var source in packageSources.Where(s => s.IsEnabled))
			{
				var newPackages = source.GetUpdates(packagesToUpdate, includePrerelease, includeAllVersions);
				foreach (var package in newPackages)
				{
					if (packagesSet.Add(package)) packages.Add(package);
				}
			}

			return packages;
		}

		private static NugetPackageIdentifier lastSpecificPackageId;
		private static NugetPackage lastSpecificPackage;

		/// <summary>
		/// Gets a NugetPackage from the NuGet server with the exact ID and Version given.
		/// </summary>
		/// <param name="packageId">The <see cref="NugetPackageIdentifier"/> containing the ID and Version of the package to get.</param>
		/// <returns>The retrieved package, if there is one. Null if no matching package was found.</returns>
		private static NugetPackage GetSpecificPackage(NugetPackageIdentifier packageId)
		{
			if (lastSpecificPackage != null && lastSpecificPackage != null && lastSpecificPackageId == packageId)
			{
				return lastSpecificPackage;
			}
			
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

			lastSpecificPackageId = packageId;
			lastSpecificPackage = package;

			return package;
		}

		/// <summary>
		/// Tries to find an already installed package that matches (or is in the range of) the given package ID.
		/// </summary>
		/// <param name="packageId">The <see cref="NugetPackageIdentifier"/> of the <see cref="NugetPackage"/> to find.</param>
		/// <returns>The best <see cref="NugetPackage"/> match, if there is one, otherwise null.</returns>
		private static NugetPackage GetInstalledPackage(NugetPackageIdentifier packageId)
		{
			if (!installedPackages.TryGetValue(packageId.Id, out var installedPackage)) return null;
			
			if (packageId.Version != installedPackage.Version)
			{
				if (packageId.InRange(installedPackage))
				{
					var configPackage = PackagesConfigFile.Packages.Find(p => p.Id == packageId.Id);
					if (configPackage != null && configPackage < installedPackage)
					{
						LogVerbose("Requested {0} {1}. {2} is already installed, but config demands lower version.", packageId.Id, packageId.Version, installedPackage.Version);
						installedPackage = null;
					}
					else
					{
						LogVerbose("Requested {0} {1}, but {2} is already installed, so using that.", packageId.Id, packageId.Version, installedPackage.Version);
					}
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

				if (packageId.InRange(foundPackage.Version))
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
		/// Installs the package given by the identifer. It fetches the appropriate full package from the installed packages, package cache, or package sources and installs it.
		/// </summary>
		/// <param name="package">The identifer of the package to install.</param>
		/// <param name="refreshAssets">True to refresh the Unity asset database. False to ignore the changes (temporarily).</param>
		internal static bool InstallIdentifier(NugetPackageIdentifier package, bool refreshAssets = true)
		{
			if (IsAlreadyImportedInEngine(package))
			{
				LogVerbose("Package {0} is already imported in engine, skipping install.", package);
				return true;
			}

			NugetPackage foundPackage = GetSpecificPackage(package);

			if (foundPackage != null)
			{
				if (package.IsManuallyInstalled) foundPackage.IsManuallyInstalled = true;
				return Install(foundPackage, refreshAssets);
			}
			else
			{
				SystemProxy.LogError($"Could not find {package.Id} {package.Version} or greater.");
				return false;
			}
		}

		/// <summary>
		/// Outputs the given message to the log only if verbose mode is active. Otherwise it does nothing.
		/// </summary>
		/// <param name="format">The formatted message string.</param>
		/// <param name="args">The arguments for the formattted message string.</param>
		public static void LogVerbose(string format, params object[] args)
		{
			if (NugetConfigFile.Verbose)
			{
				SystemProxy.Log(string.Format(format, args));
			}
		}

		/// <summary>
		/// Installs the given package.
		/// </summary>
		/// <param name="package">The package to install.</param>
		/// <param name="refreshAssets">True to refresh the Unity asset database. False to ignore the changes (temporarily).</param>
		public static bool Install(NugetPackage package, bool refreshAssets = true)
		{
			if (IsAlreadyImportedInEngine(package))
			{
				LogVerbose("Package {0} is already imported in engine, skipping install.", package);
				return true;
			}

			if (installedPackages.TryGetValue(package.Id, out var installedPackage))
			{
				if (installedPackage < package)
				{
					LogVerbose("{0} {1} is installed, but need {2} or greater. Updating to {3}", installedPackage.Id,
								installedPackage.Version, package.Version, package.Version);
					return Update(installedPackage, package, false);
				}
				if (installedPackage > package)
				{
					var configPackage = PackagesConfigFile.Packages.Find(identifier => identifier.Id == package.Id);
					if (configPackage != null && configPackage < installedPackage)
					{
						LogVerbose("{0} {1} is installed but config needs {2} so downgrading.", installedPackage.Id,
								   installedPackage.Version, package.Version);
						return Update(installedPackage, package, false);
					}
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


				SystemProxy.DisplayProgress($"Installing {package.Id} {package.Version}", "Installing Dependencies", 0.1f);

				// install all dependencies for target framework
				var frameworkGroup = GetBestDependencyFrameworkGroupForCurrentSettings(package);
				
				LogVerbose("Installing dependencies for TargetFramework: {0}", frameworkGroup.TargetFramework);
				foreach (var dependency in frameworkGroup.Dependencies)
				{
					LogVerbose("Installing Dependency: {0} {1}", dependency.Id, dependency.Version);

					var installed = InstallIdentifier(dependency, false);
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
						// Mono doesn't have a Certificate Authority, so we have to provide all validation manually. Currently just accept anything.
						// See here: http://stackoverflow.com/questions/4926676/mono-webrequest-fails-with-https

						// remove all handlers
						//if (ServicePointManager.ServerCertificateValidationCallback != null)
						//	foreach (var d in ServicePointManager.ServerCertificateValidationCallback.GetInvocationList())
						//		ServicePointManager.ServerCertificateValidationCallback -= (d as System.Net.Security.RemoteCertificateValidationCallback);
						ServicePointManager.ServerCertificateValidationCallback = null;

						// add anonymous handler
						ServicePointManager.ServerCertificateValidationCallback += (sender, certificate, chain, policyErrors) => true;

						LogVerbose("Downloading package {0} {1}", package.Id, package.Version);

						SystemProxy.DisplayProgress($"Installing {package.Id} {package.Version}", "Downloading Package", 0.3f);

						var objStream = RequestUrl(package.DownloadUrl, package.PackageSource.UserName, package.PackageSource.ExpandedPassword, timeOut: null);
						using (Stream file = File.Create(cachedPackagePath))
						{
							CopyStream(objStream, file);
						}
					}
				}

				SystemProxy.DisplayProgress($"Installing {package.Id} {package.Version}", "Extracting Package", 0.6f);

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
							var normalizedPath = destPath.Replace('\\', '/');
							
							// Skip platform specific libs that are 32bit
							if (normalizedPath.Contains("runtimes/") && normalizedPath.Contains("x86")) continue;
							
							// Skip directories & files that NuGet normally deletes plus nuspec file and src dir we don't need
							if (normalizedPath.EndsWith($"/{package.Id}.nuspec.meta", StringComparison.Ordinal)) continue;
							if (normalizedPath.EndsWith($"/{package.Id}.nuspec", StringComparison.Ordinal)) continue;
							if (normalizedPath.EndsWith("/_rels", StringComparison.Ordinal) || normalizedPath.Contains("/_rels/")) continue;
							if (normalizedPath.EndsWith("/package", StringComparison.Ordinal) || normalizedPath.Contains("/package/")) continue;
							if (normalizedPath.EndsWith("/build", StringComparison.Ordinal) || normalizedPath.Contains("/build/")) continue;
							if (normalizedPath.EndsWith("/src", StringComparison.Ordinal) || normalizedPath.Contains("/src/")) continue;
							if (normalizedPath.EndsWith("/[Content_Types].xml", StringComparison.Ordinal)) continue;
							
							// Skip documentation folders since they sometimes have HTML docs with JavaScript, which Unity tried to parse as "UnityScript"
							if (normalizedPath.EndsWith("/docs", StringComparison.Ordinal) || normalizedPath.Contains("/docs/")) continue;
							
							// Skip ref folder, as it is just used for compile-time reference and does not contain implementations.
							// Leaving it results in "assembly loading" and "multiple pre-compiled assemblies with same name" errors
							if (normalizedPath.EndsWith("/ref", StringComparison.Ordinal) || normalizedPath.Contains("/ref/")) continue;
							
							// Skip all PDB files since Unity uses Mono and requires MDB files, which causes it to output "missing MDB" errors
							if (normalizedPath.EndsWith(".pdb", StringComparison.Ordinal)) continue;

							var isDir = normalizedPath.EndsWith("/");
							var dirName = isDir ? destPath : Path.GetDirectoryName(destPath);
							if (dirName != null && !Directory.Exists(dirName)) Directory.CreateDirectory(dirName);
							if (isDir) continue;
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
					SystemProxy.LogError($"File not found: {cachedPackagePath}");
				}

				SystemProxy.DisplayProgress($"Installing {package.Id} {package.Version}", "Cleaning Package", 0.9f);

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
					SystemProxy.LogError("Failed processing init template " + e);
				}

				// clean
				Clean(package);

				// update the installed packages list
				installedPackages.Add(package.Id, package);
				installSuccess = true;
			}
			catch (Exception e)
			{
				SystemProxy.ShowAlert($"Unable to install package {package.Id} {package.Version}: {e.Message}");
				SystemProxy.LogError($"Unable to install package {package.Id} {package.Version}\n{e}");
				installSuccess = false;
			}
			finally
			{
				if (refreshAssets)
				{
					SystemProxy.DisplayProgress($"Installing {package.Id} {package.Version}", "Importing Package", 0.95f);
					SystemProxy.RefreshAssets();
				}
				SystemProxy.ClearProgress();
			}

			return installSuccess;
		}
		
		private static string PackageIdToMethodName(string pkgId)
		{
			pkgId = pkgId.Replace("nordeus.", "").Replace("unity.", "");
			pkgId = pkgId.Substring(0, 1).ToUpper() + pkgId.Substring(1);
			return "Init" + pkgId;
		}

		private static string LoadInitClassFile(string path, string name)
		{
			if (File.Exists(path)) return File.ReadAllText(path);

			var stream = typeof(NugetHelper).Assembly.GetManifestResourceStream("CreateDLL..Templates." + name);
			if (stream == null && File.Exists("../../NuGetForUnity/CreateDLL/.Templates/" + name))
			{
				stream = File.OpenRead("../../NuGetForUnity/CreateDLL/.Templates/" + name);
			}
			if (stream == null)
			{
				SystemProxy.LogError("Failed to load embedded CreateDLL..Templates." + name);
				return null;
			}

			using (var streamReader = new StreamReader(stream, Encoding.UTF8))
			{
				return streamReader.ReadToEnd();
			}
		}

		private static void ProcessInitTemplate(string initTemplatePath, NugetPackage package)
		{
			var initCsDir = Path.Combine(SystemProxy.CurrentDir, "Scripts/Initialization");
			var initCsPath = Path.Combine(initCsDir, "AppInitializer.cs");
			var initTxtPath = Path.Combine(initCsDir, "AppInitializerOriginal.txt");
			var generatedInitCsPath = Path.Combine(initCsDir, "AppInitializer.Generated.cs");
			var editorCsDir = Path.Combine(SystemProxy.CurrentDir, "Editor");
			var editorCsPath = Path.Combine(editorCsDir, "EditorAppInitializer.cs");

			var initCs = LoadInitClassFile(initCsPath, "AppInitializer.cs");
			var initTxt = File.Exists(initTxtPath) ? File.ReadAllText(initTxtPath) : "";
			var generatedInitCs = LoadInitClassFile(generatedInitCsPath, "AppInitializer.Generated.cs");
			var editorCs = LoadInitClassFile(editorCsPath, "EditorAppInitializer.cs");

			if (initCs == null || generatedInitCs == null || editorCs == null) return;

			// We guarantee Windows line breaks
			var newLinesRegex = new Regex(@"\r\n?|\n");

			initCs = newLinesRegex.Replace(initCs, "\r\n");
			initTxt = newLinesRegex.Replace(initTxt, "\r\n");
			generatedInitCs = newLinesRegex.Replace(generatedInitCs, "\r\n");
			editorCs = newLinesRegex.Replace(editorCs, "\r\n");

			var initTemplate = File.ReadAllLines(initTemplatePath);

			var initMethodName = PackageIdToMethodName(package.Id);
			// We will rewrite init code if it is the same as the original that came from previous installed version
			var methodStartLine = $"\r\n\t\tprivate static void {initMethodName}()";
			var initIndex = initCs.IndexOf(methodStartLine, StringComparison.Ordinal);
			var initCodeExisted = initIndex > 0;
			var initTxtIndex = -1;
			var writeCs = true;
			var originalInitCode = "";
			if (initCodeExisted)
			{
				var methodEndLine = "\r\n\t\t}\r\n";
				var methodEndIndex = initCs.IndexOf(methodEndLine, initIndex, StringComparison.Ordinal) + methodEndLine.Length;

				initTxtIndex = initTxt.IndexOf(methodStartLine, StringComparison.Ordinal);
				var initTxtCodeExisted = initTxtIndex >= 0;
				if (initTxtCodeExisted)
				{
					var methodTxtEndIndex = initTxt.IndexOf(methodEndLine, initTxtIndex, StringComparison.Ordinal) + methodEndLine.Length;
					var realInitCode = initCs.Substring(initIndex, methodEndIndex - initIndex);
					originalInitCode = initTxt.Substring(initTxtIndex, methodTxtEndIndex - initTxtIndex);
					if (realInitCode == originalInitCode)
					{
						initCs = initCs.Remove(initIndex, methodEndIndex - initIndex);
					}
					else
					{
						writeCs = false;
					}
					initTxt = initTxt.Remove(initTxtIndex, methodTxtEndIndex - initTxtIndex);
				}
				else
				{
					writeCs = false;
				}
			}

			// Init template file should be written like this:
			// InitDependencies: a, b, c <in case the init code depends on packages the package itself doesn't depend on>
			// Uses: use1, use2 <using clauses that need to exist at the top of the file>
			// CustomExceptionLogging: <code to insert into CustomExceptionLogging if package wants to handle init exceptions>
			// {...}
			// InitCode:|SceneInitCode: <this is the only required section>
			// {...}
			// EditInitCode:
			// {...}

			const string INIT_DEPENDENCIES_KEY = "InitDependencies:";
			const string USES_KEY = "Uses:";
			const string EDITOR_USES_KEY = "EditorUses:";
			const string CUSTOM_EXCEPTION_LOGGING_KEY = "CustomExceptionLogging:";
			const string INIT_CODE_KEY = "InitCode:";
			const string INIT_SCENE_CODE_KEY = "SceneInitCode:";
			const string EDITOR_INIT_CODE_KEY = "EditorInitCode:";

			var frameworkGroup = GetBestDependencyFrameworkGroupForCurrentSettings(package);
			var dependencies = frameworkGroup.Dependencies.Select(identifier => identifier.Id.Replace("nordeus.", "").Replace("unity.", "")).ToList();
			var uses = new List<string>();
			var editorUses = new List<string>();
			var customExceptionLoggingCode = "";
			var initCode = "";
			var editorInitCode = "";

			var line = 0;
			var initSceneCodeFound = false;
			for (; line < initTemplate.Length; line++)
			{
				if (initTemplate[line].StartsWith(INIT_DEPENDENCIES_KEY, StringComparison.Ordinal))
				{
					var additionalDeps = initTemplate[line].Substring(INIT_DEPENDENCIES_KEY.Length).Split(new []{','}, StringSplitOptions.RemoveEmptyEntries);
					foreach (var additionalDep in additionalDeps)
					{
						dependencies.Add(additionalDep.Trim());
					}
					continue;
				}

				if (initTemplate[line].StartsWith(USES_KEY, StringComparison.Ordinal))
				{
					var useModules = initTemplate[line].Substring(USES_KEY.Length).Split(new []{','}, StringSplitOptions.RemoveEmptyEntries);
					foreach (var useModule in useModules)
					{
						uses.Add(useModule.Trim());
					}
					continue;
				}

				if (initTemplate[line].StartsWith(EDITOR_USES_KEY, StringComparison.Ordinal))
				{
					var useModules = initTemplate[line].Substring(EDITOR_USES_KEY.Length).Split(new []{','}, StringSplitOptions.RemoveEmptyEntries);
					foreach (var useModule in useModules)
					{
						editorUses.Add(useModule.Trim());
					}
					continue;
				}

				if (initTemplate[line].StartsWith(CUSTOM_EXCEPTION_LOGGING_KEY, StringComparison.Ordinal))
				{
					line++;
					while (line + 1 < initTemplate.Length && !initTemplate[line + 1].StartsWith("}", StringComparison.Ordinal))
					{
						line++;
						customExceptionLoggingCode += "\t\t" + initTemplate[line] + "\r\n";
					}
					continue;
				}

				if (initTemplate[line].StartsWith(EDITOR_INIT_CODE_KEY, StringComparison.Ordinal))
				{
					line += 3; // We want to skip first and last line containing only braces
					while (line < initTemplate.Length)
					{
						editorInitCode += "\t\t" + initTemplate[line - 1] + "\r\n";
						line++;
					}
					continue;
				}

				var initCodeFound = initTemplate[line].StartsWith(INIT_CODE_KEY, StringComparison.Ordinal);
				initSceneCodeFound = initTemplate[line].StartsWith(INIT_SCENE_CODE_KEY, StringComparison.Ordinal);
				if (initCodeFound || initSceneCodeFound)
				{
					line++;
					while (line < initTemplate.Length && !initTemplate[line].Contains(EDITOR_INIT_CODE_KEY))
					{
						initCode += "\t\t" + initTemplate[line] + "\r\n";
						line++;
					}

					if (line < initTemplate.Length) line--;
				}
			}

			if (writeCs)
			{
				foreach (var use in uses)
				{
					var usingLine = "using " + use + ";";
					if (initCs.Contains(usingLine)) continue;
					initCs = usingLine + "\r\n" + initCs;
					if (initIndex > 0) initIndex += usingLine.Length + 2;
				}
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
					var depMethod = PackageIdToMethodName(dependency);
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

			if (!initCodeExisted)
			{
				generatedInitCs = generatedInitCs.Substring(0, insertPos) + "\t\t\t" + initMethodName + "();\r\n" + generatedInitCs.Substring(insertPos);
			}

			if (customExceptionLoggingCode.Length > 0 && !initCs.Contains(customExceptionLoggingCode))
			{
				insertPos = initCs.IndexOf("\t\t}", StringComparison.Ordinal);
				initCs = initCs.Substring(0, insertPos) + customExceptionLoggingCode + initCs.Substring(insertPos);
				if (initIndex > 0) initIndex += customExceptionLoggingCode.Length;
			}

			initCode = "\r\n\t\tprivate static void " + initMethodName + "()\r\n" + initCode;
			if (writeCs)
			{
				insertPos = initIndex > 0 ? initIndex : initCs.LastIndexOf("\t}", StringComparison.Ordinal);
				initCs = initCs.Substring(0, insertPos) + initCode + initCs.Substring(insertPos);
			}

			insertPos = initTxtIndex >= 0 ? initTxtIndex : initTxt.Length;
			initTxt = initTxt.Substring(0, insertPos) + initCode + initTxt.Substring(insertPos);
			
			// Make sure the dir exists
			Directory.CreateDirectory(initCsDir);

			if (writeCs)
			{
				File.WriteAllText(initCsPath, initCs);
				File.WriteAllText(generatedInitCsPath, generatedInitCs);
			}
			else if (originalInitCode != initCode)
			{
				SystemProxy.LogError($"{initMethodName} is updated in the package but you have also modified it. Compare your version with the one in AppInitializer.txt file.");
			}

			if (!initTxt.Contains("// DO NOT MODIFY, THIS IS A GENERATED FILE"))
			{
				initTxt = "// DO NOT MODIFY, THIS IS A GENERATED FILE\r\n" +
				          "// This file will always contain the original init code for each package that you have installed.\r\n" +
				          "// You can compare it with AppInitializer.cs to see what are your modifications and if there are\r\n" +
				          "// any new modifications from the package that you need to add. NugetForUnity also uses this file\r\n" +
				          "// to determine if it can replace the package init code in AppInitializer.cs since it will only do\r\n" +
				          "// that if current init code matches the one from this file which means you didn't add custom\r\n" +
				          "// modifications to it.\r\n" + initTxt;
			}
			File.WriteAllText(initTxtPath, initTxt);

			if (editorInitCode.Length == 0 || initCodeExisted)
			{
				if (!File.Exists(editorCsPath))
				{
					Directory.CreateDirectory(editorCsDir);
					File.WriteAllText(editorCsPath, editorCs);
				}
				return;
			}
			
			foreach (var editorUse in editorUses)
			{
				var usingLine = "using " + editorUse + ";";
				if (editorCs.Contains(usingLine)) continue;
				editorCs = usingLine + "\r\n" + editorCs;
			}

			var braceCount = 0;
			int n;
			for (n = editorCs.Length - 1; n > 0; n--)
			{
				if (editorCs[n] != '}') continue;
				braceCount++;
				if (braceCount == 3) break;
			}

			if (braceCount != 3)
			{
				SystemProxy.LogError($"Invalid format of EditorAppInitializer.cs. Can't insert following code:\n{editorInitCode}");
			}

			while (n > 0 && editorCs[n] != '\n') n--;
			n++;
			editorCs = editorCs.Substring(0, n) + editorInitCode + editorCs.Substring(n);
			Directory.CreateDirectory(editorCsDir);
			File.WriteAllText(editorCsPath, editorCs);
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
				AccountUrlPattern = @"^https:\/\/(?<account>[-a-zA-Z0-9]+)\.pkgs\.visualstudio\.com",
				ProviderUrlTemplate = "https://{account}.pkgs.visualstudio.com/_apis/public/nuget/client/CredentialProviderBundle.zip"
			},
			new AuthenticatedFeed()
			{
				AccountUrlPattern = @"^https:\/\/pkgs\.dev\.azure\.com\/(?<account>[-a-zA-Z0-9]+)\/",
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
			getRequest.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.None;
			if (timeOut.HasValue)
			{
				getRequest.Timeout = timeOut.Value;
				getRequest.ReadWriteTimeout = timeOut.Value;
			}

			if (string.IsNullOrEmpty(password))
			{
				var creds = GetCredentialFromProvider(GetTruncatedFeedUri(getRequest.RequestUri));
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
		public static bool Restore()
		{
			UpdateInstalledPackages();
			var packagesToInstall = PackagesConfigFile.Packages.FindAll(package => !IsInstalled(package));

			if (packagesToInstall.Count == 0)
			{
				LogVerbose("No packages need restoring");
				CheckForUnnecessaryPackages();
				return true;
			}

			var stopwatch = new Stopwatch();
			stopwatch.Start();

			var allSucceeded = true;

			try
			{
				var progressStep = 1.0f / packagesToInstall.Count;
				float currentProgress = 0;

				// copy the list since the InstallIdentifier operation below changes the actual installed packages list

				LogVerbose("Restoring {0} packages.", packagesToInstall.Count);

				foreach (var package in packagesToInstall)
				{
					if (package != null)
					{
						SystemProxy.DisplayProgress("Restoring NuGet Packages", $"Restoring {package.Id} {package.Version}", currentProgress);
						
						LogVerbose("---Restoring {0} {1}", package.Id, package.Version);
						allSucceeded = allSucceeded && InstallIdentifier(package);
					}

					currentProgress += progressStep;
				}

				CheckForUnnecessaryPackages();
			}
			catch (Exception e)
			{
				allSucceeded = false;
				SystemProxy.ShowAlert("Failed to restore packages: " + e.Message);
				SystemProxy.LogError(e.ToString());
			}
			finally
			{
				stopwatch.Stop();
				LogVerbose("Restoring packages took {0} ms", stopwatch.ElapsedMilliseconds);

				SystemProxy.RefreshAssets();
				SystemProxy.ClearProgress();
			}

			return allSucceeded;
		}

		internal static void CheckForUnnecessaryPackages()
		{
			if (!Directory.Exists(NugetConfigFile.RepositoryPath))
				return;

			var directories = Directory.GetDirectories(NugetConfigFile.RepositoryPath, "*", SearchOption.TopDirectoryOnly);
			var packageNames = new HashSet<string>(PackagesConfigFile.Packages.Select(package => $"{package.Id}.{package.Version}"));
			foreach (var folder in directories)
			{
				var dirName = Path.GetFileName(folder);
				if (!string.IsNullOrEmpty(dirName) && dirName[0] == '.') continue;

				if (packageNames.Contains(dirName)) continue;
				
				LogVerbose("---DELETE unnecessary package {0}", dirName);

				DeleteDirectory(folder);
				DeleteFile(folder + ".meta");
			}

		}

		/// <summary>
		/// Checks if a given package is installed.
		/// </summary>
		/// <param name="package">The package to check if is installed.</param>
		/// <returns>True if the given package is installed. False if it is not.</returns>
		internal static bool IsInstalled(NugetPackageIdentifier package)
		{
			if (IsAlreadyImportedInEngine(package))
			{
				return true;
			}

			var isInstalled = false;

			if (installedPackages.TryGetValue(package.Id, out var installedPackage))
			{
				isInstalled = package.CompareVersion(installedPackage.Version) == 0;
			}

			return isInstalled;
		}

		/// <summary>
		/// Data class returned from nuget credential providers in a JSON format. As described here:
		/// https://docs.microsoft.com/en-us/nuget/reference/extensibility/nuget-exe-credential-providers#creating-a-nugetexe-credential-provider
		/// </summary>
		private struct CredentialProviderResponse
		{
			public string Username;
			public string Password;
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
					SystemProxy.LogError($"Failed to download credential provider from {credentialProviderRequest.Address}: {e.Message}");
				}
			}

		}

		/// <summary>
		/// Helper function to aquire a token to access VSTS hosted nuget feeds by using the CredentialProvider.VSS.exe
		/// tool. Downloading it from the VSTS instance if needed.
		/// See here for more info on nuget Credential Providers:
		/// https://docs.microsoft.com/en-us/nuget/reference/extensibility/nuget-exe-credential-providers
		/// </summary>
		/// <param name="feedUri">The hostname where the VSTS instance is hosted (such as microsoft.pkgs.visualsudio.com.</param>
		/// <returns>The password in the form of a token, or null if the password could not be aquired</returns>
		private static CredentialProviderResponse? GetCredentialFromProvider(Uri feedUri)
		{
			if (!cachedCredentialsByFeedUri.TryGetValue(feedUri, out var response))
			{
				response = GetCredentialFromProvider_Uncached(feedUri, true);
				cachedCredentialsByFeedUri[feedUri] = response;
			}
			return response;
		}

		/// <summary>
		/// Given the URI of a nuget method, returns the URI of the feed itself without the method and query parameters.
		/// </summary>
		/// <param name="methodUri">URI of nuget method.</param>
		/// <returns>URI of the feed without the method and query parameters.</returns>
		private static Uri GetTruncatedFeedUri(Uri methodUri)
		{
			var truncatedUriString = methodUri.GetLeftPart(UriPartial.Path);
			
			// Pull off the function if there is one
			if (truncatedUriString.EndsWith(")"))
			{
				var lastSeparatorIndex = truncatedUriString.LastIndexOf('/');
				if (lastSeparatorIndex != -1)
				{
					truncatedUriString = truncatedUriString.Substring(0, lastSeparatorIndex);
				}
			}

			var truncatedUri = new Uri(truncatedUriString);
			return truncatedUri;
		}

		/// <summary>
		/// Clears static credentials previously cached by GetCredentialFromProvider.
		/// </summary>
		public static void ClearCachedCredentials()
		{
			cachedCredentialsByFeedUri.Clear();
		}

		/// <summary>
		/// Internal function called by GetCredentialFromProvider to implement retrieving credentials. For performance reasons,
		/// most functions should call GetCredentialFromProvider in order to take advantage of cached credentials.
		/// </summary>
		private static CredentialProviderResponse? GetCredentialFromProvider_Uncached(Uri feedUri, bool downloadIfMissing)
		{
			LogVerbose("Getting credential for {0}", feedUri);

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
			var toolsPackagesFolder = Path.Combine(SystemProxy.CurrentDir, "../Packages");
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
						SystemProxy.LogError($"Failed to get credentials from {providerPath}!\n\tOutput\n\t{output}\n\tErrors\n\t{errors}");
						return null;
					}
					case CredentialProviderExitCode.Success:
					{
						output = output.Trim('{', '}', ' ', '\t', '\r', '\n');
						var lines = output.Split(',');
						var result = new CredentialProviderResponse();
						foreach (var line in lines)
						{
							var keyVal = line.Split(':');
							if (keyVal.Length != 2) continue;
							if (keyVal[0].Contains(nameof(CredentialProviderResponse.Username)))
							{
								result.Username = keyVal[1].Trim(' ', '\t', '"');
							}
							else if (keyVal[0].Contains(nameof(CredentialProviderResponse.Password)))
							{
								result.Password = keyVal[1].Trim(' ', '\t', '"');
							}
						}

						return result;
					}
					default:
					{
						SystemProxy.LogWarning($"Unrecognized exit code {process.ExitCode} from {providerPath} {process.StartInfo.Arguments}");
						break;
					}
				}
			}

			if (downloadIfMissing)
			{
				DownloadCredentialProviders(feedUri);
				return GetCredentialFromProvider_Uncached(feedUri, false);
			}

			return null;
		}
	}
}
