using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace NugetForUnity
{
	/// <summary>
	/// Represents a package.config file that holds the NuGet package dependencies for the project.
	/// </summary>
	public class PackagesConfigFile
	{
		/// <summary>
		/// Gets the <see cref="NugetPackageIdentifier"/>s contained in the package.config file.
		/// </summary>
		public List<NugetPackageIdentifier> Packages { get; private set; }

		/// <summary>
		/// Adds a package to the packages.config file.
		/// </summary>
		/// <param name="package">The NugetPackage to add to the packages.config file.</param>
		public void AddPackage(NugetPackageIdentifier package)
		{
			var existingPackage = Packages.Find(p => p.Id.ToLower() == package.Id.ToLower());
			if (existingPackage != null)
			{
				if (existingPackage < package)
				{
					SystemProxy.LogWarning($"{existingPackage.Id} {existingPackage.Version} is already listed in the packages.config file.  Updating to {package.Version}");
					Packages.Remove(existingPackage);
					Packages.Add(package);
				}
				else if (existingPackage > package)
				{
					SystemProxy.LogWarning($"Trying to add {package.Id} {package.Version} to the packages.config file.  {existingPackage.Version} is already listed, so using that.");
				}
			}
			else
			{
				Packages.Add(package);
			}
		}

		/// <summary>
		/// Removes a package from the packages.config file.
		/// </summary>
		/// <param name="package">The NugetPackage to remove from the packages.config file.</param>
		public void RemovePackage(NugetPackageIdentifier package)
		{
			Packages.Remove(package);
		}

		/// <summary>
		/// Loads a list of all currently installed packages by reading the packages.config file.
		/// </summary>
		/// <returns>A newly created <see cref="PackagesConfigFile"/>.</returns>
		public static PackagesConfigFile Load(string filepath)
		{
			var configFile = new PackagesConfigFile {Packages = new List<NugetPackageIdentifier>()};

			// Create a package.config file, if there isn't already one in the project
			if (!File.Exists(filepath))
			{
				SystemProxy.Log($"No packages.config file found. Creating default at {filepath}");

				configFile.Save(filepath);
				
				SystemProxy.RefreshAssets();
			}

			var packagesFile = XDocument.Load(filepath);
			foreach (var packageElement in packagesFile.Root.Elements())
			{
				var package = new NugetPackage
				{
					Id = packageElement.Attribute("id").Value,
					Version = packageElement.Attribute("version").Value,
					IsManuallyInstalled = packageElement.Attribute("manual") != null
				};
				configFile.Packages.Add(package);
			}

			return configFile;
		}

		/// <summary>
		/// Saves the packages.config file and populates it with given installed NugetPackages.
		/// </summary>
		/// <param name="filepath">The filepath to where this packages.config will be saved.</param>
		public void Save(string filepath)
		{
			Packages.Sort(delegate (NugetPackageIdentifier x, NugetPackageIdentifier y)
			{
				if (x.Id == null && y.Id == null) return 0;
				if (x.Id == null) return -1;
				if (y.Id == null) return 1;
				if (x.Id == y.Id) return string.Compare(x.Version, y.Version, StringComparison.Ordinal);
				return string.Compare(x.Id, y.Id, StringComparison.Ordinal);
			});

			var packagesFile = new XDocument();
			packagesFile.Add(new XElement("packages"));
			foreach (var package in Packages)
			{
				var packageElement = new XElement("package");
				packageElement.Add(new XAttribute("id", package.Id));
				packageElement.Add(new XAttribute("version", package.Version));
				if (package.IsManuallyInstalled) packageElement.Add(new XAttribute("manual", "true"));
				packagesFile.Root.Add(packageElement);
			}

			// remove the read only flag on the file, if there is one.
			if (File.Exists(filepath))
			{
				var attributes = File.GetAttributes(filepath);

				if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
				{
					attributes &= ~FileAttributes.ReadOnly;
					File.SetAttributes(filepath, attributes);
				}
			}
			using (var xw = XmlWriter.Create(filepath, new XmlWriterSettings {IndentChars = "\t", Indent = true, NewLineChars = "\n"}))
			{
				packagesFile.Save(xw);
			}
		}
	}
}