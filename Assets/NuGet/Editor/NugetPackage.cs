﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;

namespace NugetForUnity
{
	/// <summary>
	/// Represents a package available from NuGet.
	/// </summary>
	[Serializable]
	public class NugetPackage : NugetPackageIdentifier, IEquatable<NugetPackage>, IEqualityComparer<NugetPackage>
	{
		/// <summary>
		/// Gets or sets the title (not ID) of the package.  This is the "friendly" name that only appears in GUIs and on webpages.
		/// </summary>
		public string Title;

		/// <summary>
		/// Gets or sets the description of the NuGet package.
		/// </summary>
		public string Description;

		/// <summary>
		/// Gets or sets the summary of the NuGet package.
		/// </summary>
		public string Summary;

		/// <summary>
		/// Gets or sets the release notes of the NuGet package.
		/// </summary>
		public string ReleaseNotes;

		/// <summary>
		/// Gets or sets the URL for the location of the license of the NuGet package.
		/// </summary>
		public string LicenseUrl;

		/// <summary>
		/// Gets or sets the URL for the location of the actual (.nupkg) NuGet package.
		/// </summary>
		public string DownloadUrl;

		/// <summary>
		/// Gets or sets the DownloadCount.
		/// </summary>
		public int DownloadCount;

		/// <summary>
		/// Gets or sets the authors of the package.
		/// </summary>
		public string Authors;

		/// <summary>
		/// Gets or sets the <see cref="NugetPackageSource"/> that contains this package.
		/// </summary>
		public NugetPackageSource PackageSource;

		/// <summary>
		/// Gets or sets the icon for the package as a <see cref="UnityEngine.Texture2D"/>. 
		/// </summary>
		public UnityEngine.Texture2D Icon;

		/// <summary>
		/// Gets or sets the NuGet packages that this NuGet package depends on.
		/// </summary>
		public List<NugetFrameworkGroup> Dependencies = new List<NugetFrameworkGroup>();

		/// <summary>
		/// Gets or sets the url for the location of the package's source code.
		/// </summary>
		public string ProjectUrl;

		/// <summary>
		/// Gets or sets the url for the location of the package's source code.
		/// </summary>
		public string RepositoryUrl;

		/// <summary>
		/// Gets or sets the type of source control software that the package's source code resides in.
		/// </summary>
		public RepositoryType RepositoryType;

		/// <summary>
		/// Gets or sets the source control branch the package is from.
		/// </summary>
		public string RepositoryBranch;

		/// <summary>
		/// Gets or sets the source control commit the package is from.
		/// </summary>
		public string RepositoryCommit;

		/// <summary>
		/// Checks to see if this <see cref="NugetPackage"/> is equal to the given one.
		/// </summary>
		/// <param name="other">The other <see cref="NugetPackage"/> to check equality with.</param>
		/// <returns>True if the packages are equal, otherwise false.</returns>
		public bool Equals(NugetPackage other)
		{
			return other != null && other.Id == Id && other.Version == Version;
		}

		/// <summary>
		/// Creates a new <see cref="NugetPackage"/> from the given <see cref="NuspecFile"/>.
		/// </summary>
		/// <param name="nuspec">The <see cref="NuspecFile"/> to use to create the <see cref="NugetPackage"/>.</param>
		/// <returns>The newly created <see cref="NugetPackage"/>.</returns>
		public static NugetPackage FromNuspec(NuspecFile nuspec)
		{
			var package = new NugetPackage
			{
				Id = nuspec.Id,
				Version = nuspec.Version,
				Title = nuspec.Title,
				Description = nuspec.Description,
				ReleaseNotes = nuspec.ReleaseNotes,
				LicenseUrl = nuspec.LicenseUrl,
				ProjectUrl = nuspec.ProjectUrl,
				Authors = nuspec.Authors,
				Summary = nuspec.Summary
			};


			if (!string.IsNullOrEmpty(nuspec.IconUrl))
			{
				SystemProxy.DownloadAndSetIcon(package, nuspec.IconUrl);
			}

			package.RepositoryUrl = nuspec.RepositoryUrl;

			try
			{
				package.RepositoryType = (RepositoryType)Enum.Parse(typeof(RepositoryType), nuspec.RepositoryType, true);
			}
			catch (Exception) { }

			package.RepositoryBranch = nuspec.RepositoryBranch;
			package.RepositoryCommit = nuspec.RepositoryCommit;

			// if there is no title, just use the ID as the title
			if (string.IsNullOrEmpty(package.Title))
			{
				package.Title = package.Id;
			}

			package.Dependencies = nuspec.Dependencies;

			return package;
		}

		/// <summary>
		/// Loads a <see cref="NugetPackage"/> from the .nupkg file at the given filepath.
		/// </summary>
		/// <param name="packageFilePath">The filepath to the .nupkg file to load.</param>
		/// <returns>The <see cref="NugetPackage"/> loaded from the .nupkg file.</returns>
		public static NugetPackage FromPackageFile(string packageFilePath)
		{
			var packageFileInfo = new FileInfo(packageFilePath);
			var nuspecCachedPath = GetNuSpecFileCachePath(packageFilePath);
			var nuspecCachedFileInfo = new FileInfo(nuspecCachedPath);
			NuspecFile nuspecFile;
            
			if (nuspecCachedFileInfo.Exists
                && packageFileInfo.Exists
                && nuspecCachedFileInfo.LastWriteTimeUtc > packageFileInfo.LastWriteTimeUtc)
			{
				nuspecFile = NuspecFile.FromXmlFile(nuspecCachedPath);
			}
			else
			{
                var rawXml = NuspecFile.ReadNuspecXDocumentFromPackageFile(packageFilePath);

                nuspecFile = rawXml != null
                    ? NuspecFile.From(rawXml)
                    : new NuspecFile { Description = $"COULD NOT LOAD {packageFilePath}" };

                // cache nuspec file for next time
                rawXml?.Save(nuspecCachedPath);
            }
			
			var package = FromNuspec(nuspecFile);
			package.DownloadUrl = packageFilePath;
			return package;
		}
        
        public static string NuSpecFileCacheDirectoryPath =>
            Path.Combine(SystemProxy.CurrentDir, "../Library/NuSpecFilesCache/");

        public static string GetNuSpecFileCachePath(string packagePath) =>
            Path.Combine(NuSpecFileCacheDirectoryPath, Path.GetFileNameWithoutExtension(packagePath) + ".nuspec");

        /// <summary>
		/// Checks to see if the two given <see cref="NugetPackage"/>s are equal.
		/// </summary>
		/// <param name="x">The first <see cref="NugetPackage"/> to compare.</param>
		/// <param name="y">The second <see cref="NugetPackage"/> to compare.</param>
		/// <returns>True if the packages are equal, otherwise false.</returns>
		public bool Equals(NugetPackage x, NugetPackage y)
		{
			if (x == null || y == null) return x == null && y == null;
			return x.Id == y.Id && x.Version == y.Version;
		}

		/// <summary>
		/// Gets the hashcode for the given <see cref="NugetPackage"/>.
		/// </summary>
		/// <returns>The hashcode for the given <see cref="NugetPackage"/>.</returns>
		public int GetHashCode(NugetPackage obj)
		{
			return obj.Id.GetHashCode() ^ obj.Version.GetHashCode();
		}
	}
}