using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BuildToolUtilities;

namespace BuildTool
{
	// Caches information about C++ source files;
	// whether they contain reflection markup, what the first included header is, and so on.
	class SourceFileMetadataCache
	{
		// Information about the first file included from a source file
		class IncludeInfo
		{
			public long   LastWriteTimeUtc; // Last write time of the file when the data was cached
			public string IncludeText;      // Contents of the include directive
		}

		// Information about whether a file contains reflection markup
		class ReflectionInfo
		{
			public long LastWriteTimeUtc; // Last write time of the file when the data was cached
			public bool bContainsMarkup;  // Whether or not the file contains reflection markup
		}

		// The current file version
		public const int CurrentVersion = 3;

		// Location of this dependency cache
		private readonly FileReference Location;

		// Directory for files to cache dependencies for.
		private readonly DirectoryReference BaseDirectory;

		// The parent cache.
		private readonly SourceFileMetadataCache Parent;

		// Map from file item to source file info
		private readonly ConcurrentDictionary<FileItem, IncludeInfo> FileToIncludeInfo = new ConcurrentDictionary<FileItem, IncludeInfo>();

		// Map from file item to header file info
		private readonly ConcurrentDictionary<FileItem, ReflectionInfo> FileToReflectionInfo = new ConcurrentDictionary<FileItem, ReflectionInfo>();

		// Whether the cache has been modified and needs to be saved
		private bool bModified;

		// ^   Matches the beginning of input.
		// \s  Matches any white space including spaces, tabs, form-feed characters, and so on.
		// *   Matches the preceding character zero or more times. For example, zo* matches either z or zoo.
		// x|y Matches either x or y. For example, z|wood matches z or wood. (z|w)oo matches zoo or wood.
		// \b  Matches a word boundary, that is, the position between a word and a space. For example, er\b matches the er in never but not the er in verb.

		// Regex that matches C++ code with UObject declarations which we will need to generated code for.
		private static readonly Regex ReflectionMarkupRegex = new Regex("^\\s*U(CLASS|STRUCT|ENUM|INTERFACE|DELEGATE)\\b", RegexOptions.Compiled | RegexOptions.Multiline);

		// \t Matches a tab character.

		// Regex that matches #include statements.
		private static readonly Regex IncludeRegex = new Regex("^[ \t]*#[ \t]*include[ \t]*[<\"](?<HeaderFile>[^\">]*)[\">]", 
			RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.ExplicitCapture);

		// Regex that matches #import directives in mm files
		private static readonly Regex ImportRegex = new Regex("^[ \t]*#[ \t]*import[ \t]*[<\"](?<HeaderFile>[^\">]*)[\">]", 
			RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.ExplicitCapture);

		// Static cache of all constructed dependency caches
		private static readonly Dictionary<FileReference, SourceFileMetadataCache> Caches = new Dictionary<FileReference, SourceFileMetadataCache>();

		// Constructs a dependency cache.
		// This method is private; call CppDependencyCache.Create() to create a cache hierarchy for a given project.
		// <param name="Location">File to store the cache</param>
		// <param name="BaseDir">Base directory for files that this cache should store data for</param>
		// <param name="Parent">The parent cache to use</param>
		private SourceFileMetadataCache(FileReference Location, DirectoryReference BaseDir, SourceFileMetadataCache Parent)
		{
			this.Location = Location;
			this.BaseDirectory = BaseDir;
			this.Parent = Parent;

			if(FileReference.Exists(Location))
			{
				Read();
			}
		}

		// Gets the first included file from a source file
		// <param name="SourceFile">The source file to parse</param>
		// <returns>Text from the first include directive. Null if the file did not contain any include directives.</returns>
		public string GetFirstInclude(FileItem SourceFile)
		{
			if(Parent != null && !SourceFile.FileDirectory.IsUnderDirectory(BaseDirectory))
			{
				return Parent.GetFirstInclude(SourceFile);
			}
			else
			{
				if (!FileToIncludeInfo.TryGetValue(SourceFile, out IncludeInfo IncludeInfo) 
					|| IncludeInfo.LastWriteTimeUtc < SourceFile.LastWriteTimeUtc.Ticks)
				{
					IncludeInfo = new IncludeInfo
					{
						LastWriteTimeUtc = SourceFile.LastWriteTimeUtc.Ticks,
						IncludeText = ParseFirstInclude(SourceFile.FileDirectory)
					};

					FileToIncludeInfo[SourceFile] = IncludeInfo;
					bModified = true;
				}
				return IncludeInfo.IncludeText;
			}
		}

