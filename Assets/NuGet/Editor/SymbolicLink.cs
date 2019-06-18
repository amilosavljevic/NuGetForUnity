using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Nordeus.Nuget.Utility
{
	public static class SymbolicLink
	{
		/// <summary>
		///     Creates a symbolic link from the specified directory to the specified target directory.
		/// </summary>
		/// <param name="linkPath">The junction point path</param>
		/// <param name="targetDir">The target directory</param>
		/// <param name="overwrite">If true overwrites an existing directory</param>
		/// <exception cref="IOException">
		///     Thrown when the link could not be created or when
		///     an existing directory was found and <paramref name="overwrite" /> was false
		/// </exception>
		public static void Create(string linkPath, string targetDir, bool overwrite = false)
		{
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			{
				JunctionPoint.Create(linkPath, targetDir, overwrite);
				return;
			}

			var processInfo = new ProcessStartInfo("ln", $"-s \"{targetDir}\" \"{linkPath}\"")
			{
				CreateNoWindow = true,
				UseShellExecute = false,
				// *** Redirect the output ***
				RedirectStandardError = true,
				RedirectStandardOutput = true
			};

			using (var process = Process.Start(processInfo))
			{
				if (process == null) throw new IOException("Failed starting ln command");

				// *** Read the streams ***
				var output = new StringBuilder();
				var error = new StringBuilder();
				process.OutputDataReceived += (object sender, DataReceivedEventArgs e) => output.Append(e.Data);
				process.BeginOutputReadLine();

				process.ErrorDataReceived += (object sender, DataReceivedEventArgs e) => error.Append(e.Data);
				process.BeginErrorReadLine();

				process.WaitForExit();

				var exitCode = process.ExitCode;
				if (exitCode != 0) throw new IOException($"Error creating symlink:\n{output}\n{error}");
			}
		}

		/// <summary>
		///     Deletes a symbolic link
		/// </summary>
		/// <param name="linkPath">The path to the link</param>
		/// <exception cref="IOException">
		///     Thrown when the link can't be deleted
		/// </exception>
		public static void Delete(string linkPath)
		{
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			{
				JunctionPoint.Delete(linkPath);
				return;
			}

			Directory.Delete(linkPath);
		}

		/// <summary>
		///     Determines whether the specified path exists and refers to a symbolic link.
		/// </summary>
		/// <param name="path">The symbolic link path</param>
		/// <returns>True if the specified path represents a symbolic link</returns>
		/// <exception cref="IOException">
		///     Thrown if the specified path is invalid or some other error occurs
		/// </exception>
		public static bool Exists(string path)
		{
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			{
				return JunctionPoint.Exists(path);
			}

			return (new DirectoryInfo(path).Attributes & FileAttributes.ReparsePoint) != 0;
		}
	}
}