using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BuildToolUtilities;

namespace BuildTool
{
	// Stores the version (or a unique build ID) for the modules for a target in a certain folder.
	// 
	// This allows the runtime to identify which modules are used for which files, and which version they're at.
	// This prevents stale binaries from being loaded by the runtime when making a local unversioned build,
	// and allows faster incremental builds than compiling the build changelist into every module when making versioned builds.
	
	// #Keep Sync with ModuleManifest.h/cpp in EngineSourceCode at CORE_API

	[Serializable]
	class ModuleManifest
	{
		public string                     BuildId;
		public Dictionary<string, string> ModuleNameToFileName = new Dictionary<string, string>();

		// Constructs the module map with the given changelist
		// <param name="InBuildId">The unique build id</param>
		public ModuleManifest(string InUniqueBuildId)
		{
			BuildId = InUniqueBuildId;
		}

		// Merge another manifest into this one
		// <param name="Other">The manifest to merge in</param>
		public void Include(ModuleManifest Other)
		{
			foreach (KeyValuePair<string, string> Pair in Other.ModuleNameToFileName)
			{
				if (!ModuleNameToFileName.ContainsKey(Pair.Key))
				{
					ModuleNameToFileName.Add(Pair.Key, Pair.Value);
				}
			}
		}

		// Gets the standard path for an manifest
		public static string GetStandardFileName
		(
			string                    ModularAppName,
			BuildTargetPlatform      TargetPlatform,
			TargetConfiguration TargetConfiguration,
			string                    TargetPlatformBuildArchitecture,
			bool                      bIsGameDirectory
		)
		{
			string BaseName = ModularAppName;

			if (TargetConfiguration != TargetConfiguration.Development && 
				!(TargetConfiguration == TargetConfiguration.DebugGame && 
				!bIsGameDirectory))
			{
				BaseName += String.Format("-{0}-{1}", TargetPlatform.ToString(), TargetConfiguration.ToString());
			}

			if(!String.IsNullOrEmpty(TargetPlatformBuildArchitecture) && 
				BuildPlatform.GetBuildPlatform(TargetPlatform).RequiresArchitectureSuffix())
			{
				BaseName += TargetPlatformBuildArchitecture;
			}

			return String.Format("{0}.modules", BaseName);
		}

		// Read an app receipt from disk
		public static ModuleManifest Read(FileReference FileNameToRead)
		{
			JsonObject Object = JsonObject.Read(FileNameToRead);

			ModuleManifest Receipt = new ModuleManifest(Object.GetStringField(nameof(BuildId)));

			JsonObject Modules = Object.GetObjectField("Modules");
			foreach (string ModuleName in Modules.KeyNames)
			{
				Receipt.ModuleNameToFileName.Add(ModuleName, Modules.GetStringField(ModuleName));
			}
			return Receipt;
		}

		// Tries to read a receipt from disk.
		public static bool TryRead(FileReference FileNameToRead, out ModuleManifest Result)
		{
			if (!FileReference.Exists(FileNameToRead))
			{
				Result = null;
				return false;
			}
			try
			{
				Result = Read(FileNameToRead);
				return true;
			}
			catch (Exception)
			{
				Result = null;
				return false;
			}
		}

		// Write the receipt to disk.
		public void Write(FileReference FileNameToWrite)
		{
			DirectoryReference.CreateDirectory(FileNameToWrite.Directory);
			using(StreamWriter Writer = new StreamWriter(FileNameToWrite.FullName))
			{
				Write(Writer);
			}
		}

		// Write the receipt to disk.
		public void Write(TextWriter Writer)
		{
			using (JsonWriter OutputWriter = new JsonWriter(Writer, true))
			{
				OutputWriter.WriteObjectStart();
				OutputWriter.WriteValue(nameof(ModuleManifest.BuildId), BuildId);

				OutputWriter.WriteObjectStart(nameof(ModuleManifest.ModuleNameToFileName));

				foreach (KeyValuePair<string, string> ModuleNameToFileNamePair in ModuleNameToFileName.OrderBy(x => x.Key))
				{
					OutputWriter.WriteValue(ModuleNameToFileNamePair.Key, ModuleNameToFileNamePair.Value);
				}

				OutputWriter.WriteObjectEnd();
				OutputWriter.WriteObjectEnd();
			}
		}
	}
}
