using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BuildToolUtilities;

namespace BuildTool
{
	// TargetDescriptor => BuildTargetDescriptor
	// Describes all of the information needed to initialize a UEBuildTarget object
	internal class BuildTargetDescriptor
	{
		public FileReference ProjectFile;
		public string Name;
		public BuildTargetPlatform Platform;
		public TargetConfiguration Configuration;
		public string Architecture;
		public CommandLineArguments AdditionalArguments;

		// Foreign plugin to compile against this target
		[CommandLine("-Plugin=")]
		public FileReference ForeignPlugin = null;

		// Set of module names to compile.
		[CommandLine("-Module=")]
		public HashSet<string> OnlyModuleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		// Single file to compile
		[CommandLine("-SingleFile=")]
		public FileReference SingleFileToCompile = null;

		// Whether to perform hot reload for this target
		[CommandLine("-NoHotReload", Value = nameof(HotReloadMode.Disabled))]
		[CommandLine("-ForceHotReload", Value = nameof(HotReloadMode.FromIDE))]
		[CommandLine("-LiveCoding", Value = nameof(HotReloadMode.LiveCoding))]
		public HotReloadMode HotReloadMode = HotReloadMode.Default;

		// Map of module name to suffix for hot reloading from the editor
		public Dictionary<string, int> HotReloadModuleNameToSuffix = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

		// Export the actions for the target to a file
		[CommandLine("-WriteActions=")]
		public List<FileReference> WriteActionFiles = new List<FileReference>();

		// Path to a file containing a list of modules that may be modified for live coding.
		[CommandLine("-LiveCodingModules=")]
		public FileReference LiveCodingModules = null;

		// Path to the manifest for passing info about the output to live coding
		[CommandLine("-LiveCodingManifest=")]
		public FileReference LiveCodingManifest = null;

		// Suppress messages about building this target
		[CommandLine("-Quiet")]
		public bool bQuiet;

		public BuildTargetDescriptor
		(
			FileReference             ProjectFilePath,
			string                    TargetNameToBuild,
			BuildTargetPlatform      PlatformToBuildFor,
			TargetConfiguration ConfigurationToBuild,
			string                    ArchitectureToBuildFor,
			CommandLineArguments      ArgumentsForTarget
		)
		{
			this.ProjectFile   = ProjectFilePath;
			this.Name          = TargetNameToBuild;
			this.Platform      = PlatformToBuildFor;
			this.Configuration = ConfigurationToBuild;
			this.Architecture  = ArchitectureToBuildFor;

			// If there are any additional command line arguments
			List<string> AdditionalArguments = new List<string>();

			if(ArgumentsForTarget != null)
			{
				// Apply the arguments to this object
				ArgumentsForTarget.ApplyTo(this);

				// Parse all the hot-reload module names
				foreach(string ModuleWithSuffix in ArgumentsForTarget.GetValues(Tag.GlobalArgument.ModuleWithSuffix))
				{
					int SuffixIdx = ModuleWithSuffix.LastIndexOf(',');
					if(SuffixIdx == -1)
					{
						throw new BuildException("Missing suffix argument from -ModuleWithSuffix=Name,Suffix");
					}

					string ModuleName = ModuleWithSuffix.Substring(0, SuffixIdx);

					if (!Int32.TryParse(ModuleWithSuffix.Substring(SuffixIdx + 1), out int Suffix))
					{
						throw new BuildException("Suffix for modules must be an integer");
					}

					HotReloadModuleNameToSuffix[ModuleName] = Suffix;
				}

				// Pull out all the arguments that haven't been used so far
				for(int Idx = 0; Idx < ArgumentsForTarget.Count; ++Idx)
				{
					if(!ArgumentsForTarget.HasBeenUsed(Idx))
					{
						AdditionalArguments.Add(ArgumentsForTarget[Idx]);
					}
				}
			}

			this.AdditionalArguments = new CommandLineArguments(AdditionalArguments.ToArray());
		}

		// Parse a list of target descriptors from the command line
		public static List<BuildTargetDescriptor> ParseCommandLine(CommandLineArguments InCommandLineArguments, bool bUsePrecompiledEngineDistribution, bool bSkipRulesCompileRulesAssemblies)
		{
			List<BuildTargetDescriptor> OutTargetDescriptors = new List<BuildTargetDescriptor>();
			ParseCommandLine(InCommandLineArguments, bUsePrecompiledEngineDistribution, bSkipRulesCompileRulesAssemblies, OutTargetDescriptors);
			return OutTargetDescriptors;
		}

