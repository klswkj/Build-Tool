using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BuildToolUtilities;

namespace BuildTool
{
	internal sealed class CppDependencyCacheTag
	{
		// 확장자는 BuildTool로 string변수로 편입, .tlh, .tli
		// 나머지는 여기에 "DependencyCache.bin"
	}

	// Reads the contents of C++ dependency files, and caches them for future iterations.
	internal sealed class CppDependencyCache
	{
		// Contents of a single dependency file
		private sealed class DependencyInfo
		{
			public long LastWriteTimeUtc;
			public List<FileItem> Files;

			public DependencyInfo(long LastWriteTimeUtc, List<FileItem> Files)
			{
				this.LastWriteTimeUtc = LastWriteTimeUtc;
				this.Files            = Files;
			}

			// # Keep Sync with between read and write.

			public static DependencyInfo Read(BinaryArchiveReader Reader)
			{
				long LastWriteTimeUtc = Reader.ReadLong();
				List<FileItem> Files  = Reader.ReadList(() => Reader.ReadCompactFileItem());

				return new DependencyInfo(LastWriteTimeUtc, Files);
			}

			public void Write(BinaryArchiveWriter Writer)
			{
				Writer.WriteLong(LastWriteTimeUtc);
				Writer.WriteList<FileItem>(Files, File => Writer.WriteCompactFileItem(File));
			}
		}

		public const int CurrentVersion = 2; // The current file version

		private readonly FileReference Location; // Location of this dependency cache
		private readonly DirectoryReference BaseDir; // Directory for files to cache dependencies for.
		private readonly CppDependencyCache ParentCache;
		private readonly ConcurrentDictionary<FileItem, DependencyInfo> DependencyFileToInfo // Map from file item to dependency info
			= new ConcurrentDictionary<FileItem, DependencyInfo>();

		private readonly static Dictionary<FileReference, CppDependencyCache> AllCaches 
			= new Dictionary<FileReference, CppDependencyCache>();

		private bool bModified; // Whether the cache has been modified and needs to be saved


		// Creates a cache hierarchy for a particular target
		public static CppDependencyCache CreateHierarchy
		(
			FileReference             TargetBeingBuiltProjectFile,
			string                    TargetName,
			BuildTargetPlatform      PlatformBeingBuilt,
			TargetConfiguration ConfigurationBeingBuilt,
			TargetType                TargetType,
			string                    TargetArchitecture
		)
		{
			CppDependencyCache OutCache = null;

			if (TargetBeingBuiltProjectFile == null || !BuildTool.IsEngineInstalled())
			{
				string AppName;
				if (TargetType == TargetType.Program)
				{
					AppName = TargetName;
				}
				else
				{
					AppName = BuildTarget.GetAppNameForTargetType(TargetType);
				}

				FileReference EngineCacheLocation 
					= FileReference.Combine
					(
						BuildTool.EngineDirectory,
						BuildTool.GetPlatformGeneratedFolder(PlatformBeingBuilt, TargetArchitecture), 
						AppName, 
						ConfigurationBeingBuilt.ToString(),
						Tag.Binary.DependencyCache + Tag.Ext.Bin
					);

				OutCache = FindOrAddCache(EngineCacheLocation, BuildTool.EngineDirectory, OutCache);
			}

			if (TargetBeingBuiltProjectFile != null)
			{
				FileReference ProjectCacheLocation 
					= FileReference.Combine
					(
						TargetBeingBuiltProjectFile.Directory,
						BuildTool.GetPlatformGeneratedFolder(PlatformBeingBuilt, TargetArchitecture), 
						TargetName, 
						ConfigurationBeingBuilt.ToString(),
						Tag.Binary.DependencyCache + Tag.Ext.Bin
					);

				OutCache = FindOrAddCache(ProjectCacheLocation, TargetBeingBuiltProjectFile.Directory, OutCache);
			}

			return OutCache;
		}

		// Constructs a dependency cache. This method is private;
		// call CppDependencyCache.Create() to create a cache hierarchy for a given project.
		private CppDependencyCache(FileReference CacheLocation, DirectoryReference DirectoryForStoreData, CppDependencyCache ParentCache)
		{
			this.Location    = CacheLocation;
			this.BaseDir     = DirectoryForStoreData;
			this.ParentCache = ParentCache;

			if (FileReference.Exists(CacheLocation))
			{
				Read();
			}
			else
			{
				throw new BuildException("Invalid Cpp Dependency Cache.");
			}
		}

