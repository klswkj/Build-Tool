using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BuildToolUtilities;

// # Keep Sync with ModuleDescriptor(C++ Class) in EngineSourcecode at PROJECTS_API.

namespace BuildTool
{
	// The type of host that can load a module
	public enum ModuleHostType
	{
		Default = 0,
		Runtime,                // Any target using the runtime
		RuntimeNoCommandlet,    // Any target except for commandlet
		RuntimeAndProgram,      // Any target or program
		CookedOnly,             // Loaded only in cooked builds
		UncookedOnly,           // Loaded only in uncooked builds
		Developer,              // Loaded only when the engine has support for developer tools enabled
		DeveloperTool,          // Loads on any targets where bBuildDeveloperTools is enabled
		Editor,                 // Loaded only by the editor
		EditorNoCommandlet,     // Loaded only by the editor, except when running commandlets
		EditorAndProgram,       // Loaded by the editor or program targets
		Program,                // Loaded only by programs
		ServerOnly,             // Loaded only by servers
		ClientOnly,             // Loaded only by clients, and commandlets, and editor....
		ClientOnlyNoCommandlet, // Loaded only by clients and editor (editor can run PIE which is kinda a commandlet)
	}

	// Indicates when the engine should attempt to load this module
	public enum ModuleLoadingPhase
	{
		Default,               // Loaded at the default loading point during startup (during engine init, after game modules are loaded.)
		PostDefault,           // Right after the default phase
		PreDefault,            // Right before the default phase
		EarliestPossible,      // Loaded as soon as plugins can possibly be loaded (need GConfig)
		PostConfigInit,        // Loaded before the engine is fully initialized, immediately after the config system has been initialized.  Necessary only for very low-level hooks
		PostSplashScreen,      // The first screen to be rendered after system splash screen
		PreEarlyLoadingScreen, // After PostConfigInit and before coreUobject initialized.
							   //  used for early boot loading screens before the uobjects are initialized
		PreLoadingScreen,      // Loaded before the engine is fully initialized for modules that need to hook into the loading screen before it triggers
		PostEngineInit,        // After the engine has been initialized
		None,                  // Do not automatically load this module
	}

	// Class containing information about a code module
	[DebuggerDisplay("Name={Name}")]
	public sealed class ModuleDescriptor
	{
		public readonly string ModuleName;

		public ModuleHostType Type; // Type of target that can host this module
		public ModuleLoadingPhase LoadingPhase = ModuleLoadingPhase.Default; // When should the module be loaded during the startup sequence?  This is sort of an advanced setting.
		public List<BuildTargetPlatform> WhitelistPlatforms; // List of allowed platforms
		public List<BuildTargetPlatform> BlacklistPlatforms; // List of disallowed platforms
		public TargetType[] WhitelistTargets; // List of allowed targets
		public TargetType[] BlacklistTargets; // List of disallowed targets
		public TargetConfiguration[] WhitelistTargetConfigurations; // List of allowed target configurations
		public TargetConfiguration[] BlacklistTargetConfigurations; // List of disallowed target configurations
		public string[] WhitelistPrograms; // List of allowed programs
		public string[] BlacklistPrograms; // List of disallowed programs
		public string[] AdditionalDependencies; // List of additional dependencies for building this module.

		/*
		private readonly string NameTag                          = nameof(ModuleName);
		private readonly string TypeTag                          = nameof(Type);
		private readonly string LoadingPhaseTag                  = nameof(LoadingPhase);
		private readonly string WhitelistPlatformsTag            = nameof(WhitelistPlatforms);
		private readonly string BlacklistPlatformsTag            = nameof(BlacklistPlatforms);
		private readonly string WhitelistTargetsTag              = nameof(WhitelistTargets);
		private readonly string BlacklistTargetsTag              = nameof(BlacklistTargets);
		private readonly string WhitelistTargetConfigurationsTag = nameof(WhitelistTargetConfigurations);
		private readonly string BlacklistTargetConfigurationsTag = nameof(BlacklistTargetConfigurations);
		private readonly string WhitelistProgramsTag             = nameof(WhitelistPrograms);
		private readonly string BlacklistProgramsTag             = nameof(BlacklistPrograms);
		private readonly string AdditionalDependenciesTag        = nameof(AdditionalDependencies);
		*/

