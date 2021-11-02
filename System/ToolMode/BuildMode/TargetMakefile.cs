using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BuildToolUtilities;

namespace BuildTool
{
	internal static class TargetMakeFileTag
	{

	}

	// Cached list of actions that need to be executed to build a target, along with the information needed to determine whether they are valid.
	internal sealed class TargetMakefile : IActionGraphBuilder
	{		
		public const int CurrentVersion = 19; // The version number to write
		public DateTime  CreateTimeUtc;       // The time at which the makefile was created
		public DateTime  ModifiedTimeUtc;     // The time at which the makefile was modified

		public TargetType      TargetType;
		public string[]        AdditionalArguments;              // The makefile will be invalidated whenever these change.
		public FileReference[] PreBuildScripts;                  // Scripts which should be run before building anything
		public List<string>    Diagnostics = new List<string>(); // Additional diagnostic output to print before building this target (toolchain version, etc...)
		public List<Action>    Actions;                          // Every action in the action graph
		public bool            bDeployAfterCompile; // Whether the target should be deployed after being built


		public HashSet<FileItem> ExternalDependencies = new HashSet<FileItem>(); // Set of external (ie. user-owned) files which will cause the makefile to be invalidated if modified after
		public HashSet<FileItem> InternalDependencies = new HashSet<FileItem>(); // Set of internal (eg. response files, unity files) which will cause the makefile to be invalidated if modified.
		public string            ExternalMetadata;                               // Any additional information about the build environment which the platform can use to invalidate the makefile

		public FileReference      ExecutableFile;               // The main executable output by this target
		public FileReference      ReceiptFile;                  // Path to the receipt file for this target
		public DirectoryReference ProjectIntermediateDirectory; // The project intermediate directory

		public ConfigValueTracker         ConfigValueTracker;                                // Map of config file keys to values. Makefile will be invalidated if any of these change.
		public Dictionary<string, string> ConfigSettings = new Dictionary<string, string>(); // List of config settings in generated config files
		
		// Environment variables that we'll need in order to invoke the platform's compiler and linker
		// @todo ubtmake: Really we want to allow a different set of environment variables for every Action.
		// This would allow for targets on multiple platforms to be built in a single assembling phase.
		// We'd only have unique variables for each platform that has actions, so we'd want to make sure we only store the minimum set.
		public readonly List<Tuple<string, string>> EnvironmentVariables = new List<Tuple<string, string>>();

		public List<FileItem>                        OutputItems;             // The final output items for all target
		public List<DirectoryItem>                   SourceDirectories;       // List of all source directories
		public Dictionary<string, FileItem[]>        ModuleNameToOutputItems; // Maps module names to output items
		public Dictionary<DirectoryItem, FileItem[]> DirectoryToSourceFiles;  // Any files being added or removed from these directories will invalidate the makefile.
		public HashSet<string>                       HotReloadModuleNames;    // List of game module names, for hot-reload
		public HashSet<FileItem>                     PluginFiles;             // The makefile will be considered invalid if any of these changes, or new plugins are added.

		public HashSet<FileItem> WorkingSet = new HashSet<FileItem>();              // The set of source files that BuildTool determined to be part of the programmer's "working set". // Used for adaptive non-unity builds.
		public HashSet<FileItem> CandidatesForWorkingSet = new HashSet<FileItem>(); // Set of files which are currently not part of the working set, but could be.

		public bool                      bHasProjectScriptPlugin; // UHT needs to know this to detect which manifest to use for checking out-of-datedness.
		public List<HeaderToolModuleInfo>       UObjectModules;          // Maps each target to a list of UObject module info structures
		public List<UHTModuleHeaderInfo> UObjectModuleHeaders     // Used to map names of modules to their .Build.cs filename
			= new List<UHTModuleHeaderInfo>(); 
		
