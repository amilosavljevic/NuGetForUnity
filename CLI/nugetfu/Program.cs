using System;
using NugetForUnity;

namespace nugetfu
{
	public class Program
	{
		public static int Main(string[] args)
		{
			foreach (var s in args)
			{
				Console.WriteLine(s);
			}
			if (args.Length != 1 && args.Length != 2) return PrintUsage();
			if (args[0] != "restore") return PrintUsage();
			if (args.Length == 2)
			{
				SystemProxy.AppDir = args[1].Replace("'", "").Replace("\"", "");
			}

			return NugetHelper.Restore() ? 0 : 1;
		}

		private static int PrintUsage()
		{
			Console.WriteLine("Usage: nugetfu restore [pathToUnityExe]");
			return 1;
		}
	}
}
