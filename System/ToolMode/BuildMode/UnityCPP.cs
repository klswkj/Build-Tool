using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Linq;
using BuildToolUtilities;

namespace BuildTool
{
	internal sealed class UnityCPP
	{
		// Prefix used for all dynamically created Unity modules
		public const string ModulePrefix = "Module.";

		// A class which represents a list of files and the sum of their lengths.
		public sealed class FileCollection
		{
			public List<FileItem> Files {  get; private set; }

			public List<FileItem> VirtualFiles { get; private set; }

			public long TotalLength { get; private set; }

			// The length of this file collection, plus any additional virtual space needed for TargetRules::bUseAdapativeUnityBuild.
			// See the comment above AddVirtualFile() below for more information.
			public long VirtualLength { get; private set; }

			public FileCollection()
			{
				Files         = new List<FileItem>();
				VirtualFiles  = new List<FileItem>();
				TotalLength   = 0;
				VirtualLength = 0;
			}

			public void AddFile(FileItem File)
			{
				Files.Add(File);
				TotalLength   += File.Length;
				VirtualLength += File.Length;
			}

			// Doesn't actually add a file, but instead reserves space.
			// This is used with TargetRules::bUseAdaptiveUnityBuild, to prevent other compiled unity blobs in
			// the module's numbered set from having to be recompiled after we eject source files one of that module's unity blobs.
			// Basically, it can prevent dozens of files from being recompiled after the first time building after your working set of source files changes
			public void AddVirtualFile(FileItem File)
			{
				VirtualFiles.Add(File);
				VirtualLength += File.Length;
			}
		}

		// A class for building up a set of unity files.
		// You add files one-by-one using AddFile then
		// call EndCurrentUnityFile to finish that one and (perhaps) begin a new one.
		public sealed class UnityFileBuilder
		{
			private List<FileCollection> UnityFiles;
			private FileCollection       CurrentUnityFile;
			private readonly int         SplitLength;
			private readonly bool        bSplitUnityFiles;

			public UnityFileBuilder(int InSplitLength)
			{
				UnityFiles       = new List<FileCollection>();
				CurrentUnityFile = new FileCollection();

				bSplitUnityFiles = 0 < InSplitLength;
				SplitLength      = InSplitLength; 
			}

			// Adds a file to the current unity file.
			public void AddFile(FileItem File)
			{
				CurrentUnityFile.AddFile(File);
				if (bSplitUnityFiles && 
					SplitLength < CurrentUnityFile.VirtualLength)
				{
					EndCurrentUnityFile();
				}
			}

			// Doesn't actually add a file, but instead reserves space,
			// then splits the unity blob normally as if it was a real file that was added.
			// See the comment above FileCollection.AddVirtualFile() for more info.
			public void AddVirtualFile(FileItem File)
			{
				CurrentUnityFile.AddVirtualFile(File);
				if (bSplitUnityFiles && 
					SplitLength < CurrentUnityFile.VirtualLength)
				{
					EndCurrentUnityFile();
				}
			}

			// Starts a new unity file.
			// If the current unity file contains no files, this function has no effect,
			// i.e. you will not get an empty unity file.
			private void EndCurrentUnityFile()
			{
				if (CurrentUnityFile.Files.Count == 0)
				{
					return;
				}

				UnityFiles.Add(CurrentUnityFile);
				CurrentUnityFile = new FileCollection();
			}

			// Returns the list of built unity files.
			// The UnityFileBuilder is unusable after this.
			public List<FileCollection> GetUnityFiles()
			{
				EndCurrentUnityFile();

				List<FileCollection> Result = UnityFiles;

				// Null everything to ensure that failure will occur if you accidentally reuse this object.
				CurrentUnityFile = null;
				UnityFiles       = null;

				return Result;
			}
		}