		// Determines whether the given file contains reflection markup
		// <param name="SourceFile">The source file to parse</param>
		// <returns>True if the file contains reflection markup</returns>
		public bool ContainsReflectionMarkup(FileItem SourceFile)
		{
			if(Parent != null 
			&& !SourceFile.FileDirectory.IsUnderDirectory(BaseDirectory))
			{
				return Parent.ContainsReflectionMarkup(SourceFile);
			}
			else
			{
				if (!FileToReflectionInfo.TryGetValue(SourceFile, out ReflectionInfo ReflectionInfo) 
					|| ReflectionInfo.LastWriteTimeUtc < SourceFile.LastWriteTimeUtc.Ticks)
				{
					ReflectionInfo = new ReflectionInfo
					{
						LastWriteTimeUtc = SourceFile.LastWriteTimeUtc.Ticks,
						bContainsMarkup  = ReflectionMarkupRegex.IsMatch(FileReference.ReadAllText(SourceFile.FileDirectory))
					};

					FileToReflectionInfo[SourceFile] = ReflectionInfo;
					bModified = true;
				}
				return ReflectionInfo.bContainsMarkup;
			}
		}

		// Parse the first include directive from a source file
		// <param name="SourceFile">The source file to parse</param>
		// <returns>The first include directive</returns>
		static string ParseFirstInclude(FileReference SourceFile)
		{
			bool bMatchImport = SourceFile.HasExtension(Tag.Ext.ObjCSource) || SourceFile.HasExtension(Tag.Ext.ObjCSource2);
			using(StreamReader Reader = new StreamReader(SourceFile.FullName, true))
			{
				for(;;)
				{
					string Line = Reader.ReadLine();
					if(Line == null)
					{
						return null;
					}

					Match IncludeMatch = IncludeRegex.Match(Line);
					if(IncludeMatch.Success)
					{
						return IncludeMatch.Groups[1].Value;
					}

					if(bMatchImport)
					{
						Match ImportMatch = ImportRegex.Match(Line);
						if(ImportMatch.Success)
						{
							return IncludeMatch.Groups[1].Value;
						}
					}
				}
			}
		}

		// Creates a cache hierarchy for a particular target
		// <param name="ProjectFile">Project file for the target being built</param>
		// <returns>Dependency cache hierarchy for the given project</returns>
		public static SourceFileMetadataCache CreateHierarchy(FileReference ProjectFile)
		{
			SourceFileMetadataCache Cache = null;

			if(ProjectFile == null || !BuildTool.IsEngineInstalled())
			{
				FileReference EngineCacheLocation = FileReference.Combine(BuildTool.EngineDirectory, Tag.Directory.Generated, Tag.Directory.Build, Tag.Binary.SourceFileCacheBin);
				Cache = FindOrAddCache(EngineCacheLocation, BuildTool.EngineDirectory, Cache);
			}

			if(ProjectFile != null)
			{
				FileReference ProjectCacheLocation = FileReference.Combine(ProjectFile.Directory, Tag.Directory.Generated, Tag.Directory.Build, Tag.Binary.SourceFileCacheBin);
				Cache = FindOrAddCache(ProjectCacheLocation, ProjectFile.Directory, Cache);
			}

			return Cache;
		}

		// Enumerates all the locations of metadata caches for the given target
		// <param name="ProjectFile">Project file for the target being built</param>
		// <returns>Dependency cache hierarchy for the given project</returns>
		public static IEnumerable<FileReference> GetFilesToClean(FileReference ProjectFile)
		{
			if(ProjectFile == null || !BuildTool.IsEngineInstalled())
			{
				yield return FileReference.Combine(BuildTool.EngineDirectory, Tag.Directory.Generated, Tag.Directory.Build, Tag.Binary.SourceFileCacheBin);
			}
			if(ProjectFile != null)
			{
				yield return FileReference.Combine(ProjectFile.Directory, Tag.Directory.Generated, Tag.Directory.Build, Tag.Binary.SourceFileCacheBin);
			}
		}

