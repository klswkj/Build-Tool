using System;
using System.Collections.Generic;
using System.IO;
using BuildToolUtilities;

// Mirror of Engine&Editor(Developer)::InstalledPlatformInfo.h/cpp

namespace BuildTool
{
	// The project type that support is required for
	public enum EProjectType
	{
		Unknown,
		Any,
		Code,    // Support for code projects
		Content, // Support for deploying content projects
	};

	// The state of a downloaded platform
	[Flags]
	public enum InstalledPlatformState
	{
		Supported,  // Query whether the platform is supported
		Downloaded, // Query whether the platform has been downloaded
	}

	// Contains methods to allow querying the available installed platforms
	public class InstalledPlatformInfo
	{
		// Information about a single installed platform configuration
		public struct InstalledPlatformConfiguration
		{
			public TargetConfiguration Configuration;   // Build Configuration of this combination
			public BuildTargetPlatform      Platform;        // Platform for this combination
			public TargetType                PlatformType;    // Type of Platform for this combination
			public string                    Architecture;    // Architecture for this combination
			public EProjectType              ProjectType;     // Type of project this configuration can be used for
			public string                    RequiredFile;    // Location of a file that must exist for this combination to be valid (optional)
			public bool                      bCanBeDisplayed; // Whether to display this platform as an option even if it is not valid

			public InstalledPlatformConfiguration(TargetConfiguration InConfiguration, BuildTargetPlatform InPlatform, TargetType InPlatformType, string InArchitecture, string InRequiredFile, EProjectType InProjectType, bool bInCanBeDisplayed)
			{
				Configuration   = InConfiguration;
				Platform        = InPlatform;
				PlatformType    = InPlatformType;
				Architecture    = InArchitecture;
				RequiredFile    = InRequiredFile;
				ProjectType     = InProjectType;
				bCanBeDisplayed = bInCanBeDisplayed;
			}
		}

		private static readonly List<InstalledPlatformConfiguration> InstalledPlatformConfigurations;

		static InstalledPlatformInfo()
		{
			ConfigHierarchy Ini = ConfigCache.ReadHierarchy(ConfigHierarchyType.Engine, null, BuildHostPlatform.Current.Platform);

			if (Ini.TryGetValue("InstalledPlatforms", "HasInstalledPlatformInfo", out bool bHasInstalledPlatformInfo) && bHasInstalledPlatformInfo)
			{
				InstalledPlatformConfigurations = new List<InstalledPlatformConfiguration>();
				if (Ini.GetArray("InstalledPlatforms", "InstalledPlatformConfigurations", out List<string> InstalledPlatforms))
				{
					foreach (string InstalledPlatform in InstalledPlatforms)
					{
						ParsePlatformConfiguration(InstalledPlatform);
					}
				}
			}
		}

