using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Serialization;
using BuildToolUtilities;

// See this : https://pvs-studio.com/en/docs/manual/full/

namespace BuildTool
{
	// Flags for the PVS analyzer mode
	public enum PVSAnalysisModeFlags : uint
	{
		Check64BitPortability = 1 << 0, // Check for 64-bit portability issues
		// PVSAnyalysis has no value 2.
		GeneralAnalysis       = 1 << 2, // Enable general analysis
		Optimizations         = 1 << 3, // Check for optimizations
		CustomerSpecific      = 1 << 4, // Enable customer-specific rules
		MISRA                 = 1 << 5, // Enable MISRA analysis
	}

	// Partial representation of PVS-Studio main settings file
	[XmlRoot("ApplicationSettings")]
	public class PVSApplicationSettings
	{
		public string[] PathMasks; // Masks for paths excluded for analysis
		public string UserName; // Registered username
		public string SerialNumber; // Registered serial number
		public bool Disable64BitAnalysis; // Disable the 64-bit Analysis

		public bool DisableGeneralAnalysis;
		public bool DisableOptimizationAnalysis;
		public bool DisableCustomerSpecificAnalysis;
		public bool DisableMISRAAnalysis;

		public PVSAnalysisModeFlags GetModeFlags()
		{
			PVSAnalysisModeFlags Flags = 0;
			if (!Disable64BitAnalysis)
			{
				Flags |= PVSAnalysisModeFlags.Check64BitPortability;
			}
			if (!DisableGeneralAnalysis)
			{
				Flags |= PVSAnalysisModeFlags.GeneralAnalysis;
			}
			if (!DisableOptimizationAnalysis)
			{
				Flags |= PVSAnalysisModeFlags.Optimizations;
			}
			if (!DisableCustomerSpecificAnalysis)
			{
				Flags |= PVSAnalysisModeFlags.CustomerSpecific;
			}
			if (!DisableMISRAAnalysis)
			{
				Flags |= PVSAnalysisModeFlags.MISRA;
			}
			return Flags;
		}

		// Attempts to read the application settings from the default location
		internal static PVSApplicationSettings Read()
		{
			FileReference SettingsPath = FileReference.Combine(new DirectoryReference(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)), "PVS-Studio", "Settings.xml");
			if (FileReference.Exists(SettingsPath))
			{
				try
				{
					XmlSerializer Serializer = new XmlSerializer(typeof(PVSApplicationSettings));
					using (FileStream Stream = new FileStream(SettingsPath.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
					{
						return (PVSApplicationSettings)Serializer.Deserialize(Stream);
					}
				}
				catch (Exception Ex)
				{
					throw new BuildException(Ex, "Unable to read PVS-Studio settings file from {0}", SettingsPath);
				}
			}
			return null;
		}
	}

	public class PVSTargetSettings
	{
		// Returns the application settings
		internal Lazy<PVSApplicationSettings> ApplicationSettings { get; } = new Lazy<PVSApplicationSettings>(() => PVSApplicationSettings.Read());

		// Whether to use application settings to determine the analysis mode
		public bool UseApplicationSettings { get; set; }

		// Override for the analysis mode to use
		public PVSAnalysisModeFlags ModeFlags
		{
			get
 			{
				if (ModePrivate.HasValue)
				{
					return ModePrivate.Value;
				}
				else if (UseApplicationSettings && ApplicationSettings.Value != null)
				{
					return ApplicationSettings.Value.GetModeFlags();
				}
				else
				{
					return PVSAnalysisModeFlags.GeneralAnalysis;
				}
			}
			set
			{
				ModePrivate = value;
			}
		}

		// Private storage for the mode flags
		PVSAnalysisModeFlags? ModePrivate;
	}

	// Read-only version of the PVS toolchain settings
	public class ReadOnlyPVSTargetSettings
	{
        private readonly PVSTargetSettings Inner;

        public ReadOnlyPVSTargetSettings(PVSTargetSettings Inner) => this.Inner = Inner;

        internal PVSApplicationSettings ApplicationSettings => Inner.ApplicationSettings.Value;

        public bool UseApplicationSettings => Inner.UseApplicationSettings;

        // Override for the analysis mode to use
        public PVSAnalysisModeFlags ModeFlags => Inner.ModeFlags;
    }