		public TargetMakefile
		(
			string ExternalMetadata,
			FileReference ExecutableFile,
			FileReference ReceiptFile,
			DirectoryReference ProjectIntermediateDirectory,
			TargetType TargetType,
			ConfigValueTracker ConfigValueTracker,
			bool bDeployAfterCompile,
			bool bHasProjectScriptPlugin
		)
		{
			this.CreateTimeUtc = BuildTool.StartTimeUtc;
			this.ModifiedTimeUtc = CreateTimeUtc;
			this.Diagnostics = new List<string>();
			this.ExternalMetadata = ExternalMetadata;
			this.ExecutableFile = ExecutableFile;
			this.ReceiptFile = ReceiptFile;
			this.ProjectIntermediateDirectory = ProjectIntermediateDirectory;
			this.TargetType = TargetType;
			this.ConfigValueTracker = ConfigValueTracker;
			this.bDeployAfterCompile = bDeployAfterCompile;
			this.bHasProjectScriptPlugin = bHasProjectScriptPlugin;
			this.Actions = new List<Action>();
			this.OutputItems = new List<FileItem>();
			this.ModuleNameToOutputItems = new Dictionary<string, FileItem[]>(StringComparer.OrdinalIgnoreCase);
			this.HotReloadModuleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			this.SourceDirectories = new List<DirectoryItem>();
			this.DirectoryToSourceFiles = new Dictionary<DirectoryItem, FileItem[]>();
			this.WorkingSet = new HashSet<FileItem>();
			this.CandidatesForWorkingSet = new HashSet<FileItem>();
			this.UObjectModules = new List<HeaderToolModuleInfo>();
			this.UObjectModuleHeaders = new List<UHTModuleHeaderInfo>();
			this.PluginFiles = new HashSet<FileItem>();
			this.ExternalDependencies = new HashSet<FileItem>();
			this.InternalDependencies = new HashSet<FileItem>();
		}
		
		// #Keep Sync with Read between Write.

		private TargetMakefile(BinaryArchiveReader Reader, DateTime LastWriteTimeUtc)
		{
			CreateTimeUtc = new DateTime(Reader.ReadLong(), DateTimeKind.Utc);
			ModifiedTimeUtc = LastWriteTimeUtc;
			Diagnostics = Reader.ReadList(() => Reader.ReadString());
			ExternalMetadata = Reader.ReadString();
			ExecutableFile = Reader.ReadFileReference();
			ReceiptFile = Reader.ReadFileReference();
			ProjectIntermediateDirectory = Reader.ReadDirectoryReference();
			TargetType = (TargetType)Reader.ReadInt();
			ConfigValueTracker = new ConfigValueTracker(Reader);
			bDeployAfterCompile = Reader.ReadBool();
			bHasProjectScriptPlugin = Reader.ReadBool();
			AdditionalArguments = Reader.ReadArray(() => Reader.ReadString());
			PreBuildScripts = Reader.ReadArray(() => Reader.ReadFileReference());
			Actions = Reader.ReadList(() => new Action(Reader));
			EnvironmentVariables = Reader.ReadList(() => Tuple.Create(Reader.ReadString(), Reader.ReadString()));
			OutputItems = Reader.ReadList(() => Reader.ReadFileItem());
			ModuleNameToOutputItems = Reader.ReadDictionary(() => Reader.ReadString(), () => Reader.ReadArray(() => Reader.ReadFileItem()), StringComparer.OrdinalIgnoreCase);
			HotReloadModuleNames = Reader.ReadHashSet(() => Reader.ReadString(), StringComparer.OrdinalIgnoreCase);
			SourceDirectories = Reader.ReadList(() => Reader.ReadDirectoryItem());
			DirectoryToSourceFiles = Reader.ReadDictionary(() => Reader.ReadDirectoryItem(), () => Reader.ReadArray(() => Reader.ReadFileItem()));
			WorkingSet = Reader.ReadHashSet(() => Reader.ReadFileItem());
			CandidatesForWorkingSet = Reader.ReadHashSet(() => Reader.ReadFileItem());
			UObjectModules = Reader.ReadList(() => new HeaderToolModuleInfo(Reader));
			UObjectModuleHeaders = Reader.ReadList(() => new UHTModuleHeaderInfo(Reader));
			PluginFiles = Reader.ReadHashSet(() => Reader.ReadFileItem());
			ExternalDependencies = Reader.ReadHashSet(() => Reader.ReadFileItem());
			InternalDependencies = Reader.ReadHashSet(() => Reader.ReadFileItem());
		}

		// #Keep Sync with Read between Write.