		// Reads a cache from the given location, or creates it with the given settings
		// <param name="Location">File to store the cache</param>
		// <param name="BaseDirectory">Base directory for files that this cache should store data for</param>
		// <param name="Parent">The parent cache to use</param>
		// <returns>Reference to a dependency cache with the given settings</returns>
		static SourceFileMetadataCache FindOrAddCache(FileReference Location, DirectoryReference BaseDirectory, SourceFileMetadataCache Parent)
		{
			lock(Caches)
			{
				if (Caches.TryGetValue(Location, out SourceFileMetadataCache Cache))
				{
					Debug.Assert(Cache.BaseDirectory == BaseDirectory);
					Debug.Assert(Cache.Parent == Parent);
				}
				else
				{
					Cache = new SourceFileMetadataCache(Location, BaseDirectory, Parent);
					Caches.Add(Location, Cache);
				}
				return Cache;
			}
		}

		// Save all the caches that have been modified
		public static void SaveAll() => Parallel.ForEach(Caches.Values, Cache => { if (Cache.bModified) { Cache.Write(); } });

		// Reads data for this dependency cache from disk
		private void Read()
		{
			try
			{
				using(BinaryArchiveReader Reader = new BinaryArchiveReader(Location))
				{
					int Version = Reader.ReadInt();
					if(Version != CurrentVersion)
					{
						Log.TraceLog("Unable to read dependency cache from {0}; version {1} vs current {2}", Location, Version, CurrentVersion);
						return;
					}

					int FileToFirstIncludeCount = Reader.ReadInt();
					for(int Idx = 0; Idx < FileToFirstIncludeCount; ++Idx)
					{
						FileItem File = Reader.ReadCompactFileItem();

						IncludeInfo IncludeInfo = new IncludeInfo
						{
							LastWriteTimeUtc = Reader.ReadLong(),
							IncludeText      = Reader.ReadString()
						};

						FileToIncludeInfo[File] = IncludeInfo;
					}

					int FileToMarkupFlagCount = Reader.ReadInt();
					for(int Idx = 0; Idx < FileToMarkupFlagCount; ++Idx)
					{
						FileItem File = Reader.ReadCompactFileItem();

						ReflectionInfo ReflectionInfo = new ReflectionInfo
						{
							LastWriteTimeUtc = Reader.ReadLong(),
							bContainsMarkup  = Reader.ReadBool()
						};

						FileToReflectionInfo[File] = ReflectionInfo;
					}
				}
			}
			catch(Exception Ex)
			{
				Log.TraceWarning("Unable to read {0}. See log for additional information.", Location);
				Log.TraceLog("{0}", ExceptionUtils.FormatExceptionDetails(Ex));
			}
		}

		// Writes data for this dependency cache to disk
		private void Write()
		{
			DirectoryReference.CreateDirectory(Location.Directory);
			using(FileStream Stream = File.Open(Location.FullName, FileMode.Create, FileAccess.Write, FileShare.Read))
			{
				using(BinaryArchiveWriter Writer = new BinaryArchiveWriter(Stream))
				{
					Writer.WriteInt(CurrentVersion);

					Writer.WriteInt(FileToIncludeInfo.Count);
					foreach(KeyValuePair<FileItem, IncludeInfo> Pair in FileToIncludeInfo)
					{
						Writer.WriteCompactFileItem(Pair.Key);
						Writer.WriteLong(Pair.Value.LastWriteTimeUtc);
						Writer.WriteString(Pair.Value.IncludeText);
					}

					Writer.WriteInt(FileToReflectionInfo.Count);
					foreach(KeyValuePair<FileItem, ReflectionInfo> Pair in FileToReflectionInfo)
					{
						Writer.WriteCompactFileItem(Pair.Key);
						Writer.WriteLong(Pair.Value.LastWriteTimeUtc);
						Writer.WriteBool(Pair.Value.bContainsMarkup);
					}
				}
			}
			bModified = false;
		}
	}
}