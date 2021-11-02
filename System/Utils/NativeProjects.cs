using System.Collections.Generic;
using System.IO;
using BuildToolUtilities;

namespace BuildTool
{
	// Utility functions for querying native projects (ie. those found via a .uprojectdirs query)
	public static class NativeProjects
	{
		private static readonly object Mutex = new object();
		private static HashSet<DirectoryReference>       CachedNativeProjectBaseDirs;   // The native project base directories
		private static HashSet<FileReference>            CachedNativeProjectInBaseDirs; // Cached list of project files within all the base directories
		private static Dictionary<string, FileReference> CachedTargetNameToProjectFile; // Cached map of target names to the project file they belong to

		// Clear our cached properties.
		// Generally only needed if your script has modified local files...
		public static void ClearCache()
		{
			CachedNativeProjectBaseDirs   = null;
			CachedNativeProjectInBaseDirs = null;
			CachedTargetNameToProjectFile = null;
		}

		// Retrieve the list of base directories for native projects
		public static IEnumerable<DirectoryReference> EnumerateBaseDirectories()
		{
			if(CachedNativeProjectBaseDirs == null)
			{
				lock(Mutex)
				{
					if(CachedNativeProjectBaseDirs == null)
					{
						HashSet<DirectoryReference> BaseDirs = new HashSet<DirectoryReference>();
						foreach (FileReference RootFile in DirectoryLookupCache.EnumerateFiles(BuildTool.RootDirectory))
						{
							if(RootFile.HasExtension(Tag.Ext.ProjectDir))
							{
								foreach(string Line in File.ReadAllLines(RootFile.FullName))
								{
									string TrimLine = Line.Trim();
									if(!TrimLine.StartsWith(";"))
									{
										DirectoryReference BaseProjectDir = DirectoryReference.Combine(BuildTool.RootDirectory, TrimLine);
										if(BaseProjectDir.IsUnderDirectory(BuildTool.RootDirectory))
										{
											BaseDirs.Add(BaseProjectDir);
										}
										else
										{
											Log.TraceWarning("Project search path '{0}' referenced by '{1}' is not under '{2}', ignoring.", 
												TrimLine, RootFile, BuildTool.RootDirectory);
										}
									}
								}
							}
						}

						CachedNativeProjectBaseDirs = BaseDirs;
					}
				}
			}
			return CachedNativeProjectBaseDirs;
		}

		// Returns a list of all the projects
		public static IEnumerable<FileReference> EnumerateProjectFiles()
		{
			if(CachedNativeProjectInBaseDirs == null)
			{
				lock(Mutex)
				{
					if(CachedNativeProjectInBaseDirs == null)
					{
						HashSet<FileReference> ProjectFiles = new HashSet<FileReference>();
						foreach(DirectoryReference BaseDirectory in EnumerateBaseDirectories())
						{
							if(DirectoryLookupCache.DirectoryExists(BaseDirectory))
							{
								foreach(DirectoryReference SubDirectory in DirectoryLookupCache.EnumerateDirectories(BaseDirectory))
								{
									foreach(FileReference File in DirectoryLookupCache.EnumerateFiles(SubDirectory))
									{
										if(File.HasExtension(Tag.Ext.Project))
										{
											ProjectFiles.Add(File);
										}
									}
								}
							}
						}
						CachedNativeProjectInBaseDirs = ProjectFiles;
					}
				}
			}

			return CachedNativeProjectInBaseDirs;
		}

		// Get the project folder for the given target name
		public static bool TryGetProjectForTarget(string InTargetName, out FileReference OutProjectFileName)
		{
			if (CachedTargetNameToProjectFile == null)
			{
				lock (Mutex)
				{
					Dictionary<string, FileReference> TargetNameToProjectFile = new Dictionary<string, FileReference>();
					foreach (FileReference ProjectFile in EnumerateProjectFiles())
					{
						DirectoryReference SourceDirectory = DirectoryReference.Combine(ProjectFile.Directory, Tag.Directory.SourceCode);
						if (DirectoryLookupCache.DirectoryExists(SourceDirectory))
						{
							RecursivelyFindTargetFiles(SourceDirectory, TargetNameToProjectFile, ProjectFile);
						}

						DirectoryReference IntermediateSourceDirectory = DirectoryReference.Combine(ProjectFile.Directory, Tag.Directory.Generated, Tag.Directory.SourceCode);
						if (DirectoryLookupCache.DirectoryExists(IntermediateSourceDirectory))
						{
							RecursivelyFindTargetFiles(IntermediateSourceDirectory, TargetNameToProjectFile, ProjectFile);
						}
					}
					CachedTargetNameToProjectFile = TargetNameToProjectFile;
				}
			}

			return CachedTargetNameToProjectFile.TryGetValue(InTargetName, out OutProjectFileName);
		}

		// Finds all target files under a given folder, and add them to the target name to project file map
		private static void RecursivelyFindTargetFiles
		(
			DirectoryReference                DirectoryToSearch, 
			Dictionary<string, FileReference> TargetNameToProjectFile, 
			FileReference                     ToFindProjectFile
		)
		{
			// Search for all target files within this directory
			bool bSearchSubFolders = true;
			foreach (FileReference File in DirectoryLookupCache.EnumerateFiles(DirectoryToSearch))
			{
				if (File.HasExtension(Tag.Ext.TargetCS))
				{
					string TargetName = Path.GetFileNameWithoutExtension(File.GetFileNameWithoutExtension());
					TargetNameToProjectFile[TargetName] = ToFindProjectFile;
					bSearchSubFolders = false;
				}
			}

			// If we didn't find anything, recurse through the subfolders
			if(bSearchSubFolders)
			{
				foreach(DirectoryReference SubDirectory in DirectoryLookupCache.EnumerateDirectories(DirectoryToSearch))
				{
					RecursivelyFindTargetFiles(SubDirectory, TargetNameToProjectFile, ToFindProjectFile);
				}
			}
		}

		// Checks if a given project is a native project
		public static bool IsNativeProject(FileReference ProjectFileToCheck)
		{
			EnumerateProjectFiles();
			return CachedNativeProjectInBaseDirs.Contains(ProjectFileToCheck);
		}
	}
}