		private void Write(BinaryArchiveWriter Writer)
		{
			Writer.WriteLong(CreateTimeUtc.Ticks);
			Writer.WriteList(Diagnostics, x => Writer.WriteString(x));
			Writer.WriteString(ExternalMetadata);
			Writer.WriteFileReference(ExecutableFile);
			Writer.WriteFileReference(ReceiptFile);
			Writer.WriteDirectoryReference(ProjectIntermediateDirectory);
			Writer.WriteInt((int)TargetType);
			ConfigValueTracker.Write(Writer);
			Writer.WriteBool(bDeployAfterCompile);
			Writer.WriteBool(bHasProjectScriptPlugin);
			Writer.WriteArray(AdditionalArguments, Item => Writer.WriteString(Item));
			Writer.WriteArray(PreBuildScripts, Item => Writer.WriteFileReference(Item));
			Writer.WriteList(Actions, Action => Action.Write(Writer));
			Writer.WriteList(EnvironmentVariables, x => { Writer.WriteString(x.Item1); Writer.WriteString(x.Item2); });
			Writer.WriteList(OutputItems, Item => Writer.WriteFileItem(Item));
			Writer.WriteDictionary(ModuleNameToOutputItems, k => Writer.WriteString(k), v => Writer.WriteArray(v, e => Writer.WriteFileItem(e)));
			Writer.WriteHashSet(HotReloadModuleNames, x => Writer.WriteString(x));
			Writer.WriteList(SourceDirectories, x => Writer.WriteDirectoryItem(x));
			Writer.WriteDictionary(DirectoryToSourceFiles, k => Writer.WriteDirectoryItem(k), v => Writer.WriteArray(v, e => Writer.WriteFileItem(e)));
			Writer.WriteHashSet(WorkingSet, x => Writer.WriteFileItem(x));
			Writer.WriteHashSet(CandidatesForWorkingSet, x => Writer.WriteFileItem(x));
			Writer.WriteList(UObjectModules, e => e.Write(Writer));
			Writer.WriteList(UObjectModuleHeaders, x => x.Write(Writer));
			Writer.WriteHashSet(PluginFiles, x => Writer.WriteFileItem(x));
			Writer.WriteHashSet(ExternalDependencies, x => Writer.WriteFileItem(x));
			Writer.WriteHashSet(InternalDependencies, x => Writer.WriteFileItem(x));
		}

		public void Save(FileReference PathToSaveMakeFileTo)
		{
			DirectoryReference.CreateDirectory(PathToSaveMakeFileTo.Directory);
			using(BinaryArchiveWriter Writer = new BinaryArchiveWriter(PathToSaveMakeFileTo))
			{
				Writer.WriteInt(CurrentVersion);
				Write(Writer);
			}
		}