		// Attempts to read the dependencies from the given input file
		public bool TryGetDependencies(FileItem CppListFileToRead, out List<FileItem> OutDependencyCppFiles)
		{
			if (!CppListFileToRead.Exists)
			{
				OutDependencyCppFiles = null;
				return false;
			}

			try
			{
				return TryGetDependenciesInternal(CppListFileToRead, out OutDependencyCppFiles);
			}
			catch (Exception Ex)
			{
				Log.TraceLog("Unable to read {0}:\n{1}", CppListFileToRead, ExceptionUtils.FormatExceptionDetails(Ex));
				OutDependencyCppFiles = null;
				return false;
			}
		}
		
		// Attempts to read dependencies from the given file.
		private bool TryGetDependenciesInternal(FileItem CppListFileToRead, out List<FileItem> OutDependencyCppFile)
		{
			if (ParentCache != null && !CppListFileToRead.FileDirectory.IsUnderDirectory(BaseDir))
			{
				return ParentCache.TryGetDependencies(CppListFileToRead, out OutDependencyCppFile);
			}
			else
			{
				if (DependencyFileToInfo.TryGetValue(CppListFileToRead, out DependencyInfo Info) && CppListFileToRead.LastWriteTimeUtc.Ticks <= Info.LastWriteTimeUtc)
				{
					OutDependencyCppFile = Info.Files;
					return true;
				}

				List<FileItem> DependencyItems = ReadDependenciesFile(CppListFileToRead.FileDirectory);
				DependencyFileToInfo.TryAdd(CppListFileToRead, new DependencyInfo(CppListFileToRead.LastWriteTimeUtc.Ticks, DependencyItems));
				bModified = true;

				OutDependencyCppFile = DependencyItems;
				return true;
			}
		}

		// Reads a cache from the given location, or creates it with the given settings
		private static CppDependencyCache FindOrAddCache(FileReference FileToStoreCache, DirectoryReference DirectoryForStoreData, CppDependencyCache ParentCache)
		{
			lock (AllCaches)
			{
				if (AllCaches.TryGetValue(FileToStoreCache, out CppDependencyCache Cache))
				{
					Debug.Assert(Cache.BaseDir == DirectoryForStoreData);
					Debug.Assert(Cache.ParentCache == ParentCache);
				}
				else
				{
					Cache = new CppDependencyCache(FileToStoreCache, DirectoryForStoreData, ParentCache);
					AllCaches.Add(FileToStoreCache, Cache);
				}
				return Cache;
			}
		}

		// Save all the caches that have been modified
		public static void SaveAll() 
			=> Parallel.ForEach(AllCaches.Values, Cache => { if (Cache.bModified) { Cache.Write(); } });

		// Reads data for this dependency cache from disk
		private void Read()
		{
			try
			{
				using (BinaryArchiveReader Reader = new BinaryArchiveReader(Location))
				{
					int Version = Reader.ReadInt();
					if (Version != CurrentVersion)
					{
						Log.TraceLog("Unable to read dependency cache from {0}; version {1} vs current {2}", Location, Version, CurrentVersion);
						return;
					}

					int Count = Reader.ReadInt();
					for (int Idx = 0; Idx < Count; ++Idx)
					{
						FileItem File = Reader.ReadFileItem();
						DependencyFileToInfo[File] = DependencyInfo.Read(Reader);
					}
				}
			}
			catch (Exception Ex)
			{
				Log.TraceWarning("Unable to read {0}. See log for additional information.", Location);
				Log.TraceLog("{0}", ExceptionUtils.FormatExceptionDetails(Ex));
			}
		}

		// Writes data for this dependency cache to disk
		private void Write()
		{
			DirectoryReference.CreateDirectory(Location.Directory);
			using (FileStream Stream = File.Open(Location.FullName, FileMode.Create, FileAccess.Write, FileShare.Read))
			{
				using (BinaryArchiveWriter Writer = new BinaryArchiveWriter(Stream))
				{
					Writer.WriteInt(CurrentVersion);

					Writer.WriteInt(DependencyFileToInfo.Count);
					foreach (KeyValuePair<FileItem, DependencyInfo> Pair in DependencyFileToInfo)
					{
						Writer.WriteFileItem(Pair.Key);
						Pair.Value.Write(Writer);
					}
				}
			}
			bModified = false;
		}