		// Parse a list of target descriptors from the command line
		// 여기서 실행 파라미터 수행
		public static void ParseCommandLine
		(
            CommandLineArguments        InCommandLineArguments,
            bool                        bUsePrecompiledEngineDistribution,
            bool                        bSkipRulesCompileRulesAssemblies,
            List<BuildTargetDescriptor> ReceivingTargetDescriptors
		)
		{
			InCommandLineArguments = InCommandLineArguments.Remove(Tag.GlobalArgument.TargetList, out List<string> TargetLists);
			InCommandLineArguments = InCommandLineArguments.Remove(Tag.GlobalArgument.Target,     out List<string> Targets);

			if (0 < TargetLists.Count || 
			    0 < Targets.Count)
			{
				// Try to parse multiple arguments from a single command line
				foreach(string TargetList in TargetLists)
				{
					string[] Lines = File.ReadAllLines(TargetList);
					foreach(string Line in Lines)
					{
						string TrimLine = Line.Trim();
						if(0 < TrimLine.Length && TrimLine[0] != ';')
						{
							CommandLineArguments NewArguments = InCommandLineArguments.Append(CommandLineArguments.Split(TrimLine));
							ParseCommandLine(NewArguments, bUsePrecompiledEngineDistribution, bSkipRulesCompileRulesAssemblies, ReceivingTargetDescriptors);
						}
					}
				}

				foreach(string Target in Targets)
				{
					CommandLineArguments NewArguments = InCommandLineArguments.Append(CommandLineArguments.Split(Target));
					ParseCommandLine(NewArguments, bUsePrecompiledEngineDistribution, bSkipRulesCompileRulesAssemblies, ReceivingTargetDescriptors);
				}
			}
			else
			{
				// Otherwise just process the whole command line together
				ParseSingleCommandLine(InCommandLineArguments, bUsePrecompiledEngineDistribution, bSkipRulesCompileRulesAssemblies, ReceivingTargetDescriptors);
			}
		}

		// Parse a list of target descriptors from the command line
		public static void ParseSingleCommandLine
		(
            CommandLineArguments        Arguments,
            bool                        bUsePrecompiledEngineDistribution,
            bool                        bSkipRulesCompileRulesAssemblies,
            List<BuildTargetDescriptor> ReceivingTargetDescriptors
		)
		{
			List<BuildTargetPlatform> Platforms = new List<BuildTargetPlatform>();
			List<TargetConfiguration> Configurations = new List<TargetConfiguration>();
			List<string> TargetNames = new List<string>();
			FileReference ProjectFile = Arguments.GetFileReferenceOrDefault(Tag.GlobalArgument.Project, null);

			// Settings for creating/using static libraries for the engine
			for (int ArgumentIndex = 0; ArgumentIndex < Arguments.Count; ArgumentIndex++)
			{
				string Argument = Arguments[ArgumentIndex];
				if(0 < Argument.Length && Argument[0] != '-')
				{
					// Mark this argument as used. We'll interpret it as one thing or another.
					Arguments.MarkAsUsed(ArgumentIndex);

					// Check if it's a project file argument
					if(Argument.EndsWith(Tag.Ext.Project, StringComparison.OrdinalIgnoreCase))
					{
						FileReference NewProjectFile = new FileReference(Argument);
						if(ProjectFile != null && ProjectFile != NewProjectFile)
						{
							throw new BuildException("Multiple project files specified on command line (first {0}, then {1})", ProjectFile, NewProjectFile);
						}
						ProjectFile = new FileReference(Argument);
						continue;
					}

					// Split it into separate arguments
					string[] InlineArguments = Argument.Split('+');

					// Try to parse them as platforms
					if (BuildTargetPlatform.TryParse(InlineArguments[0], out BuildTargetPlatform ParsedPlatform))
					{
						Platforms.Add(ParsedPlatform);
						for (int InlineArgumentIdx = 1; InlineArgumentIdx < InlineArguments.Length; ++InlineArgumentIdx)
						{
							Platforms.Add(BuildTargetPlatform.Parse(InlineArguments[InlineArgumentIdx]));
						}
						continue;
					}

					// Try to parse them as configurations
					if (Enum.TryParse(InlineArguments[0], true, out TargetConfiguration ParsedConfiguration))
					{
						Configurations.Add(ParsedConfiguration);
						for (int InlineArgumentIdx = 1; InlineArgumentIdx < InlineArguments.Length; InlineArgumentIdx++)
						{
							string InlineArgument = InlineArguments[InlineArgumentIdx];
							if (!Enum.TryParse(InlineArgument, true, out ParsedConfiguration))
							{
								throw new BuildException("Invalid configuration '{0}'", InlineArgument);
							}
							Configurations.Add(ParsedConfiguration);
						}
						continue;
					}

					// Otherwise assume they are target names
					TargetNames.AddRange(InlineArguments);
				}
			}

			if (Platforms.Count == 0)
			{
				throw new BuildException("No platforms specified for target");
			}
			if (Configurations.Count == 0)
			{
				throw new BuildException("No configurations specified for target");
			}

			// Make sure the project file exists, and make sure we're using the correct case.
			if(ProjectFile != null)
			{
				FileInfo ProjectFileInfo = FileUtils.FindCorrectCase(ProjectFile.ToFileInfo());
				if(!ProjectFileInfo.Exists)
				{
					throw new BuildException("Unable to find project '{0}'.", ProjectFile);
				}
				ProjectFile = new FileReference(ProjectFileInfo);
			}

			// Expand all the platforms, architectures and configurations
			foreach(BuildTargetPlatform Platform in Platforms)
			{
				// Make sure the platform is valid
				if (!InstalledPlatformInfo.IsValid(null, Platform, null, EProjectType.Code, InstalledPlatformState.Downloaded))
				{
					if (!InstalledPlatformInfo.IsValid(null, Platform, null, EProjectType.Code, InstalledPlatformState.Supported))
					{
						throw new BuildException("The {0} platform is not supported from this engine distribution.", Platform);
					}
					else
					{
						throw new BuildException("Missing files required to build {0} targets. Enable {0} as an optional download component in the Epic Games Launcher.", Platform);
					}
				}

				// Parse the architecture parameter, or get the default for the platform
				List<string> Architectures = new List<string>(Arguments.GetValues(Tag.GlobalArgument.Architecture, '+'));
				if(Architectures.Count == 0)
				{
					Architectures.Add(BuildPlatform.GetBuildPlatform(Platform).GetDefaultArchitecture(ProjectFile));
				}

				foreach(string Architecture in Architectures)
				{
					foreach(TargetConfiguration Configuration in Configurations)
					{
						// Create all the target descriptors for targets specified by type
						foreach(string TargetTypeString in Arguments.GetValues(Tag.GlobalArgument.TargetType))
						{
							if (!Enum.TryParse(TargetTypeString, out TargetType TargetType))
							{
								throw new BuildException("Invalid target type '{0}'", TargetTypeString);
							}

							if (ProjectFile == null)
							{
								throw new BuildException("-TargetType=... requires a project file to be specified");
							}
							else
							{
								TargetNames.Add(RulesCompiler.CreateProjectRulesAssembly(ProjectFile, bUsePrecompiledEngineDistribution, bSkipRulesCompileRulesAssemblies).GetTargetNameByTypeRecursively(TargetType, Platform, Configuration, Architecture, ProjectFile));
							}
						}

						// Make sure we could parse something
						if (TargetNames.Count == 0)
						{
							throw new BuildException("No target name was specified on the command-line.");
						}

						// Create all the target descriptors
						foreach(string TargetName in TargetNames)
						{
							// If a project file was not specified see if we can find one
							if (ProjectFile == null && NativeProjects.TryGetProjectForTarget(TargetName, out ProjectFile))
							{
								Log.TraceVerbose("Found project file for {0} - {1}", TargetName, ProjectFile);
							}

							// Create the target descriptor
							ReceivingTargetDescriptors.Add(new BuildTargetDescriptor(ProjectFile, TargetName, Platform, Configuration, Architecture, Arguments));
						}
					}
				}
			}
		}