		// Loads a makefile from disk, at BuildMode.
		public static TargetMakefile Load
		(
			FileReference        TargetMakefileToLoad,
			FileReference        PathToProjectFile, 
			BuildTargetPlatform PlatformForThisMakeFile, 
			string[]             Arguments, 
			out string           OutReasonNotLoaded
		)
        {
            FileInfo MakefileInfo;
            // Check the directory timestamp on the project files directory.
            // If the user has generated project files more recently than the makefile, then we need to consider the file to be out of date
            MakefileInfo = new FileInfo(TargetMakefileToLoad.FullName);
            if (!MakefileInfo.Exists)
            {
                // Makefile doesn't even exist, so we won't bother loading it
                OutReasonNotLoaded = "no existing makefile";
                return null;
            }

            // Check the build version
            FileInfo BuildVersionFileInfo = new FileInfo(BuildVersion.GetDefaultFileName().FullName);
            if (BuildVersionFileInfo.Exists && MakefileInfo.LastWriteTime.CompareTo(BuildVersionFileInfo.LastWriteTime) < 0)
            {
                Log.TraceLog("Existing makefile is older than Build.version, ignoring it");
                OutReasonNotLoaded = "Build.version is newer";
                return null;
            }

            // @todo ubtmake: This will only work if the directory timestamp actually changes with every single GPF.  Force delete existing files before creating new ones?  Eh... really we probably just want to delete + create a file in that folder
            //			   -> UPDATE: Seems to work OK right now though on Windows platform, maybe due to GUID changes
            // @todo ubtmake: Some platforms may not save any files into this folder.  We should delete + generate a "touch" file to force the directory timestamp to be updated (or just check the timestamp file itself.  We could put it ANYWHERE, actually)

            // Installed Build doesn't need to check engine projects for outdatedness
            if (!BuildTool.IsEngineInstalled())
            {
                if (DirectoryReference.Exists(ProjectFileGenerator.IntermediateProjectFilesPath))
                {
                    DateTime EngineProjectFilesLastUpdateTime = new FileInfo(ProjectFileGenerator.ProjectTimestampFile).LastWriteTime;
                    if (MakefileInfo.LastWriteTime.CompareTo(EngineProjectFilesLastUpdateTime) < 0)
                    {
                        // Engine project files are newer than makefile
                        Log.TraceLog("Existing makefile is older than generated engine project files, ignoring it");
                        OutReasonNotLoaded = "project files are newer";
                        return null;
                    }
                }
            }

            // Check the game project directory too
            if (PathToProjectFile != null)
            {
                string ProjectFilename = PathToProjectFile.FullName;
                FileInfo ProjectFileInfo = new FileInfo(ProjectFilename);
                if (!ProjectFileInfo.Exists || MakefileInfo.LastWriteTime.CompareTo(ProjectFileInfo.LastWriteTime) < 0)
                {
                    // .uproject file is newer than makefile
                    Log.TraceLog("Makefile is older than .uproject file, ignoring it");
                    OutReasonNotLoaded = ".uproject file is newer";
                    return null;
                }

                DirectoryReference MasterProjectRelativePath = PathToProjectFile.Directory;
                string GameIntermediateProjectFilesPath = Path.Combine(MasterProjectRelativePath.FullName, "Intermediate", "ProjectFiles");
                if (Directory.Exists(GameIntermediateProjectFilesPath))
                {
                    DateTime GameProjectFilesLastUpdateTime = new DirectoryInfo(GameIntermediateProjectFilesPath).LastWriteTime;
                    if (MakefileInfo.LastWriteTime.CompareTo(GameProjectFilesLastUpdateTime) < 0)
                    {
                        // Game project files are newer than makefile
                        Log.TraceLog("Makefile is older than generated game project files, ignoring it");
                        OutReasonNotLoaded = "game project files are newer";
                        return null;
                    }
                }
            }

            // Check to see if BuildTool.exe was compiled more recently than the makefile
            DateTime BuildToolTimestamp = new FileInfo(Assembly.GetExecutingAssembly().Location).LastWriteTime;
            if (MakefileInfo.LastWriteTime.CompareTo(BuildToolTimestamp) < 0)
            {
                // BuildTool.exe was compiled more recently than the makefile
                Log.TraceLog("Makefile is older than BuildTool.exe, ignoring it");
                OutReasonNotLoaded = "BuildTool.exe is newer";
                return null;
            }

            // Check to see if any BuildConfiguration files have changed since the last build
            List<XMLConfig.InputFile> InputFiles = XMLConfig.FindInputFiles();
            foreach (XMLConfig.InputFile InputFile in InputFiles)
            {
                FileInfo InputFileInfo = new FileInfo(InputFile.Location.FullName);
                if (MakefileInfo.LastWriteTime < InputFileInfo.LastWriteTime)
                {
                    Log.TraceLog("Makefile is older than BuildConfiguration.xml, ignoring it");
                    OutReasonNotLoaded = "BuildConfiguration.xml is newer";
                    return null;
                }
            }


            TargetMakefile Makefile;

            try
            {
                using (BinaryArchiveReader Reader = new BinaryArchiveReader(TargetMakefileToLoad))
                {
                    int Version = Reader.ReadInt();
                    if (Version != CurrentVersion)
                    {
                        OutReasonNotLoaded = "makefile version does not match";
                        return null;
                    }
                    Makefile = new TargetMakefile(Reader, MakefileInfo.LastWriteTimeUtc);
                }
            }
            catch (Exception Ex)
            {
                Log.TraceWarning("Failed to read makefile: {0}", Ex.Message);
                Log.TraceLog("Exception: {0}", Ex.ToString());
                OutReasonNotLoaded = "couldn't read existing makefile";
                return null;
            }

            // Check if the arguments are different
            if (!Enumerable.SequenceEqual(Makefile.AdditionalArguments, Arguments))
            {
                OutReasonNotLoaded = "command line arguments changed";
                return null;
            }

            // Check if any config settings have changed. Ini files contain build settings too.
            if (!Makefile.ConfigValueTracker.IsValid())
            {
                OutReasonNotLoaded = "config setting changed";
                return null;
            }

            // Get the current build metadata from the platform
            string CurrentExternalMetadata = BuildPlatform.GetBuildPlatform(PlatformForThisMakeFile).GetExternalBuildMetadata(PathToProjectFile);
            if (String.Compare(CurrentExternalMetadata, Makefile.ExternalMetadata, StringComparison.Ordinal) != 0)
            {
                Log.TraceLog("Old metadata:\n", Makefile.ExternalMetadata);
                Log.TraceLog("New metadata:\n", CurrentExternalMetadata);
                OutReasonNotLoaded = "build metadata has changed";
                return null;
            }

            // The makefile is ok
            OutReasonNotLoaded = null;
            return Makefile;
        }