	class PVSToolChain : ToolChain
	{
		private readonly VCToolChain               InnerToolChain;
		private readonly ReadOnlyTargetRules       Target;
		private readonly ReadOnlyPVSTargetSettings Settings;
		private readonly PVSApplicationSettings    ApplicationSettings;
		private readonly FileReference             AnalyzerFile;
		private readonly FileReference             LicenseFile;
		private readonly BuildTargetPlatform      Platform;

		public PVSToolChain(ReadOnlyTargetRules Target)
		{
			this.Target = Target;
			Platform = Target.Platform;
			InnerToolChain = new VCToolChain(Target);

			AnalyzerFile = FileReference.Combine(new DirectoryReference(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)), "PVS-Studio", "x64", "PVS-Studio.exe");
			if(!FileReference.Exists(AnalyzerFile))
			{
				FileReference EngineAnalyzerFile = FileReference.Combine(BuildTool.RootDirectory, "Engine", "Extras", "ThirdPartyNotUE", "NoRedist", "PVS-Studio", "PVS-Studio.exe");
				if (FileReference.Exists(EngineAnalyzerFile))
				{
					AnalyzerFile = EngineAnalyzerFile;
				}
				else
				{
					throw new BuildException("Unable to find PVS-Studio at {0} or {1}", AnalyzerFile, EngineAnalyzerFile);
				}
			}

			Settings = Target.WindowsPlatform.PVS;
			ApplicationSettings = Settings.ApplicationSettings;

			if(ApplicationSettings != null)
			{
				if (Settings.ModeFlags == 0)
				{
					throw new BuildException("All PVS-Studio analysis modes are disabled.");
				}

				if (!String.IsNullOrEmpty(ApplicationSettings.UserName) && !String.IsNullOrEmpty(ApplicationSettings.SerialNumber))
				{
					LicenseFile = FileReference.Combine(BuildTool.EngineDirectory, "Intermediate", "PVS", "PVS-Studio.lic");
					StringUtils.WriteFileIfChanged(LicenseFile, String.Format("{0}\n{1}\n", ApplicationSettings.UserName, ApplicationSettings.SerialNumber), StringComparison.Ordinal);
				}
			}
			else
			{
				FileReference DefaultLicenseFile = AnalyzerFile.ChangeExtension(".lic");
				if(FileReference.Exists(DefaultLicenseFile))
				{
					LicenseFile = DefaultLicenseFile;
				}
			}
		}

		public override void GetVersionInfo(List<string> Lines)
		{
			base.GetVersionInfo(Lines);

			ReadOnlyPVSTargetSettings Settings = Target.WindowsPlatform.PVS;
			Lines.Add(String.Format("Using PVS-Studio installation at {0} with analysis mode {1} ({2})", AnalyzerFile, (uint)Settings.ModeFlags, Settings.ModeFlags.ToString()));
		}

		public override CPPOutput GenerateOnlyISPCHeaders(CppCompileEnvironment Environment, List<FileItem> InputFiles, DirectoryReference OutputDir, IActionGraphBuilder Graph)
		{
			return null;
		}

		class ActionGraphCapture : ForwardingActionGraphBuilder
		{
			private readonly List<Action> Actions;

			public ActionGraphCapture(IActionGraphBuilder Inner, List<Action> Actions)
				: base(Inner)
			{
				this.Actions = Actions;
			}

			public override Action CreateAction(ActionType Type)
			{
				Action Action = base.CreateAction(Type);
				Actions.Add(Action);
				return Action;
			}
		}