		// Try to parse the project file from the command line
		public static bool TryParseProjectFileArgument(CommandLineArguments Arguments, out FileReference OutParsedProjectFile)
		{
			if (Arguments.TryGetValue(Tag.GlobalArgument.Project, out FileReference ExplicitProjectFile))
			{
				OutParsedProjectFile = ExplicitProjectFile;
				return true;
			}

			for (int Idx = 0; Idx < Arguments.Count; ++Idx)
			{
				if(Arguments[Idx][0] != '-' && 
				   Arguments[Idx].EndsWith(Tag.Ext.Project, StringComparison.OrdinalIgnoreCase))
				{
					Arguments.MarkAsUsed(Idx);
					OutParsedProjectFile = new FileReference(Arguments[Idx]);
					return true;
				}
			}

			if(BuildTool.IsProjectInstalled())
			{
				OutParsedProjectFile = BuildTool.GetInstalledProjectFile();
				return true;
			}

			OutParsedProjectFile = null;
			return false;
		}

		// Parse a single argument value, of the form -Foo=Bar
		public static bool ParseArgumentValue(string ArgumentToParse, string ArgumentPrefix, out string OutValue)
		{
			if(ArgumentToParse.StartsWith(ArgumentPrefix, StringComparison.InvariantCultureIgnoreCase))
			{
				OutValue = ArgumentToParse.Substring(ArgumentPrefix.Length);
				return true;
			}
			else
			{
				OutValue = null;
				return false;
			}
		}

		// Format this object for the debugger
		public override string ToString()
		{
			StringBuilder Result = new StringBuilder();
			Result.AppendFormat("{0} {1} {2}", Name, Platform, Configuration);
			if(Architecture.HasValue())
			{
				Result.AppendFormat(" -Architecture={0}", Architecture);
			}
			if(ProjectFile != null)
			{
				Result.AppendFormat(" -Project={0}", StringUtils.MakePathSafeToUseWithCommandLine(ProjectFile));
			}
			if(AdditionalArguments != null && 
			   0 < AdditionalArguments.Count)
			{
				Result.AppendFormat(" {0}", AdditionalArguments);
			}
			return Result.ToString();
		}
	}
}