		// Reads dependencies from the given file.
		static List<FileItem> ReadDependenciesFile(FileReference FileToRead)
		{
			if (FileToRead.HasExtension(".d"))
			{
				string Text = FileReference.ReadAllText(FileToRead);

				List<string> Tokens = new List<string>();

				StringBuilder Token = new StringBuilder();
				for (int Idx = 0; TryReadMakefileToken(Text, ref Idx, Token);)
				{
					Tokens.Add(Token.ToString());
				}

				int TokenIdx = 0;
				while (TokenIdx < Tokens.Count && Tokens[TokenIdx++] == "\n")
				{
				}

				if (Tokens.Count <= TokenIdx + 1 || Tokens[TokenIdx + 1] != ":")
				{
					throw new BuildException("Unable to parse dependency file");
				}

				TokenIdx += 2;

				List<FileItem> NewDependencyFiles = new List<FileItem>();
				for (; TokenIdx < Tokens.Count && Tokens[TokenIdx] != "\n"; ++TokenIdx)
				{
					NewDependencyFiles.Add(FileItem.GetItemByPath(Tokens[TokenIdx]));
				}

				while (TokenIdx < Tokens.Count && Tokens[TokenIdx++] == "\n")
				{
				}

				if (TokenIdx != Tokens.Count)
				{
					throw new BuildException("Unable to parse dependency file");
				}

				return NewDependencyFiles;
			}
			else if (FileToRead.HasExtension(Tag.Ext.Txt))
			{
				string[] Lines = FileReference.ReadAllLines(FileToRead);

				HashSet<FileItem> DependencyItems = new HashSet<FileItem>();
				foreach (string Line in Lines)
				{
					if (0 < Line.Length)
					{
						// Ignore *.tlh and *.tli files generated by the compiler from COM DLLs
						if (!Line.EndsWith(Tag.Ext.Tlh, StringComparison.OrdinalIgnoreCase) && 
							!Line.EndsWith(Tag.Ext.Tli, StringComparison.OrdinalIgnoreCase))
						{
							string FixedLine = Line.Replace("\\\\", "\\"); // ISPC outputs files with escaped slashes
							DependencyItems.Add(FileItem.GetItemByPath(FixedLine));
						}
					}
				}
				return DependencyItems.ToList();
			}
			else
			{
				throw new BuildException("Unknown dependency list file type: {0}", FileToRead);
			}
		}

		// Attempts to read a single token from a makefile
		static bool TryReadMakefileToken(string TextToRead, ref int RefIdx, StringBuilder Token)
		{
			Token.Clear();

			int Idx = RefIdx;
			for (;;)
			{
				if (Idx == TextToRead.Length)
				{
					return false;
				}

				// Skip whitespace
				while (TextToRead[Idx] == ' ' || 
					   TextToRead[Idx] == '\t')
				{
					if (++Idx == TextToRead.Length)
					{
						return false;
					}
				}

				// Colon token
				if (TextToRead[Idx] == ':')
				{
					Token.Append(':');
					RefIdx = Idx + 1;
					return true;
				}

				// Check for a newline
				if (TextToRead[Idx] == '\r' || 
					TextToRead[Idx] == '\n')
				{
					Token.Append('\n');
					RefIdx = Idx + 1;
					return true;
				}

				// Check for an escaped newline
				if (TextToRead[Idx] == '\\' && 
					Idx + 1 < TextToRead.Length)
				{
					if (TextToRead[Idx + 1] == '\n')
					{
						Idx += 2;
						continue;
					}
					if (TextToRead[Idx + 1] == '\r' && 
						Idx + 2 < TextToRead.Length && 
						TextToRead[Idx + 2] == '\n')
					{
						Idx += 3;
						continue;
					}
				}

				// Read a token. Special handling for drive letters on Windows!
				for (; Idx < TextToRead.Length; ++Idx)
				{
					if (TextToRead[Idx] == ' ' || 
						TextToRead[Idx] == '\t' || 
						TextToRead[Idx] == '\r' || 
						TextToRead[Idx] == '\n')
					{
						break;
					}
					if (TextToRead[Idx] == ':' && 1 < Token.Length)
					{
						break;
					}
					if (TextToRead[Idx] == '\\' && 
						Idx + 1 < TextToRead.Length && 
						TextToRead[Idx + 1] == ' ')
					{
						++Idx;
					}
					Token.Append(TextToRead[Idx]);
				}

				RefIdx = Idx;
				return true;
			}
		}
	}
}