		private static void ParsePlatformConfiguration(string PlatformConfiguration)
		{
			// Trim whitespace at the beginning.
			PlatformConfiguration = PlatformConfiguration.Trim();
			// Remove brackets.
			PlatformConfiguration = PlatformConfiguration.TrimStart('(');
			PlatformConfiguration = PlatformConfiguration.TrimEnd(')');

			bool bCanCreateEntry = true;

			TargetConfiguration Configuration = TargetConfiguration.Unknown;
			if (ParseSubValue(PlatformConfiguration, Tag.ConfigKey.Configuration, out string ConfigurationName))
			{
				Enum.TryParse(ConfigurationName, out Configuration);
			}
			if (Configuration == TargetConfiguration.Unknown)
			{
				Log.TraceWarning("Unable to read configuration from {0}", PlatformConfiguration);
				bCanCreateEntry = false;
			}

			if (ParseSubValue(PlatformConfiguration, Tag.ConfigKey.PlatformName, out string PlatformName))
			{
				if (!BuildTargetPlatform.IsValidName(PlatformName))
				{
					Log.TraceWarning("Unable to read platform from {0}", PlatformConfiguration);
					bCanCreateEntry = false;
				}
			}

			TargetType PlatformType = TargetType.Game;
			if (ParseSubValue(PlatformConfiguration, Tag.ConfigKey.PlatformType, out string PlatformTypeName))
			{
				if (!Enum.TryParse(PlatformTypeName, out PlatformType))
				{
					Log.TraceWarning("Unable to read Platform Type from {0}, defaulting to Game", PlatformConfiguration);
					PlatformType = TargetType.Game;
				}
			}
			if (PlatformType == TargetType.Program)
			{
				Log.TraceWarning("Program is not a valid PlatformType for an Installed Platform, defaulting to Game");
				PlatformType = TargetType.Game;
			}

			ParseSubValue(PlatformConfiguration, Tag.ConfigKey.Architecture, out string Architecture);

			if (ParseSubValue(PlatformConfiguration, Tag.ConfigKey.RequiredFile, out string RequiredFile))
			{
				RequiredFile = FileReference.Combine(BuildTool.RootDirectory, RequiredFile).ToString();
			}

			EProjectType ProjectType = EProjectType.Any;
			if (ParseSubValue(PlatformConfiguration, Tag.ConfigKey.ProjectType, out string ProjectTypeName))
			{
				Enum.TryParse(ProjectTypeName, out ProjectType);
			}

			if (ProjectType == EProjectType.Unknown)
			{
				Log.TraceWarning("Unable to read project type from {0}", PlatformConfiguration);
				bCanCreateEntry = false;
			}

			bool bCanBeDisplayed = false;
			if (ParseSubValue(PlatformConfiguration, Tag.ConfigKey.bCanBeDisplayed, out string CanBeDisplayedString))
			{
				bCanBeDisplayed = Convert.ToBoolean(CanBeDisplayedString);
			}

			if (bCanCreateEntry)
			{
				InstalledPlatformConfigurations.Add
				(
					new InstalledPlatformConfiguration
					(
					    Configuration,
					    BuildTargetPlatform.Parse(PlatformName),
					    PlatformType,
					    Architecture,
					    RequiredFile,
					    ProjectType,
					    bCanBeDisplayed
					)
				);
			}
		}

		private static bool ParseSubValue(string TrimmedLine, string Match, out string Result)
		{
			Result = string.Empty;
			int MatchIndex = TrimmedLine.IndexOf(Match);
			if (MatchIndex < 0)
			{
				return false;
			}
			// Get the remainder of the string after the match
			MatchIndex += Match.Length;
			TrimmedLine = TrimmedLine.Substring(MatchIndex);
			if (String.IsNullOrEmpty(TrimmedLine))
			{
				return false;
			}
			// get everything up to the first comma and trim any new whitespace
			Result = TrimmedLine.Split(',')[0];
			Result = Result.Trim();
			if (Result.StartsWith("\""))
			{
				// Remove quotes
				int QuoteEnd = Result.LastIndexOf('\"');
				if (0 < QuoteEnd)
				{
					Result = Result.Substring(1, QuoteEnd - 1);
				}
			}
			return true;
		}

		// Determine if the given configuration is available for any platform
		public static bool IsValidConfiguration(TargetConfiguration ConfigurationToCheck, EProjectType ProjectType = EProjectType.Any)
		{
			return ContainsValidConfiguration
			(
				(InstalledPlatformConfiguration PlatformConfig) => 
				PlatformConfig.Configuration == ConfigurationToCheck &&
				(ProjectType == EProjectType.Any || PlatformConfig.ProjectType == EProjectType.Any || PlatformConfig.ProjectType == ProjectType)
			);
		}

		// Determine if the given platform is available
		public static bool IsValidPlatform(BuildTargetPlatform PlatformToCheck, EProjectType ProjectType = EProjectType.Any)
		{
			// HACK: For installed builds, we always need to treat Mac as a valid platform for generating project files.
			// When remote building from PC, we won't have all the libraries to do this, so we need to fake it.
			if(PlatformToCheck == BuildTargetPlatform.Mac                    && 
			   ProjectType == EProjectType.Any                                && 
			   BuildHostPlatform.Current.Platform == BuildTargetPlatform.Mac && 
			   BuildTool.IsEngineInstalled())
			{
				return true;
			}

			return ContainsValidConfiguration
			(
				(InstalledPlatformConfiguration CurConfig) => 
				CurConfig.Platform == PlatformToCheck && 
				(ProjectType == EProjectType.Any || CurConfig.ProjectType == EProjectType.Any || CurConfig.ProjectType == ProjectType)
			);
		}

