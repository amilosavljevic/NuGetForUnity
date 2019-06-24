using System;
using System.IO;

namespace NugetForUnity
{
	public static class SystemProxy
	{
		public static string CurrentDir => Directory.GetCurrentDirectory();

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

		public static bool IsPlayingOrWillChangePlaymode()
		{
			return false;
		}

		public static int GetApiCompatibilityLevel()
		{
			return 3; // Code for NET_4_6
			//return 6; // Code for .net standard 2.0
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
