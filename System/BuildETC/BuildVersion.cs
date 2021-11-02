using System;
using System.IO;
using BuildToolUtilities;

namespace BuildTool
{
	[Serializable]
	public class BuildVersion
	{
		public int    MajorVersion;
		public int    MinorVersion;
		public int    PatchVersion;         // The hotfix/patch version
		public int    Changelist;           // The changelist that the engine is being built from
		public int    CompatibleChangelist; // The changelist that the engine maintains compatibility with
		public bool   IsLicenseeVersion;    // Whether the changelist numbers are a licensee changelist
		public bool   IsPromotedBuild;      // built strictly from a clean sync of the given changelist
		public string BranchName;           // Name of the current branch, with '/' characters escaped as '+'
		public string BuildId;              // This will be generated automatically whenever engine binaries change if not set in the default Engine/Build/Build.version.
		public string BuildVersionString;   // The build version string

		/*
		static public readonly string MajorVersionTag         = "MajorVersion";
		static public readonly string MinorVersionTag         = "MinorVersion";
		static public readonly string PatchVersionTag         = "PatchVersion";
		static public readonly string BuildTag                = "Build";
		static public readonly string BuildVersionTag         = "Build.Version";
		static public readonly string ChangeListTag           = "ChangeList";
		static public readonly string CompatibleChangeListTag = "CompatibleChangeList";
		static public readonly string IsLicenseeVersionTag    = "IsLicenseeVersion";
		static public readonly string IsPromotedBuildTag      = "IsPromotedBuild";
		static public readonly string BranchNameTag           = "BranchName";
		static public readonly string BuildIdTag              = "BuildId";
		*/
		// Returns the value which can be used as the compatible changelist.
		// Requires that the regular changelist is also set, and defaults to the 
		// regular changelist if a specific compatible changelist is not set.
		public int EffectiveCompatibleChangelist
		{
			get { return (Changelist != 0 && CompatibleChangelist != 0)? CompatibleChangelist : Changelist; }
		}

		public static bool TryRead(FileReference FileNameInDisk, out BuildVersion Version)
		{
            if (!JsonObject.TryRead(FileNameInDisk, out JsonObject Object))
            {
                Version = null;
                return false;
            }
            return TryParse(Object, out Version);
		}

		public static FileReference GetDefaultFileName()
		{
			return FileReference.Combine(BuildTool.EngineDirectory, Tag.Directory.Build, Tag.Directory.BuildVersion);
		}

        // Get the default path for a target's version file.
        // <param name="OutputDirectory">The output directory for the executable.
		// For MacOS, this is the directory containing the app bundle.
        public static FileReference GetFileNameForTarget
        (
            DirectoryReference        OutputDirectory,
            string                    TargetName,
            BuildTargetPlatform      Platform,
            TargetConfiguration Configuration,
            string                    Architecture
        )
        {
			// Get the architecture suffix. Platforms have the option of overriding whether to include this string in filenames.
			string ArchitectureSuffix = "";
			if(BuildPlatform.GetBuildPlatform(Platform).RequiresArchitectureSuffix())
			{
				ArchitectureSuffix = Architecture;
			}
		
			// Build the output filename
			if (String.IsNullOrEmpty(ArchitectureSuffix) && Configuration == TargetConfiguration.Development)
			{
				return FileReference.Combine(OutputDirectory, TargetName + Tag.Ext.Version);
			}
			else
			{
				return FileReference.Combine(OutputDirectory, TargetName + Platform.ToString() + Configuration.ToString() + ArchitectureSuffix + Tag.Ext.Version);
			}
		}

		// Parses a build version from a JsonObject
		public static bool TryParse(JsonObject ObjectToRead, out BuildVersion OutVersion)
		{
			BuildVersion NewVersion = new BuildVersion();
			if (!ObjectToRead.TryGetIntegerField(Tag.JSONField.MajorVersion, out NewVersion.MajorVersion) || 
				!ObjectToRead.TryGetIntegerField(Tag.JSONField.MinorVersion, out NewVersion.MinorVersion) || 
				!ObjectToRead.TryGetIntegerField(Tag.JSONField.PatchVersion, out NewVersion.PatchVersion))
			{
				OutVersion = null;
				return false;
			}

			ObjectToRead.TryGetIntegerField(Tag.JSONField.ChangeList, out NewVersion.Changelist);
			ObjectToRead.TryGetIntegerField(Tag.JSONField.CompatibleChangeList, out NewVersion.CompatibleChangelist);

            ObjectToRead.TryGetIntegerField(Tag.JSONField.IsLicenseeVersion, out int IsLicenseeVersionInt);
            NewVersion.IsLicenseeVersion = IsLicenseeVersionInt != 0;

            ObjectToRead.TryGetIntegerField(Tag.JSONField.IsPromotedBuild, out int IsPromotedBuildInt);
            NewVersion.IsPromotedBuild = IsPromotedBuildInt != 0;

			ObjectToRead.TryGetStringField(Tag.JSONField.BranchName, out NewVersion.BranchName);
			ObjectToRead.TryGetStringField(Tag.JSONField.BuildId, out NewVersion.BuildId);
			ObjectToRead.TryGetStringField(Tag.JSONField.BuildVersion, out NewVersion.BuildVersionString);

			OutVersion = NewVersion;
			return true;
		}