		public ModuleDescriptor(string InModuleName, ModuleHostType InTargetType)
		{
			ModuleName = InModuleName;
			Type       = InTargetType;
		}

		public static ModuleDescriptor FromJsonObject(JsonObject InObject)
		{
			ModuleDescriptor OutModuleDescriptor = new ModuleDescriptor(InObject.GetStringField(nameof(ModuleDescriptor.ModuleName)), InObject.GetEnumField<ModuleHostType>(nameof(ModuleDescriptor.Type)));

			if (InObject.TryGetEnumField<ModuleLoadingPhase>(nameof(ModuleDescriptor.LoadingPhase), out ModuleLoadingPhase LoadingPhase))
			{
				OutModuleDescriptor.LoadingPhase = LoadingPhase;
			}

			try
			{
				if (InObject.TryGetStringArrayField(nameof(ModuleDescriptor.WhitelistPlatforms), out string[] WhitelistPlatforms))
				{
					OutModuleDescriptor.WhitelistPlatforms = Array.ConvertAll(WhitelistPlatforms, x => BuildTargetPlatform.Parse(x)).ToList();
				}

				if (InObject.TryGetStringArrayField(nameof(ModuleDescriptor.BlacklistPlatforms), out string[] BlacklistPlatforms))
				{
					OutModuleDescriptor.BlacklistPlatforms = Array.ConvertAll(BlacklistPlatforms, x => BuildTargetPlatform.Parse(x)).ToList();
				}
			}
			catch (BuildException Ex)
			{
				ExceptionUtils.AddContext(Ex, "while parsing module descriptor '{0}'", OutModuleDescriptor.ModuleName);
				throw;
			}

			if (InObject.TryGetEnumArrayField<TargetType>(nameof(ModuleDescriptor.WhitelistTargets), out TargetType[] WhitelistTargets))
			{
				OutModuleDescriptor.WhitelistTargets = WhitelistTargets;
			}

			if (InObject.TryGetEnumArrayField<TargetType>(nameof(ModuleDescriptor.BlacklistTargets), out TargetType[] BlacklistTargets))
			{
				OutModuleDescriptor.BlacklistTargets = BlacklistTargets;
			}

			if (InObject.TryGetEnumArrayField<TargetConfiguration>(nameof(ModuleDescriptor.WhitelistTargetConfigurations), out TargetConfiguration[] WhitelistTargetConfigurations))
			{
				OutModuleDescriptor.WhitelistTargetConfigurations = WhitelistTargetConfigurations;
			}

			if (InObject.TryGetEnumArrayField<TargetConfiguration>(nameof(ModuleDescriptor.BlacklistTargetConfigurations), out TargetConfiguration[] BlacklistTargetConfigurations))
			{
				OutModuleDescriptor.BlacklistTargetConfigurations = BlacklistTargetConfigurations;
			}

			if (InObject.TryGetStringArrayField(nameof(ModuleDescriptor.WhitelistPrograms), out string[] WhitelistPrograms))
			{
				OutModuleDescriptor.WhitelistPrograms = WhitelistPrograms;
			}

			if (InObject.TryGetStringArrayField(nameof(ModuleDescriptor.BlacklistPrograms), out string[] BlacklistPrograms))
			{
				OutModuleDescriptor.BlacklistPrograms = BlacklistPrograms;
			}

			if (InObject.TryGetStringArrayField(nameof(ModuleDescriptor.AdditionalDependencies), out string[] AdditionalDependencies))
			{
				OutModuleDescriptor.AdditionalDependencies = AdditionalDependencies;
			}

			return OutModuleDescriptor;
		}

