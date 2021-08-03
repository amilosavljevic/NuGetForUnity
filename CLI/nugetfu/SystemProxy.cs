using System;
using System.IO;

namespace NugetForUnity
{
	public static class SystemProxy
	{
		private static string unityAppDir;
		
		public static string CurrentDir => Directory.GetCurrentDirectory();
		
		public static string AppDir
		{
			get => unityAppDir ?? Directory.GetCurrentDirectory();
			set => unityAppDir = value;
		}

		public static void Log(string message)
		{
			Console.WriteLine(message);
		}

		public static void LogWarning(string message)
		{
			var oldColor = Console.ForegroundColor;
			Console.ForegroundColor = ConsoleColor.Yellow;
			Console.WriteLine(message);
			Console.ForegroundColor = oldColor;
		}

		public static void LogError(string message)
		{
			var oldColor = Console.ForegroundColor;
			Console.ForegroundColor = ConsoleColor.Red;
			Console.WriteLine(message);
			Console.ForegroundColor = oldColor;
		}

		public static void RefreshAssets()
		{
			// Nothing needs to be done in CLI
		}

		public static bool IsPlayingOrWillChangePlaymode => false;
		
		public static string UnityVersion
		{
			get
			{
				var versionFile = File.ReadAllLines("../ProjectSettings/ProjectVersion.txt");
				foreach (var line in versionFile)
				{
					if (line.StartsWith("m_EditorVersion: ")) return line.Substring("m_EditorVersion: ".Length);
				}

				return "";
			}
		}

		public static int GetApiCompatibilityLevel()
		{
			const int netstandard = 6;
			const int framework = 3;
			var settings = File.ReadAllText("../ProjectSettings/ProjectSettings.asset");
			var apiIndex = settings.IndexOf("apiCompatibilityLevelPerPlatform", StringComparison.Ordinal);
			if (apiIndex < 0) return netstandard;
			settings = settings.Substring(apiIndex); // Remove everything before the part we are interested in
			var standardIndex = settings.IndexOf(": 6", StringComparison.Ordinal);
			var frameworkIndex = settings.IndexOf(": 3", StringComparison.Ordinal);
			if (frameworkIndex < 0) return netstandard;
			if (standardIndex < 0) return framework;
			// If both are found return the one that comes first
			return standardIndex < frameworkIndex ? netstandard : framework;
		}

		public static void DisplayProgress(string title, string info, float progress)
		{
			Console.WriteLine($"{title}: {(int)(progress * 100f)}%");
		}

		public static void ClearProgress()
		{
			// Nothing needs to be done in CLI
		}

		public static void DownloadAndSetIcon(NugetPackage package, string url)
		{
			//Nothing needs to be done in CLI
		}
	}
}