		// Given a set of C++ files, generates another set of C++ files that
		// #include all the original files, the goal being to compile the same code in **fewer** translation units.
		// The "unity" files are written to the CompileEnvironment's OutputDirectory.
		public static List<FileItem> GenerateUnityCPPs
		(
			ReadOnlyTargetRules            BuildingTargetRules,
			List<FileItem>                 CPPFilesToIncludes, // ex) 모듈 구성 *.cpp, #define만 구성되있는 파일 말고.
			ISourceFileWorkingSet          WorkingSet,
			string                         ModuleRulesFileName, // Target
			DirectoryReference             GeneratedDirectoryForUnityCpps, // ex) {D:\UERelease\Engine\Generated\Build\Win64\HeaderTool\Development\HeaderTool}
			IActionGraphBuilder            MakeFileGraphBuilder,
			Dictionary<FileItem, FileItem> ReceiveSourceFileToUnityFile
		)
		{
			List<FileItem> NewCPPFiles = new List<FileItem>();

			// Figure out size of all input files combined. We use this to determine whether to use larger unity threshold or not.
			long TotalBytesInCPPFiles = CPPFilesToIncludes.Sum(F => F.Length);

			// We have an increased threshold for unity file size if, and only if, all files fit into the same unity file.
			// This is beneficial when dealing with PCH files.
			// The default PCH creation limit is X unity files so if we generate < X this could be fairly slow and
			// we'd rather bump the limit a bit to group them all into the same unity file.

			// When enabled, buildTool will try to determine source files that you are actively iteratively changing,
			// and break those files out of their unity blobs so that you can compile them as individual translation units,
			// much faster than recompiling the entire unity blob each time.
			bool bUseAdaptiveUnityBuild = BuildingTargetRules.bUseAdaptiveUnityBuild && !BuildingTargetRules.bStressTestUnity;

			// Build the list of unity files.
			List<FileCollection> AllUnityFiles;

			{
				// Sort the incoming file paths alphabetically, so there will be consistency in unity blobs across multiple machines.
				// Note that we're relying on this not only sorting files within each directory,
				// but also the directories themselves, so the whole list of file paths is the same across computers.
				List<FileItem> SortedCPPFiles = CPPFilesToIncludes.GetRange(0, CPPFilesToIncludes.Count);
				{
					// Case-insensitive file path compare, because you never know what is going on with local file systems
					int FileItemComparer(FileItem FileA, FileItem FileB) => FileA.AbsolutePath.ToLowerInvariant().CompareTo(FileB.AbsolutePath.ToLowerInvariant());
					SortedCPPFiles.Sort(FileItemComparer);
				}

				// Figure out whether we REALLY want to use adaptive unity for this module.
				// If nearly every file in the module appears in the working set, we'll just go ahead and let unity build do its thing.
				HashSet<FileItem> FilesInWorkingSet = new HashSet<FileItem>();

				if (bUseAdaptiveUnityBuild)
				{
					int CandidateWorkingSetSourceFileCount = 0;
					int WorkingSetSourceFileCount          = 0;

					foreach (FileItem CPPFile in SortedCPPFiles)
					{
						++CandidateWorkingSetSourceFileCount;

						// Don't include writable source files into unity blobs
						if (WorkingSet.Contains(CPPFile))
						{
							++WorkingSetSourceFileCount;

							// Mark this file as part of the working set.
							// This will be saved into the buildtool Makefile.
							// So that the assembler can automatically invalidate the Makefile when the working set changes.
							// (allowing this code to run again, to build up new unity blobs.)
							FilesInWorkingSet.Add(CPPFile);
							MakeFileGraphBuilder.AddFileToWorkingSet(CPPFile);
						}
					}

					if (CandidateWorkingSetSourceFileCount <= WorkingSetSourceFileCount)
					{
						// Every single file in the module appears in the working set,
						// so don't bother using adaptive unity for this module.
						// Otherwise it would make full builds really slow.
						bUseAdaptiveUnityBuild = false;
					}
				}

				// Optimization only makes sense if PCH files are enabled.
				bool bForceIntoSingleUnityFile = BuildingTargetRules.bStressTestUnity ||
					(TotalBytesInCPPFiles < 2 * BuildingTargetRules.NumIncludedBytesPerUnityCPP && BuildingTargetRules.bUsePCHFiles);

				UnityFileBuilder CPPUnityFileBuilder          = new UnityFileBuilder(bForceIntoSingleUnityFile ? -1 : BuildingTargetRules.NumIncludedBytesPerUnityCPP);
				StringBuilder    AdaptiveUnityBuildInfoString = new StringBuilder();

				foreach (FileItem CPPFile in SortedCPPFiles)
				{
					if (!bForceIntoSingleUnityFile && CPPFile.AbsolutePath.IndexOf(Tag.Ext.GeneratedWrapper, StringComparison.InvariantCultureIgnoreCase) != -1)
					{
						NewCPPFiles.Add(CPPFile);
					}

					// When adaptive unity is enabled, go ahead and exclude any source files that we're actively working with
					// CPPUnityFileBuilder.AddVirtualFile(CPPFile);
					// else
					// CPPUnityFileBuilder.AddFile(CPPFile);
					if (bUseAdaptiveUnityBuild && FilesInWorkingSet.Contains(CPPFile))
					{
						// Just compile this file normally, not as part of the unity blob
						NewCPPFiles.Add(CPPFile);

						// Let the unity file builder know about the file, so that we can retain the existing size of the unity blobs.
						// This won't actually make the source file part of the unity blob,
						// but it will keep track of how big the file is so that other existing unity blobs from the same module won't be invalidated.
						// This prevents much longer compile times the first time you build after your working file set changes.
						CPPUnityFileBuilder.AddVirtualFile(CPPFile);

						string CPPFileName = Path.GetFileName(CPPFile.AbsolutePath);

						if (AdaptiveUnityBuildInfoString.Length == 0)
						{
							AdaptiveUnityBuildInfoString.Append(String.Format("[Adaptive unity build] Excluded from {0} unity file: {1}", ModuleRulesFileName, CPPFileName));
						}
						else
						{
							AdaptiveUnityBuildInfoString.Append(", " + CPPFileName);
						}
					}
					else
					{
						// If adaptive unity build is enabled for this module,
						// add this source file to the set that will invalidate the makefile
						if(bUseAdaptiveUnityBuild)
						{
							MakeFileGraphBuilder.AddCandidateForWorkingSet(CPPFile);
						}

						// Compile this file as part of the unity blob
						CPPUnityFileBuilder.AddFile(CPPFile);
					}
				}

				if (0 < AdaptiveUnityBuildInfoString.Length)
				{
					if (BuildingTargetRules.bAdaptiveUnityCreatesDedicatedPCH)
					{
						MakeFileGraphBuilder.AddDiagnostic("[Adaptive unity build] Creating dedicated PCH for each excluded file. Set bAdaptiveUnityCreatesDedicatedPCH to false in BuildConfiguration.xml to change this behavior.");
					}
					else if (BuildingTargetRules.bAdaptiveUnityDisablesPCH)
					{
						MakeFileGraphBuilder.AddDiagnostic("[Adaptive unity build] Disabling PCH for excluded files. Set bAdaptiveUnityDisablesPCH to false in BuildConfiguration.xml to change this behavior.");
					}

					if (BuildingTargetRules.bAdaptiveUnityDisablesOptimizations)
					{
						MakeFileGraphBuilder.AddDiagnostic("[Adaptive unity build] Disabling optimizations for excluded files. Set bAdaptiveUnityDisablesOptimizations to false in BuildConfiguration.xml to change this behavior.");
					}
					if (BuildingTargetRules.bAdaptiveUnityEnablesEditAndContinue)
					{
						MakeFileGraphBuilder.AddDiagnostic("[Adaptive unity build] Enabling Edit & Continue for excluded files. Set bAdaptiveUnityEnablesEditAndContinue to false in BuildConfiguration.xml to change this behavior.");
					}

					MakeFileGraphBuilder.AddDiagnostic(AdaptiveUnityBuildInfoString.ToString());
				}

				AllUnityFiles = CPPUnityFileBuilder.GetUnityFiles();
			}

			// Create a set of CPP files that combine smaller CPP files into larger compilation units,
			// along with the corresponding actions to compile them.
			int CurrentUnityFileCount = 0;

			foreach (FileCollection UnityFile in AllUnityFiles)
			{
				++CurrentUnityFileCount;

				StringWriter OutputUnityCPPWriter = new StringWriter();

				OutputUnityCPPWriter.WriteLine("// This file is automatically generated at compile-time to include some subset of the user-created cpp files.");

				// Add source files to the unity file
				foreach (FileItem CPPFile in UnityFile.Files)
				{
					// OutputUnityCPPWriter.WriteLine("#include \"{0}\"", CPPFile.AbsolutePath.Replace('\\', '/'));
					OutputUnityCPPWriter.WriteLine(Tag.CppContents.Include + "\"" + CPPFile.AbsolutePath.Replace('\\', '/') + "\"");
				}

				// Determine unity file path name
				string UnityCPPFileName;
				if (1 < AllUnityFiles.Count)
				{
					UnityCPPFileName = string.Format("{0}{1}.{2}_of_{3}.cpp", ModulePrefix, ModuleRulesFileName, CurrentUnityFileCount, AllUnityFiles.Count);
				}
				else
				{
					UnityCPPFileName = string.Format("{0}{1}.cpp", ModulePrefix, ModuleRulesFileName);
				}
				FileReference UnityCPPFilePath = FileReference.Combine(GeneratedDirectoryForUnityCpps, UnityCPPFileName);

				// Write the unity file to the intermediate folder.
				FileItem UnityCPPFile = MakeFileGraphBuilder.CreateIntermediateTextFile(UnityCPPFilePath, OutputUnityCPPWriter.ToString());
				NewCPPFiles.Add(UnityCPPFile);

				// Store the mapping of source files to unity files in the makefile
				foreach(FileItem SourceFile in UnityFile.Files)
				{
					ReceiveSourceFileToUnityFile[SourceFile] = UnityCPPFile;
				}
				foreach (FileItem SourceFile in UnityFile.VirtualFiles)
				{
					ReceiveSourceFileToUnityFile[SourceFile] = UnityCPPFile;
				}
			}

			return NewCPPFiles;
		}

		public static void AddUniqueDiagnostic(TargetMakefile Makefile, string Message)
		{
			if(!Makefile.Diagnostics.Contains(Message, StringComparer.Ordinal))
			{
				Makefile.Diagnostics.Add(Message);
			}
		}
	}
}