		// Write this module to a JsonWriter
		void Write(JsonWriter Writer)
		{
			Writer.WriteObjectStart();
			Writer.WriteValue(nameof(ModuleName), ModuleName);
			Writer.WriteValue(nameof(Type), Type.ToString());
			Writer.WriteValue(nameof(LoadingPhase), LoadingPhase.ToString());
			if (WhitelistPlatforms != null && 0 < WhitelistPlatforms.Count)
			{
				Writer.WriteArrayStart(nameof(WhitelistPlatforms));
				foreach (BuildTargetPlatform WhitelistPlatform in WhitelistPlatforms)
				{
					Writer.WriteValue(WhitelistPlatform.ToString());
				}
				Writer.WriteArrayEnd();
			}
			if (BlacklistPlatforms != null && 0 < BlacklistPlatforms.Count)
			{
				Writer.WriteArrayStart(nameof(BlacklistPlatforms));
				foreach (BuildTargetPlatform BlacklistPlatform in BlacklistPlatforms)
				{
					Writer.WriteValue(BlacklistPlatform.ToString());
				}
				Writer.WriteArrayEnd();
			}
			if (WhitelistTargets != null && 0 < WhitelistTargets.Length)
			{
				Writer.WriteArrayStart(nameof(WhitelistTargets));
				foreach (TargetType WhitelistTarget in WhitelistTargets)
				{
					Writer.WriteValue(WhitelistTarget.ToString());
				}
				Writer.WriteArrayEnd();
			}
			if (BlacklistTargets != null && 0 < BlacklistTargets.Length)
            {
                Writer.WriteArrayStart(nameof(BlacklistTargets));
                foreach (TargetType BlacklistTarget in BlacklistTargets)
                {
                    Writer.WriteValue(BlacklistTarget.ToString());
                }
                Writer.WriteArrayEnd();
            }
			if (WhitelistTargetConfigurations != null && 0 < WhitelistTargetConfigurations.Length)
            {
                Writer.WriteArrayStart(nameof(WhitelistTargetConfigurations));
                foreach (TargetConfiguration WhitelistTargetConfiguration in WhitelistTargetConfigurations)
                {
                    Writer.WriteValue(WhitelistTargetConfiguration.ToString());
                }
                Writer.WriteArrayEnd();
            }
			if (BlacklistTargetConfigurations != null && 0 < BlacklistTargetConfigurations.Length)
            {
                Writer.WriteArrayStart(nameof(BlacklistTargetConfigurations));
                foreach (TargetConfiguration BlacklistTargetConfiguration in BlacklistTargetConfigurations)
                {
                    Writer.WriteValue(BlacklistTargetConfiguration.ToString());
                }
                Writer.WriteArrayEnd();
            }
			if(WhitelistPrograms != null && 0 < WhitelistPrograms.Length)
			{
				Writer.WriteStringArrayField(nameof(WhitelistPrograms), WhitelistPrograms);
			}
			if(BlacklistPrograms != null && 0 < BlacklistPrograms.Length)
			{
				Writer.WriteStringArrayField(nameof(BlacklistPrograms), BlacklistPrograms);
			}
			if (AdditionalDependencies != null && 0 < AdditionalDependencies.Length)
			{
				Writer.WriteArrayStart(nameof(AdditionalDependencies));
				foreach (string AdditionalDependency in AdditionalDependencies)
				{
					Writer.WriteValue(AdditionalDependency);
				}
				Writer.WriteArrayEnd();
			}
			Writer.WriteObjectEnd();
		}

		// Write an array of module descriptors
		public static void WriteArray(JsonWriter Writer, string ArrayName, ModuleDescriptor[] Modules)
		{
			if (Modules != null && 0 < Modules.Length)
			{
				Writer.WriteArrayStart(ArrayName);
				foreach (ModuleDescriptor Module in Modules)
				{
					Module.Write(Writer);
				}
				Writer.WriteArrayEnd();
			}
		}

		// Produces any warnings and errors for the module settings
		public void Validate(FileReference ModuleDeclarationFile)
		{
			if(Type == ModuleHostType.Developer)
			{
				Log.TraceWarningOnce("The 'Developer' module type has been deprecated in 4.24. Use 'DeveloperTool' for modules that can be loaded by game/client/server targets in non-shipping configurations, or 'UncookedOnly' for modules that should only be loaded by uncooked editor and program targets (eg. modules containing blueprint nodes)");
				Log.TraceWarningOnce(ModuleDeclarationFile, "The 'Developer' module type has been deprecated in 4.24.");
			}
		}