		// Checks if the makefile is valid for the current set of source files.
		// This is done separately to the Load() method to allow pre-build steps to modify source files.
		// <returns>True if the makefile is valid, false otherwise</returns>
		public static bool IsValidForSourceFiles
		(
			TargetMakefile        LoadedTargetMakefile, 
			FileReference         ProjectFilePath, 
			BuildTargetPlatform  PlatformBeingBuilt, 
			ISourceFileWorkingSet CurrentSourceFileWorkingSet, 
			out string            ReasonNotLoaded
		)
		{

            // Get the list of excluded folder names for this platform
            ReadOnlyHashSet<string> ExcludedFolderNames = BuildPlatform.GetBuildPlatform(PlatformBeingBuilt).GetExcludedFolderNames();

            // Check if any source files have been added or removed
            foreach (KeyValuePair<DirectoryItem, FileItem[]> Pair in LoadedTargetMakefile.DirectoryToSourceFiles)
            {
                DirectoryItem InputDirectory = Pair.Key;
                if (!InputDirectory.Exists || LoadedTargetMakefile.CreateTimeUtc < InputDirectory.LastWriteTimeUtc)
                {
                    FileItem[] SourceFiles = BuildModuleCPP.GetSourceFiles(InputDirectory);
                    if (SourceFiles.Length < Pair.Value.Length)
                    {
                        ReasonNotLoaded = "source file removed";
                        return false;
                    }
                    else if (SourceFiles.Length > Pair.Value.Length)
                    {
                        ReasonNotLoaded = "source file added";
                        return false;
                    }
                    else if (SourceFiles.Intersect(Pair.Value).Count() != SourceFiles.Length)
                    {
                        ReasonNotLoaded = "source file modified";
                        return false;
                    }

                    foreach (DirectoryItem Directory in InputDirectory.EnumerateSubDirectories())
                    {
                        if (!LoadedTargetMakefile.DirectoryToSourceFiles.ContainsKey(Directory) && ContainsSourceFiles(Directory, ExcludedFolderNames))
                        {
                            ReasonNotLoaded = "directory added";
                            return false;
                        }
                    }
                }
            }

            // Check if any external dependencies have changed.
            // These comparisons are done against the makefile creation time.
            foreach (FileItem ExternalDependency in LoadedTargetMakefile.ExternalDependencies)
            {
                if (!ExternalDependency.Exists)
                {
                    Log.TraceLog("{0} has been deleted since makefile was built.", ExternalDependency.FileDirectory);
                    ReasonNotLoaded = string.Format("{0} deleted", ExternalDependency.FileDirectory.GetFileName());
                    return false;
                }
                if (LoadedTargetMakefile.CreateTimeUtc < ExternalDependency.LastWriteTimeUtc)
                {
                    Log.TraceLog("{0} has been modified since makefile was built.", ExternalDependency.FileDirectory);
                    ReasonNotLoaded = string.Format("{0} modified", ExternalDependency.FileDirectory.GetFileName());
                    return false;
                }
            }

            // Check if any internal dependencies has changed.
            // These comparisons are done against the makefile modified time.
            foreach (FileItem InternalDependency in LoadedTargetMakefile.InternalDependencies)
            {
                if (!InternalDependency.Exists)
                {
                    Log.TraceLog("{0} has been deleted since makefile was written.", InternalDependency.FileDirectory);
                    ReasonNotLoaded = string.Format("{0} deleted", InternalDependency.FileDirectory.GetFileName());
                    return false;
                }
                if (LoadedTargetMakefile.ModifiedTimeUtc < InternalDependency.LastWriteTimeUtc)
                {
                    Log.TraceLog("{0} has been modified since makefile was written.", InternalDependency.FileDirectory);
                    ReasonNotLoaded = string.Format("{0} modified", InternalDependency.FileDirectory.GetFileName());
                    return false;
                }
            }

            // Check that no new plugins have been added
            foreach (FileReference PluginFile in Plugins.EnumeratePlugins(ProjectFilePath))
            {
                FileItem PluginFileItem = FileItem.GetItemByFileReference(PluginFile);
                if (!LoadedTargetMakefile.PluginFiles.Contains(PluginFileItem))
                {
                    Log.TraceLog("{0} has been added", PluginFile.GetFileName());
                    ReasonNotLoaded = string.Format("{0} has been added", PluginFile.GetFileName());
                    return false;
                }
            }

            // Load the metadata cache
            SourceFileMetadataCache MetadataCache = SourceFileMetadataCache.CreateHierarchy(ProjectFilePath);

            // Find the set of files that contain reflection markup
            ConcurrentBag<FileItem> NewFilesWithMarkupBag = new ConcurrentBag<FileItem>();
            using (ThreadPoolWorkQueue Queue = new ThreadPoolWorkQueue())
            {
                foreach (DirectoryItem SourceDirectory in LoadedTargetMakefile.SourceDirectories)
                {
                    Queue.Enqueue(() => FindFilesWithMarkup(SourceDirectory, MetadataCache, ExcludedFolderNames, NewFilesWithMarkupBag, Queue));
                }
            }

            // Check whether the list has changed
            List<FileItem> PrevFilesWithMarkup = LoadedTargetMakefile.UObjectModuleHeaders.Where(x => !x.bUsePrecompiled).SelectMany(x => x.HeaderFiles).ToList();
            List<FileItem> NextFilesWithMarkup = NewFilesWithMarkupBag.ToList();

            if (NextFilesWithMarkup.Count != PrevFilesWithMarkup.Count || 
				NextFilesWithMarkup.Intersect(PrevFilesWithMarkup).Count() != PrevFilesWithMarkup.Count)
            {
                ReasonNotLoaded = "UHT files changed";
                return false;
            }

            // If adaptive unity build is enabled, do a check to see if there are any source files that became part of the
            // working set since the Makefile was created (or, source files were removed from the working set.)
            // If anything changed, then we'll force a new Makefile to be created so that we have fresh unity build blobs.
            // We always want to make sure that source files in the working set are excluded from those unity blobs
            // (for fastest possible iteration times.)

            // Check if any source files in the working set no longer belong in it
            foreach (FileItem SourceFile in LoadedTargetMakefile.WorkingSet)
            {
                if (!CurrentSourceFileWorkingSet.Contains(SourceFile) && 
					LoadedTargetMakefile.CreateTimeUtc < SourceFile.LastWriteTimeUtc)
                {
                    Log.TraceLog("{0} was part of source working set and now is not; invalidating makefile", SourceFile.AbsolutePath);
                    ReasonNotLoaded = string.Format("working set of source files changed");
                    return false;
                }
            }

            // Check if any source files that are eligible for being in the working set have been modified
            foreach (FileItem SourceFile in LoadedTargetMakefile.CandidatesForWorkingSet)
            {
                if (CurrentSourceFileWorkingSet.Contains(SourceFile) && 
					LoadedTargetMakefile.CreateTimeUtc < SourceFile.LastWriteTimeUtc)
                {
                    Log.TraceLog("{0} was part of source working set and now is not", SourceFile.AbsolutePath);
                    ReasonNotLoaded = string.Format("working set of source files changed");
                    return false;
                }
            }

            ReasonNotLoaded = null;
			return true;
		}

