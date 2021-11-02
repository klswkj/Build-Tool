using System;
using System.Collections.Generic;
using System.IO;
using BuildToolUtilities;

namespace BuildTool
{
	// Parameters for the WriteMetadata mode
	[Serializable]
	internal sealed class WriteMetadataTargetInfo
	{
		public FileReference ProjectFile;
		public FileReference VersionFile;
		public FileReference ReceiptFile;
		public TargetReceipt ReceiptData; // The partially constructed receipt data

		// Map of module manifest filenames to their location on disk.
		public Dictionary<FileReference, ModuleManifest> FileToManifest = new Dictionary<FileReference, ModuleManifest>();

		public WriteMetadataTargetInfo(FileReference ProjectFile, FileReference VersionFile, FileReference ReceiptFile, TargetReceipt Receipt, Dictionary<FileReference, ModuleManifest> FileToManifest)//string EngineManifestName, string ProjectManifestName, Dictionary<string, FileReference> ModuleNameToLocation)
		{
			this.ProjectFile = ProjectFile;
			this.VersionFile = VersionFile;
			this.ReceiptFile = ReceiptFile;
			this.ReceiptData = Receipt;
			this.FileToManifest = FileToManifest;
		}
	}

	// Writes all metadata files at the end of a build (receipts, version files, etc...).
	// This is implemented as a separate mode to allow it to be done as part of the action graph.
	[ToolMode("WriteMetadata", ToolModeOptions.None)]
	internal sealed class WriteMetadataMode : ToolMode
	{
		// Version number for output files. This is not used directly,
		// but can be appended to command-line invocations of the tool to ensure that actions to generate metadata are updated if the output format changes. 
		// The action graph is regenerated whenever UBT is rebuilt, so this should always match.
		public const int CurrentVersionNumber = 1;

		// Execute the command
		public override int Execute(CommandLineArguments Arguments)
		{
			// Acquire a different mutex to the regular UBT instance, since this mode will be called as part of a build.
			// We need the mutex to ensure that building two modular configurations 
			// in parallel don't clash over writing shared *.modules files (eg. DebugGame and Development editors).
			string MutexName = SystemWideSingletonMutex.GetUniqueMutexForPath("BuildTool_WriteMetadata", BuildTool.RootDirectory.FullName);
			using(new SystemWideSingletonMutex(MutexName, true))
			{
				return ExecuteInternal(Arguments);
			}
		}

		// Execute the command, having obtained the appropriate mutex
		private static int ExecuteInternal(CommandLineArguments Arguments)
		{
			// Read the target info
			WriteMetadataTargetInfo TargetInfo = BinaryFormatterUtils.Load<WriteMetadataTargetInfo>(Arguments.GetFileReference(Tag.GlobalArgument.Input));
			int VersionNumber = Arguments.GetInteger(Tag.GlobalArgument.Version);
			bool bNoManifestChanges = Arguments.HasOption(Tag.GlobalArgument.NoManifestChanges);
			Arguments.CheckAllArgumentsUsed();

			// Make sure the version number is correct
			if(VersionNumber != CurrentVersionNumber)
			{
				System.Diagnostics.Debugger.Break();
				throw new BuildException("Version number to WriteMetadataMode is incorrect (expected {0}, got {1})", CurrentVersionNumber, VersionNumber);
			}

			// Check if we need to set a build id
			TargetReceipt Receipt = TargetInfo.ReceiptData;
			if(Receipt.Version.BuildId == null)
			{
				// Check if there's an exist version file. If it exists, try to merge in any manifests that are valid (and reuse the existing build id)
				if (TargetInfo.VersionFile != null && 
					BuildVersion.TryRead(TargetInfo.VersionFile, out BuildVersion PreviousVersion))
				{
					// Check if we can reuse the existing manifests. This prevents unnecessary builds when switching between projects.
					Dictionary<FileReference, ModuleManifest> PreviousFileToManifest = new Dictionary<FileReference, ModuleManifest>();
					if (TryRecyclingManifests(PreviousVersion.BuildId, TargetInfo.FileToManifest.Keys, PreviousFileToManifest))
					{
						// Merge files from the existing manifests with the new ones
						foreach (KeyValuePair<FileReference, ModuleManifest> Pair in PreviousFileToManifest)
						{
							ModuleManifest TargetManifest = TargetInfo.FileToManifest[Pair.Key];
							MergeManifests(Pair.Value, TargetManifest);
						}

						// Update the build id to use the current one
						Receipt.Version.BuildId = PreviousVersion.BuildId;
					}
				}

				// If the build id is still not set, generate a new one from a GUID
				if (Receipt.Version.BuildId == null)
				{
					Receipt.Version.BuildId = Guid.NewGuid().ToString();
				}
			}
			else
			{
				// Read all the manifests and merge them into the new ones, if they have the same build id
				foreach(KeyValuePair<FileReference, ModuleManifest> Pair in TargetInfo.FileToManifest)
				{
					if (TryReadManifest(Pair.Key, out ModuleManifest SourceManifest) && 
						SourceManifest.BuildId == Receipt.Version.BuildId)
					{
						MergeManifests(SourceManifest, Pair.Value);
					}
				}
			}

			// Update the build id in all the manifests, and write them out
			foreach (KeyValuePair<FileReference, ModuleManifest> Pair in TargetInfo.FileToManifest)
			{
				FileReference ManifestFile = Pair.Key;
				if(!BuildTool.IsFileInstalled(ManifestFile))
				{
					ModuleManifest Manifest = Pair.Value;
					Manifest.BuildId = Receipt.Version.BuildId;

					if(!FileReference.Exists(ManifestFile))
					{
						// If the file doesn't already exist, just write it out
						DirectoryReference.CreateDirectory(ManifestFile.Directory);
						Manifest.Write(ManifestFile);
					}
					else
					{
						// Otherwise write it to a buffer first
						string OutputText;
						using (StringWriter Writer = new StringWriter())
						{
							Manifest.Write(Writer);
							OutputText = Writer.ToString();
						}

						// And only write it to disk if it's been modified. Note that if a manifest is out of date, we should have generated a new build id causing the contents to differ.
						string CurrentText = FileReference.ReadAllText(ManifestFile);
						if(CurrentText != OutputText)
						{
							if(bNoManifestChanges)
							{
								Log.TraceError("Build modifies {0}. This is not permitted. Before:\n    {1}\nAfter:\n    {2}", ManifestFile, CurrentText.Replace("\n", "\n    "), OutputText.Replace("\n", "\n    "));
							}
							else
							{
								FileReference.WriteAllText(ManifestFile, OutputText);
							}
						}
					}
				}
			}

			// Write out the version file, if it's changed. Since this file is next to the executable, it may be used by multiple targets, and we should avoid modifying it unless necessary.
			if(TargetInfo.VersionFile != null 
				&& !BuildTool.IsFileInstalled(TargetInfo.VersionFile))
			{
				DirectoryReference.CreateDirectory(TargetInfo.VersionFile.Directory);

				StringWriter Writer = new StringWriter();
				Receipt.Version.Write(Writer);

				string Text = Writer.ToString();
				if(!FileReference.Exists(TargetInfo.VersionFile) 
					|| File.ReadAllText(TargetInfo.VersionFile.FullName) != Text)
				{
					File.WriteAllText(TargetInfo.VersionFile.FullName, Text);
				}
			}

			// Write out the receipt
			if(!BuildTool.IsFileInstalled(TargetInfo.ReceiptFile))
			{
				DirectoryReference.CreateDirectory(TargetInfo.ReceiptFile.Directory);
				Receipt.Write(TargetInfo.ReceiptFile);
			}

			return 0;
		}