		// Determines whether the given plugin module is part of the current build.
		public bool IsCompiledInConfiguration
		(
			BuildTargetPlatform CompiledPlatform,
			TargetConfiguration CompiledConfiguration,
			string BuildTargetName,
			TargetType CompiledTargetType,
			bool bBuildDeveloperTools, // Whether the configuration includes developer tools (typically UEBuildConfiguration.bBuildDeveloperTools for UBT callers)
			bool bBuildRequiresCookedData // Whether the configuration requires cooked content (typically UEBuildConfiguration.bBuildRequiresCookedData for UBT callers)
		)
		{
			// Check the platform is whitelisted
			if (WhitelistPlatforms != null   && 
				0 < WhitelistPlatforms.Count && 
				!WhitelistPlatforms.Contains(CompiledPlatform))
			{
				return false;
			}

			// Check the platform is not blacklisted
			if (BlacklistPlatforms != null && 
				BlacklistPlatforms.Contains(CompiledPlatform))
			{
				return false;
			}

			// Check the target is whitelisted
			if (WhitelistTargets != null    && 
				0 < WhitelistTargets.Length && 
				!WhitelistTargets.Contains(CompiledTargetType))
			{
				return false;
			}

			// Check the target is not blacklisted
			if (BlacklistTargets != null && 
				BlacklistTargets.Contains(CompiledTargetType))
			{
				return false;
			}

			// Check the target configuration is whitelisted
			if (WhitelistTargetConfigurations != null    && 
				0 < WhitelistTargetConfigurations.Length && 
				!WhitelistTargetConfigurations.Contains(CompiledConfiguration))
			{
				return false;
			}

			// Check the target configuration is not blacklisted
			if (BlacklistTargetConfigurations != null && 
				BlacklistTargetConfigurations.Contains(CompiledConfiguration))
			{
				return false;
			}

			// Special checks just for programs
			if(CompiledTargetType == TargetType.Program)
			{
				// Check the program name is whitelisted. Note that this behavior is slightly different to other whitelist/blacklist checks; we will whitelist a module of any type if it's explicitly allowed for this program.
				if(WhitelistPrograms != null && 
					0 < WhitelistPrograms.Length)
				{
					return WhitelistPrograms.Contains(BuildTargetName);
				}
				
				// Check the program name is not blacklisted
				if(BlacklistPrograms != null && 
				   BlacklistPrograms.Contains(BuildTargetName))
				{
					return false;
				}
			}

			// Check the module is compatible with this target.
			switch (Type)
			{
				case ModuleHostType.Runtime:
				case ModuleHostType.RuntimeNoCommandlet:
                    return CompiledTargetType != TargetType.Program;
				case ModuleHostType.RuntimeAndProgram:
					return true;
				case ModuleHostType.CookedOnly:
                    return bBuildRequiresCookedData;
				case ModuleHostType.UncookedOnly:
					return !bBuildRequiresCookedData;
				case ModuleHostType.Developer:
					return CompiledTargetType == TargetType.Editor || CompiledTargetType == TargetType.Program;
				case ModuleHostType.DeveloperTool:
					return bBuildDeveloperTools;
				case ModuleHostType.Editor:
				case ModuleHostType.EditorNoCommandlet:
					return CompiledTargetType == TargetType.Editor;
				case ModuleHostType.EditorAndProgram:
					return CompiledTargetType == TargetType.Editor || CompiledTargetType == TargetType.Program;
				case ModuleHostType.Program:
					return CompiledTargetType == TargetType.Program;
                case ModuleHostType.ServerOnly:
                    return CompiledTargetType != TargetType.Program && CompiledTargetType != TargetType.Client;
                case ModuleHostType.ClientOnly:
                case ModuleHostType.ClientOnlyNoCommandlet:
                    return CompiledTargetType != TargetType.Program && CompiledTargetType != TargetType.Server;
            }

			return false;
		}
	}
}