		// Determines if a directory, or any subdirectory of it, contains new source files
		private static bool ContainsSourceFiles(DirectoryItem Directory, ReadOnlyHashSet<string> ExcludedFolderNames)
		{
			// Check this directory isn't ignored
			if(!ExcludedFolderNames.Contains(Directory.Name))
			{
				// Check for any source files in this actual directory
				FileItem[] SourceFiles = BuildModuleCPP.GetSourceFiles(Directory);
				if(0 < SourceFiles.Length)
				{
					return true;
				}

				// Check for any source files in a subdirectory
				foreach(DirectoryItem SubDirectory in Directory.EnumerateSubDirectories())
				{
					if(ContainsSourceFiles(SubDirectory, ExcludedFolderNames))
					{
						return true;
					}
				}
			}

			return false;
		}

        // Finds all the source files under a directory that contain reflection markup
        private static void FindFilesWithMarkup
		(
			DirectoryItem           DirectoryToSearch,
			SourceFileMetadataCache InSourceFileMetadataCache, 
			ReadOnlyHashSet<string> ExcludedFolderNames, // Set of folder names to ignore when recursing the directory tree
			ConcurrentBag<FileItem> FilesWithMarkup,     // Receives the set of files which contain reflection markup
			ThreadPoolWorkQueue     Queue
		)
		{
			// Search through all the subfolders
			foreach(DirectoryItem SubDirectory in DirectoryToSearch.EnumerateSubDirectories())
			{
				if(!ExcludedFolderNames.Contains(SubDirectory.Name))
				{
					Queue.Enqueue(() => FindFilesWithMarkup(SubDirectory, InSourceFileMetadataCache, ExcludedFolderNames, FilesWithMarkup, Queue));
				}
			}

			// Check for all the headers in this folder
			foreach(FileItem File in DirectoryToSearch.EnumerateAllCachedFiles())
			{
				if(File.HasExtension(".h") && InSourceFileMetadataCache.ContainsReflectionMarkup(File))
				{
					FilesWithMarkup.Add(File);
				}
			}
		}