		public override CPPOutput CompileCPPFiles(CppCompileEnvironment CompileEnvironment, List<FileItem> InputFiles, DirectoryReference OutputDir, string ModuleName, IActionGraphBuilder Graph)
		{
			// Use a subdirectory for PVS output, to avoid clobbering regular build artifacts
			OutputDir = DirectoryReference.Combine(OutputDir, "PVS");

            // Preprocess the source files with the regular toolchain
            CppCompileEnvironment PreprocessCompileEnvironment = new CppCompileEnvironment(CompileEnvironment)
            {
                bPreprocessOnly                    = true,
                bEnableUndefinedIdentifierWarnings = false // Not sure why THIRD_PARTY_INCLUDES_START doesn't pick this up; the _Pragma appears in the preprocessed output. Perhaps in preprocess-only mode the compiler doesn't respect these?
            };

            PreprocessCompileEnvironment.Definitions.Add("PVS_STUDIO");

			List<Action> PreprocessActions = new List<Action>();
			CPPOutput Result = InnerToolChain.CompileCPPFiles(PreprocessCompileEnvironment, InputFiles, OutputDir, ModuleName, new ActionGraphCapture(Graph, PreprocessActions));

			// Run the source files through PVS-Studio
			foreach(Action PreprocessAction in PreprocessActions)
			{
				if (PreprocessAction.Type != ActionType.Compile)
				{
					continue;
				}

				FileItem SourceFileItem = PreprocessAction.PrerequisiteItems.FirstOrDefault(x => x.HasExtension(".c") || x.HasExtension(".cc") || x.HasExtension(".cpp"));
				if (SourceFileItem == null)
				{
					Log.TraceWarning("Unable to find source file from command: {0} {1}", PreprocessAction.CommandArguments);
					continue;
				}

				FileItem PreprocessedFileItem = PreprocessAction.ProducedItems.FirstOrDefault(x => x.HasExtension(".i"));
				if (PreprocessedFileItem == null)
				{
					Log.TraceWarning("Unable to find preprocessed output file from command: {0} {1}", PreprocessAction.CommandArguments);
					continue;
				}

				// Disable a few warnings that seem to come from the preprocessor not respecting _Pragma
				PreprocessAction.CommandArguments += " /wd4005"; // macro redefinition
				PreprocessAction.CommandArguments += " /wd4828"; // file contains a character starting at offset xxxx that is illegal in the current source character set

				// Write the PVS studio config file
				StringBuilder ConfigFileContents = new StringBuilder();
				foreach(DirectoryReference IncludePath in Target.WindowsPlatform.Environment.IncludePaths)
				{
					ConfigFileContents.AppendFormat("exclude-path={0}\n", IncludePath.FullName);
				}
				if(ApplicationSettings != null && ApplicationSettings.PathMasks != null)
				{
					foreach(string PathMask in ApplicationSettings.PathMasks)
					{
						if (PathMask.Contains(":") || PathMask.Contains("\\") || PathMask.Contains("/"))
						{
							if(Path.IsPathRooted(PathMask) && !PathMask.Contains(":"))
							{
								ConfigFileContents.AppendFormat("exclude-path=*{0}*\n", PathMask);
							}
							else
							{
								ConfigFileContents.AppendFormat("exclude-path={0}\n", PathMask);
							}
						}
					}
				}
				if (Platform == BuildTargetPlatform.Win64)
				{
					ConfigFileContents.Append("platform=x64\n");
				}
				else if(Platform == BuildTargetPlatform.Win32)
				{
					ConfigFileContents.Append("platform=Win32\n");
				}
				else
				{
					throw new BuildException("PVS-Studio does not support this platform");
				}
				ConfigFileContents.Append("preprocessor=visualcpp\n");
				ConfigFileContents.Append("language=C++\n");
				ConfigFileContents.Append("skip-cl-exe=yes\n");
				ConfigFileContents.AppendFormat("i-file={0}\n", PreprocessedFileItem.FileDirectory.FullName);

				string BaseFileName = PreprocessedFileItem.FileDirectory.GetFileNameWithoutExtension();

				FileReference ConfigFileLocation = FileReference.Combine(OutputDir, BaseFileName + ".cfg");
				FileItem ConfigFileItem = Graph.CreateIntermediateTextFile(ConfigFileLocation, ConfigFileContents.ToString());

				// Run the analzyer on the preprocessed source file
				FileReference OutputFileLocation = FileReference.Combine(OutputDir, BaseFileName + ".pvslog");
				FileItem OutputFileItem = FileItem.GetItemByFileReference(OutputFileLocation);

				Action AnalyzeAction = Graph.CreateAction(ActionType.Compile);
				AnalyzeAction.CommandDescription = "Analyzing";
				AnalyzeAction.StatusDescription  = BaseFileName;
				AnalyzeAction.WorkingDirectory   = BuildTool.EngineSourceDirectory;
				AnalyzeAction.CommandPath        = AnalyzerFile;
				AnalyzeAction.CommandArguments   = String.Format("--cl-params \"{0}\" --source-file \"{1}\" --output-file \"{2}\" --cfg \"{3}\" --analysis-mode {4}", PreprocessAction.CommandArguments, SourceFileItem.AbsolutePath, OutputFileLocation, ConfigFileItem.AbsolutePath, (uint)Settings.ModeFlags);
				
				if (LicenseFile != null)
				{
					AnalyzeAction.CommandArguments += String.Format(" --lic-file \"{0}\"", LicenseFile);
					AnalyzeAction.PrerequisiteItems.Add(FileItem.GetItemByFileReference(LicenseFile));
				}

				AnalyzeAction.PrerequisiteItems.Add(ConfigFileItem);
				AnalyzeAction.PrerequisiteItems.Add(PreprocessedFileItem);
				AnalyzeAction.PrerequisiteItems.AddRange(InputFiles); // Add the InputFiles as PrerequisiteItems so that in SingleFileCompile mode the PVSAnalyze step is not filtered out
				AnalyzeAction.ProducedItems.Add(OutputFileItem);
				AnalyzeAction.DeleteItems.Add(OutputFileItem); // PVS Studio will append by default, so need to delete produced items

				Result.ObjectFiles.AddRange(AnalyzeAction.ProducedItems);
			}
			return Result;
		}