		// Attempts to read a manifest from the given location
		private static bool TryReadManifest(FileReference ManifestFileName, out ModuleManifest OutManifest)
		{
			if (FileReference.Exists(ManifestFileName))
			{
				try
				{
					OutManifest = ModuleManifest.Read(ManifestFileName);
					return true;
				}
				catch (Exception Ex)
				{
					Log.TraceWarning("Unable to read '{0}'; ignoring.", ManifestFileName);
					Log.TraceLog(ExceptionUtils.FormatExceptionDetails(Ex));
				}
			}

			OutManifest = null;
			return false;
		}

		// Checks whether existing manifests on disk can be merged with new manifests being created,
		// by testing whether any build products they reference have a newer timestamp
		private static bool TryRecyclingManifests
		(
			string                                    BuildIDFromVersionFile, // Only manifests matching this ID will be considered
			IEnumerable<FileReference>                ManifestFiles,
			Dictionary<FileReference, ModuleManifest> RecycleFileToManifest
		)
		{
			bool bCanRecycleManifests = true;
			foreach(FileReference ManifestFileName in ManifestFiles)
			{
				if (ManifestFileName.IsUnderDirectory(BuildTool.EngineDirectory) 
					&& TryReadManifest(ManifestFileName, out ModuleManifest Manifest))
				{
					if (Manifest.BuildId == BuildIDFromVersionFile)
					{
						if (IsOutOfDate(ManifestFileName, Manifest))
						{
							bCanRecycleManifests = false;
							break;
						}
						RecycleFileToManifest.Add(ManifestFileName, Manifest);
					}
				}
			}

			return bCanRecycleManifests;
		}

		// Merge a manifest into another manifest
		private static void MergeManifests(ModuleManifest SourceManifest, ModuleManifest TargetManifestToMergeInto)
		{
			foreach(KeyValuePair<string, string> ModulePair in SourceManifest.ModuleNameToFileName)
			{
				if(!TargetManifestToMergeInto.ModuleNameToFileName.ContainsKey(ModulePair.Key))
				{
					TargetManifestToMergeInto.ModuleNameToFileName.Add(ModulePair.Key, ModulePair.Value);
				}
			}
		}

		// Checks whether a module manifest on disk is out of date (whether any of the binaries it references are newer than it is)
		private static bool IsOutOfDate(FileReference ManifestFileName, ModuleManifest Manifest)
		{
			if(!BuildTool.IsFileInstalled(ManifestFileName))
			{
				DateTime ManifestTime = FileReference.GetLastWriteTimeUtc(ManifestFileName);
				foreach(string FileName in Manifest.ModuleNameToFileName.Values)
				{
					FileInfo ModuleInfo = new FileInfo(FileReference.Combine(ManifestFileName.Directory, FileName).FullName);
					if(!ModuleInfo.Exists || ManifestTime < ModuleInfo.LastWriteTimeUtc)
					{
						return true;
					}
				}
			}
			return false;
		}
	}
}