		// Gets the location of the makefile for particular target
		public static FileReference GetLocation
		(
			FileReference             ProjectFileForBuild, 
			string                    TargetNameBeingBuilt, 
			BuildTargetPlatform      TargetIsBeingBuiltForPlatform, 
			TargetConfiguration ConfigurationBeingBuilt
		)
		{
			DirectoryReference BaseDirectory = DirectoryReference.FromFile(ProjectFileForBuild) ?? BuildTool.EngineDirectory;

			return FileReference.Combine(BaseDirectory, Tag.Directory.Generated, Tag.Directory.Build, 
				TargetIsBeingBuiltForPlatform.ToString(), TargetNameBeingBuilt, ConfigurationBeingBuilt.ToString(), Tag.Binary.MakefileBin);
		}

		public Action CreateAction(ActionType Type)
		{
			Action Action = new Action(Type);
			Actions.Add(Action);
			return Action;
		}

		public FileItem CreateIntermediateTextFile(FileReference Location, string Contents)
		{
			// Write the file
			StringUtils.WriteFileIfChanged(Location, Contents, StringComparison.InvariantCultureIgnoreCase);

			// Reset the file info, in case it already knows about the old file
			FileItem Item = FileItem.GetItemByFileReference(Location);
			InternalDependencies.Add(Item);
			Item.ResetCachedInfo();
			return Item;
		}

		public void AddSourceDir(DirectoryItem SourceDir)
		{
			SourceDirectories.Add(SourceDir);
		}

		public void AddSourceFiles(DirectoryItem SourceDir, FileItem[] SourceFiles)
		{
			DirectoryToSourceFiles[SourceDir] = SourceFiles;
		}

		public void AddDiagnostic(string Message)
		{
			if(!Diagnostics.Contains(Message))
			{
				Diagnostics.Add(Message);
			}
		}

		public void AddFileToWorkingSet(FileItem File)
		{
			WorkingSet.Add(File);
		}

		public void AddCandidateForWorkingSet(FileItem File)
		{
			CandidatesForWorkingSet.Add(File);
		}

		public void SetOutputItemsForModule(string ModuleName, FileItem[] OutputItems)
		{
			ModuleNameToOutputItems[ModuleName] = OutputItems;
		}
	}
}