		// Determine whether the given platform/configuration/project type combination is supported
		public static bool IsValidPlatformAndConfiguration(TargetConfiguration Configuration, BuildTargetPlatform Platform, EProjectType ProjectType = EProjectType.Any)
		{
			return ContainsValidConfiguration
			(
				(InstalledPlatformConfiguration CurConfig) => 
				CurConfig.Configuration == Configuration && 
				CurConfig.Platform == Platform           && 
				(ProjectType == EProjectType.Any || CurConfig.ProjectType == EProjectType.Any || CurConfig.ProjectType == ProjectType)
			);
		}

		// Determines whether the given target type is supported
		public static bool IsValid
		(
            TargetType?                TargetType,
            BuildTargetPlatform?      Platform,
            TargetConfiguration? Configuration,
            EProjectType               ProjectType,
            InstalledPlatformState     State
		)
		{
			if(!BuildTool.IsEngineInstalled() || InstalledPlatformConfigurations == null)
			{
				return true;
			}

			foreach(InstalledPlatformConfiguration Config in InstalledPlatformConfigurations)
			{
				// Check whether this configuration matches all the criteria
				if(TargetType.HasValue && Config.PlatformType != TargetType.Value)
				{
					continue;
				}
				if(Platform.HasValue && Config.Platform != Platform.Value)
				{
					continue;
				}
				if(Configuration.HasValue && Config.Configuration != Configuration.Value)
				{
					continue;
				}
				if(ProjectType        != EProjectType.Any && 
				   Config.ProjectType != EProjectType.Any && 
				   Config.ProjectType != ProjectType)
				{
					continue;
				}
				if(State == InstalledPlatformState.Downloaded && Config.RequiredFile.HasValue() && !File.Exists(Config.RequiredFile))
				{
					continue;
				}

				// Success!
				return true;
			}

			return false;
		}

		private static bool ContainsValidConfiguration(Predicate<InstalledPlatformConfiguration> ConfigFilter)
		{
			if (BuildTool.IsEngineInstalled() && InstalledPlatformConfigurations != null)
			{
				foreach (InstalledPlatformConfiguration PlatformConfiguration in InstalledPlatformConfigurations)
				{
					// Check whether filter accepts this configuration and it has required file
					if (ConfigFilter(PlatformConfiguration) && 
						(string.IsNullOrEmpty(PlatformConfiguration.RequiredFile) || File.Exists(PlatformConfiguration.RequiredFile)))
					{
						return true;
					}
				}

				return false;
			}
			return true;
		}

		public static void WriteConfigFileEntries(List<InstalledPlatformConfiguration> Configs, ref List<String> OutEntries)
		{
			// Write config section header
			OutEntries.Add("[InstalledPlatforms]");
			OutEntries.Add(Tag.ConfigKey.HasInstalledPlatformInfo + Tag.Boolean.True);

			foreach (InstalledPlatformConfiguration Config in Configs)
			{
				WriteConfigFileEntry(Config, ref OutEntries);
			}
		}

		private static void WriteConfigFileEntry(InstalledPlatformConfiguration Config, ref List<String> OutEntries)
		{
			string ConfigDescription = "+" + Tag.ConfigKey.InstalledPlatformConfigurations + "=(";
			ConfigDescription += string.Format(Tag.ConfigKey.PlatformName + "\"{0}\", ", Config.Platform.ToString());
			if (Config.Configuration != TargetConfiguration.Unknown)
			{
				ConfigDescription += string.Format(Tag.ConfigKey.Configuration + "\"{0}\", ", Config.Configuration.ToString());
			}
			if (Config.PlatformType != TargetType.Program)
			{
				ConfigDescription += string.Format(Tag.ConfigKey.PlatformType + "\"{0}\", ", Config.PlatformType.ToString());
			}
			if (!string.IsNullOrEmpty(Config.Architecture))
			{
				ConfigDescription += string.Format(Tag.ConfigKey.Architecture + "\"{0}\", ", Config.Architecture);
			}
			if (!string.IsNullOrEmpty(Config.RequiredFile))
			{
				ConfigDescription += string.Format(Tag.ConfigKey.RequiredFile + "\"{0}\", ", Config.RequiredFile);
			}
			if (Config.ProjectType != EProjectType.Unknown)
			{
				ConfigDescription += string.Format(Tag.ConfigKey.ProjectType + "\"{0}\", ", Config.ProjectType.ToString());
			}
			ConfigDescription += string.Format(Tag.ConfigKey.bCanBeDisplayed + "{0})", Config.bCanBeDisplayed.ToString());

			OutEntries.Add(ConfigDescription);
		}
	}
}