		// Exports this object as Json
		public void Write(FileReference FileName)
		{
			using (StreamWriter Writer = new StreamWriter(FileName.FullName))
			{
				Write(Writer);
			}
		}

		// Exports this object as Json
		public void Write(TextWriter Writer)
		{
			using (JsonWriter OtherWriter = new JsonWriter(Writer))
			{
				OtherWriter.WriteObjectStart();
				WriteProperties(OtherWriter);
				OtherWriter.WriteObjectEnd();
			}
		}

		// Exports this object as Json
		public void WriteProperties(JsonWriter JSONWriter)
		{
			JSONWriter.WriteValue(Tag.JSONField.MajorVersion,         MajorVersion);
			JSONWriter.WriteValue(Tag.JSONField.MinorVersion,         MinorVersion);
			JSONWriter.WriteValue(Tag.JSONField.PatchVersion,         PatchVersion);
			JSONWriter.WriteValue(Tag.JSONField.ChangeList,           Changelist);
			JSONWriter.WriteValue(Tag.JSONField.CompatibleChangeList, CompatibleChangelist);
			JSONWriter.WriteValue(Tag.JSONField.IsLicenseeVersion,    IsLicenseeVersion? 1 : 0);
			JSONWriter.WriteValue(Tag.JSONField.IsPromotedBuild,      IsPromotedBuild? 1 : 0);
			JSONWriter.WriteValue(Tag.JSONField.BranchName,           BranchName);
			JSONWriter.WriteValue(Tag.JSONField.BuildId,              BuildId);
			JSONWriter.WriteValue(Tag.JSONField.BuildVersion,         BuildVersionString);
		}
	}

	// Read-only wrapper for a BuildVersion instance
	public class ReadOnlyBuildVersion
	{
		private readonly BuildVersion Inner;
		private static ReadOnlyBuildVersion CurrentCached; // Cached copy of the current build version

		public ReadOnlyBuildVersion(BuildVersion WrittenBuildVersion)
		{
			this.Inner = WrittenBuildVersion;
		}

		// Gets the current build version
		public static ReadOnlyBuildVersion Current
		{
			get
			{
				if(CurrentCached == null)
				{
					FileReference File = BuildVersion.GetDefaultFileName();
					if(!FileReference.Exists(File))
					{
						throw new BuildException("Version file is missing ({0})", File);
					}

                    if (!BuildVersion.TryRead(File, out BuildVersion Version))
                    {
                        throw new BuildException("Unable to read version file ({0}). Check that this file is present and well-formed JSON.", File);
                    }

                    CurrentCached = new ReadOnlyBuildVersion(Version);
				}
				return CurrentCached;
			}
		}

		// Accessors for fields on the inner BuildVersion instance
		#region READONLY_ACCESOR_PROPERTIES
#if !__MonoCS__
#pragma warning disable IDE0079 // Remove unnecessary suppression
#pragma warning disable CS1591
#pragma warning restore IDE0079 // Remove unnecessary suppression
#endif
		public int MajorVersion
        {
			get { return Inner.MajorVersion; }
		}

		public int MinorVersion
		{
			get { return Inner.MinorVersion; }
		}

		public int PatchVersion
		{
			get { return Inner.PatchVersion; }
		}

		public int Changelist
		{
			get { return Inner.Changelist; }
		}

		public int CompatibleChangelist
		{
			get { return Inner.CompatibleChangelist; }
		}

		public int EffectiveCompatibleChangelist
		{
			get { return Inner.EffectiveCompatibleChangelist; }
		}

		public bool IsLicenseeVersion
		{
			get { return Inner.IsLicenseeVersion; }
		}

		public bool IsPromotedBuild
		{
			get { return Inner.IsPromotedBuild; }
		}

		public string BranchName
		{
			get { return Inner.BranchName; }
		}

		public string BuildVersionString
		{
			get { return Inner.BuildVersionString; }
		}

#if !__MonoCS__
#pragma warning disable IDE0079 // Remove unnecessary suppression
#pragma warning restore C1591
#pragma warning restore IDE0079 // Remove unnecessary suppression
#endif
		#endregion READONLY_ACCESOR_PROPERTIES
	}
}
