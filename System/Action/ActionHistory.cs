using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using BuildToolUtilities;

namespace BuildTool
{
    // Caches include dependency information to speed up preprocessing on subsequent runs.
    internal sealed class ActionHistory
	{
		const int CurrentVersion = 2; // Version number to check
		const int HashLength = 16; // Size of each hash value
		private readonly FileReference CacheDataLocation; // Path to store the cache data to.
		private readonly DirectoryReference BaseDirectory;  // Any files under this directory will have their command lines stored in this object, otherwise the parent will be used.
		private readonly ActionHistory ParentActionHistory; // Any files not under this base directory will use this.

		// The command lines used to produce files, keyed by the absolute file paths.
		Dictionary<FileItem, byte[]> OutputItemToCommandLineHash = new Dictionary<FileItem, byte[]>();

        private bool bModified; // Whether the dependency cache is dirty and needs to be saved.
		
		private readonly object LockObject = new object(); // Object to use for guarding access to the OutputItemToCommandLine dictionary


		// Static cache of all loaded action history files
		private readonly static Dictionary<FileReference, ActionHistory> LoadedFiles = new Dictionary<FileReference, ActionHistory>();

		public ActionHistory(FileReference InCacheDataLocation, DirectoryReference InBaseDirectory, ActionHistory ParentActionHistory)
		{
			this.CacheDataLocation = InCacheDataLocation;
			this.BaseDirectory = InBaseDirectory;
			this.ParentActionHistory = ParentActionHistory;

			if(FileReference.Exists(InCacheDataLocation))
			{
				Load();
			}
		}

		// Attempts to load this action history from disk
		void Load()
		{
			try
			{
				using(BinaryArchiveReader Reader = new BinaryArchiveReader(CacheDataLocation))
				{
					int Version = Reader.ReadInt();
					if(Version != CurrentVersion)
					{
						Log.TraceLog("Unable to read action history from {0}; version {1} vs current {2}", CacheDataLocation, Version, CurrentVersion);
						return;
					}

					OutputItemToCommandLineHash = Reader.ReadDictionary(() => Reader.ReadFileItem(), () => Reader.ReadFixedSizeByteArray(HashLength));
				}
			}
			catch(Exception Ex)
			{
				Log.TraceWarning("Unable to read {0}. See log for additional information.", CacheDataLocation);
				Log.TraceLog("{0}", ExceptionUtils.FormatExceptionDetails(Ex));
			}
		}

		// Saves this action history to disk
		void Save()
		{
			DirectoryReference.CreateDirectory(CacheDataLocation.Directory);
			using(BinaryArchiveWriter Writer = new BinaryArchiveWriter(CacheDataLocation))
			{
				Writer.WriteInt(CurrentVersion);
				Writer.WriteDictionary(OutputItemToCommandLineHash, Key => Writer.WriteFileItem(Key), Value => Writer.WriteFixedSizeByteArray(Value));
			}
			bModified = false;
		}

		// Computes the case-invariant hash for a string
		static byte[] ComputeHash(string TextToMakeHash)
		{
			string InvariantText = TextToMakeHash.ToUpperInvariant();
			byte[] InvariantBytes = Encoding.Unicode.GetBytes(InvariantText);
			return new MD5CryptoServiceProvider().ComputeHash(InvariantBytes);
		}

		// Compares two hashes for equality
		static bool CompareHashes(byte[] A, byte[] B)
		{
			for(int Idx = 0; Idx < HashLength; ++Idx)
			{
				if(A[Idx] != B[Idx])
				{
					return false;
				}
			}
			return true;
		}

		// Gets the producing command line for the given file
		public bool UpdateProducingCommandLine(FileItem FileToCheck, string CommandLine)
		{
			if(FileToCheck.FileDirectory.IsUnderDirectory(BaseDirectory) || 
				ParentActionHistory == null)
			{
				byte[] NewHash = ComputeHash(CommandLine);
				lock (LockObject)
				{
                    if (!OutputItemToCommandLineHash.TryGetValue(FileToCheck, out byte[] CurrentHash) || 
						!CompareHashes(CurrentHash, NewHash))
                    {
                        OutputItemToCommandLineHash[FileToCheck] = NewHash;
                        bModified = true;
                        return true;
                    }
                    return false;
				}
			}
			else
			{
				return ParentActionHistory.UpdateProducingCommandLine(FileToCheck, CommandLine);
			}
		}