        public override CPPOutput CompileISPCFiles(CppCompileEnvironment Environment, List<FileItem> InputFiles, DirectoryReference OutputDir, IActionGraphBuilder Graph)
        {
			return null;
        }

        public override CPPOutput CompileRCFiles(CppCompileEnvironment Environment, List<FileItem> InputFiles, DirectoryReference OutputDir, IActionGraphBuilder Graph)
        {
			return null;
        }

        public override FileItem LinkFiles(LinkEnvironment LinkEnvironment, bool bBuildImportLibraryOnly, IActionGraphBuilder Graph)
		{
			throw new BuildException("Unable to link with PVS toolchain.");
		}

		public override void FinalizeOutput(ReadOnlyTargetRules Target, TargetMakefile Makefile)
		{
			FileReference OutputFile;
			if (Target.ProjectFile == null)
			{
				OutputFile = FileReference.Combine(BuildTool.EngineDirectory, "Saved", "PVS-Studio", String.Format("{0}.pvslog", Target.Name));
			}
			else
			{
				OutputFile = FileReference.Combine(Target.ProjectFile.Directory, "Saved", "PVS-Studio", String.Format("{0}.pvslog", Target.Name));
			}

			List<FileReference> InputFiles = Makefile.OutputItems.Select(x => x.FileDirectory).Where(x => x.HasExtension(".pvslog")).ToList();

			// Collect the prerequisite items off of the Compile action added in CompileCPPFiles so that in SingleFileCompile mode the PVSGather step is also not filtered out
			List<FileItem> AnalyzeActionPrerequisiteItems = Makefile.Actions.Where(x => x.Type == ActionType.Compile).SelectMany(x => x.PrerequisiteItems).ToList();

			FileItem InputFileListItem = Makefile.CreateIntermediateTextFile(OutputFile.ChangeExtension(".input"), InputFiles.Select(x => x.FullName));

			Action AnalyzeAction = Makefile.CreateAction(ActionType.Compile);
			AnalyzeAction.CommandPath      = BuildTool.GetBuildToolAssemblyPath();
			AnalyzeAction.CommandArguments = String.Format("-Mode=PVSGather -Input=\"{0}\" -Output=\"{1}\"", InputFileListItem.FileDirectory, OutputFile);
			AnalyzeAction.WorkingDirectory = BuildTool.EngineSourceDirectory;
			AnalyzeAction.PrerequisiteItems.Add(InputFileListItem);
			AnalyzeAction.PrerequisiteItems.AddRange(Makefile.OutputItems);
			AnalyzeAction.PrerequisiteItems.AddRange(AnalyzeActionPrerequisiteItems);
			AnalyzeAction.ProducedItems.Add(FileItem.GetItemByFileReference(OutputFile));
			AnalyzeAction.DeleteItems.AddRange(AnalyzeAction.ProducedItems);

			Makefile.OutputItems.AddRange(AnalyzeAction.ProducedItems);
		}
	}
}