		// Gets the location for the engine action history
		private static FileReference GetEngineActionHistoryLocation
		(
            string TargetName,
            BuildTargetPlatform TargetPlatform,
            TargetType TargetType,
            string TargetArchitecture
		)
		{
			string AppName;
			if(TargetType == TargetType.Program)
			{
				AppName = TargetName;
			}
			else
			{
				AppName = BuildTarget.GetAppNameForTargetType(TargetType);
			}

			return FileReference.Combine
			(
                BuildTool.EngineDirectory,
                BuildTool.GetPlatformGeneratedFolder(TargetPlatform, TargetArchitecture),
                AppName,
                "ActionHistory.bin"
			);
		}

		// Gets the location of the project action history
		private static FileReference GetProjectLocation
		(
            FileReference ProjectFile,
            string TargetName,
            BuildTargetPlatform TargetPlatform,
            string TaretArchitecture
		)
		{
			return FileReference.Combine
			(
                ProjectFile.Directory,
                BuildTool.GetPlatformGeneratedFolder(TargetPlatform, TaretArchitecture),
                TargetName,
                "ActionHistory.dat"
			);
		}

		// Creates a hierarchy of action history stores for a particular target
		public static ActionHistory CreateHierarchy(FileReference ProjectFile, string TargetName, BuildTargetPlatform TargetPlatform, TargetType TargetType, string TargetArchitecture)
		{
			ActionHistory History = null;

			if(ProjectFile == null || !BuildTool.IsEngineInstalled())
			{
				FileReference EngineCacheLocation = GetEngineActionHistoryLocation(TargetName, TargetPlatform, TargetType, TargetArchitecture);
				History = FindOrAddHistory(EngineCacheLocation, BuildTool.EngineDirectory, History);
			}

			if(ProjectFile != null)
			{
				FileReference ProjectCacheLocation = GetProjectLocation(ProjectFile, TargetName, TargetPlatform, TargetArchitecture);
				History = FindOrAddHistory(ProjectCacheLocation, ProjectFile.Directory, History);
			}

			return History;
		}

		// Enumerates all the locations of action history files for the given target
		public static IEnumerable<FileReference> GetFilesToClean(FileReference ProjectFile, string TargetName, BuildTargetPlatform TargetPlatform, TargetType TargetType, string TargetArchitecture)
		{
			if(ProjectFile == null || !BuildTool.IsEngineInstalled())
			{
				yield return GetEngineActionHistoryLocation(TargetName, TargetPlatform, TargetType, TargetArchitecture);
			}
			if(ProjectFile != null)
			{
				yield return GetProjectLocation(ProjectFile, TargetName, TargetPlatform, TargetArchitecture);
			}
		}

        // Reads a cache from the given location, or creates it with the given settings
        private static ActionHistory FindOrAddHistory(FileReference Location, DirectoryReference BaseDirectory, ActionHistory Parent)
		{
			lock(LoadedFiles)
			{
                if (LoadedFiles.TryGetValue(Location, out ActionHistory History))
                {
                    Debug.Assert(History.BaseDirectory == BaseDirectory);
                    Debug.Assert(History.ParentActionHistory == Parent);
                }
                else
                {
                    History = new ActionHistory(Location, BaseDirectory, Parent);
                    LoadedFiles.Add(Location, History);
                }
                return History;
			}
		}

		// Save all the loaded action histories
		public static void SaveAll()
		{
			lock(LoadedFiles)
			{
				foreach(ActionHistory History in LoadedFiles.Values)
				{
					if(History.bModified)
					{
						History.Save();
					}
				}
			}
		}
	}
}
