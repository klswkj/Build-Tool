using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Reflection;
using BuildToolUtilities;

// UEBulidTarget ┬─── TargetRules
//               ├─── ReulesAssembly 
//               ├─── UProjectDescriptor
//               ├─── List<UEBuildPlugins> EnabledPlugins
//               ├─── List<UEBuildPlugins> BuildPlugins
//               ├─── List<UEBuildBinaries> Binaries
//               ├─── List<UEBuildBinary> NonFilteredModules
//               ├─── List<UEBuildBinary> NonFilteredModules
//               ├─── Dictionary<string, UEBuildModule> Modules
//               └─── bool 
namespace BuildTool
{
	// A container for a binary files (dll, exe) with its associated debug info.
	public sealed class BuildManifest
	{
		public readonly List<string> BuildProducts = new List<string>();
		public readonly List<string> DeployTargetFiles = new List<string>();
		public BuildManifest()
		{
		}

		public void AddBuildProduct(string FileName)
		{
			string FullFileName = Path.GetFullPath(FileName);
			if (!BuildProducts.Contains(FullFileName))
			{
				BuildProducts.Add(FullFileName);
			}
		}

		public void AddBuildProduct(string FileName, string DebugInfoExtension)
		{
			AddBuildProduct(FileName);
			if (!String.IsNullOrEmpty(DebugInfoExtension))
			{
				AddBuildProduct(Path.ChangeExtension(FileName, DebugInfoExtension));
			}
		}
	}

	// A target that can be built
	internal class BuildTarget
	{
		// UEBuildTarget::Create(..., ..., ...) 중에 레퍼런스가 5개, 그 중에
		// BuildMode.cs(494) ProjectFileGenerator.cs(1581) 두개 추적, 그 외3개는 외부에서 불러오는거인듯.

		// All application binaries; may include binaries not built by this target.
		public List<BuildBinary> AllApplicationBuildBinaries = new List<BuildBinary>();

		public ReadOnlyTargetRules     Rules;             // The target rules (For Launch Module)
		public RulesAssembly           RulesAssembly;     // The rules assembly to use when searching for modules
		public SourceFileMetadataCache MetadataCache;     // Cache of source file metadata for this target
		public FileReference           ProjectFile;       // The project file for this target
		public UProjectDescriptor      ProjectDescriptor; // The project descriptor for this target
		public TargetType              TargetType;        // Type of target

		public string AppName;    // For targets with bUseSharedBuildEnvironment = true, this is typically the name of the base application
		public string TargetName; // The name of the target

		// Platform as defined by the VCProject and passed via the command line. Not the same as internal config names.
		public BuildTargetPlatform TargetPlatform;

		// Target as defined by the VCProject and passed via the command line. Not necessarily the same as internal name.
		public TargetConfiguration Configuration;

		public bool bUseSharedBuildEnvironment; // If false, AppName == TargetName and all binaries should be written to the project directory.

		public string Architecture; // The architecture this target is being built for
		public string PlatformIntermediateFolder; // Relative path for platform-specific intermediates (eg. Intermediate/Build/Win64)

		public DirectoryReference ProjectDirectory;             // Typically contains the .uproject file, or the engine root.
		public DirectoryReference ProjectIntermediateDirectory; // Typically underneath ProjectDirectory. Default directory for intermediate files. 

		// For an agnostic editor/game executable, this will be under the engine directory. 
		// For monolithic executables this will be the same as the project intermediate directory.
		public DirectoryReference EngineIntermediateDirectory;

		public List<BuildPlugin> BuildPlugins;
		public List<BuildPlugin> EnabledPlugins; // This differs from the list of plugins that is built for Launcher, where we build everything, but link in only the enabled plugins.
		public FileReference     ForeignPlugin; // Specifies the path to a specific plugin to compile.

		// Identifies whether the project contains a script plugin. 
		// This will cause UHT to be rebuilt, even in installed builds.
		public bool bHasProjectScriptPlugin;

		private readonly bool bCompileMonolithic = false; // true if target should be compiled in monolithic mode, false if not

		// Used to keep track of all modules by name.
		private readonly Dictionary<string, BuildModule> Modules = new Dictionary<string, BuildModule>(StringComparer.InvariantCultureIgnoreCase);

		public static FileReference   DefaultResourceDirectory;             // FileReference.Combine(BuildTool.EngineDirectory, "Build", "Windows", "Resources", "Default.rc2");
		public          FileReference ReceiptFileName { get; private set; } // Filename for the receipt for this target.
		public readonly FileReference TargetRulesFile;                      // The name of the .Target.cs file, if the target was created with one
		public bool                   bDeployAfterCompile;                  // Whether to deploy this target after compilation

		// Whether this target should be compiled in monolithic mode
		// TODO : Need to make sure this function and similar things aren't called in assembler mode
		public bool ShouldCompileMonolithic() => bCompileMonolithic;

		// Normalize an include path to be relative to the engine source directory
		public static string NormalizeIncludePath(DirectoryReference Directory)
		{
			return StringUtils.CleanDirectorySeparators(Directory.MakeRelativeTo(BuildTool.EngineSourceDirectory), '/');
		}

        private BuildTarget(BuildTargetDescriptor InDescriptor, ReadOnlyTargetRules InRules, RulesAssembly InRulesAssembly)
		{
			MetadataCache       = SourceFileMetadataCache.CreateHierarchy(InDescriptor.ProjectFile);
			ProjectFile         = InDescriptor.ProjectFile;
			AppName             = InDescriptor.Name;
			TargetName          = InDescriptor.Name;
			TargetPlatform      = InDescriptor.Platform;
			Configuration       = InDescriptor.Configuration;
			Architecture        = InDescriptor.Architecture;
			Rules               = InRules;
			RulesAssembly       = InRulesAssembly;
			TargetType          = Rules.Type;
			ForeignPlugin       = InDescriptor.ForeignPlugin;
			bDeployAfterCompile = InRules.bDeployAfterCompile && !InRules.bDisableLinking && InDescriptor.SingleFileToCompile == null;

			// now that we have the platform, we can set the intermediate path to include the platform/architecture name
			PlatformIntermediateFolder = BuildTool.GetPlatformGeneratedFolder(TargetPlatform, Architecture);

			TargetRulesFile = InRules.File;

			bCompileMonolithic = (Rules.LinkType == TargetLinkType.Monolithic);

			// Set the build environment
			bUseSharedBuildEnvironment = (Rules.BuildEnvironment == TargetBuildEnvironment.Shared);
			if (bUseSharedBuildEnvironment)
			{
				if(Rules.Type == TargetType.Program)
				{
					AppName = TargetName;
				}
				else
				{
					AppName = GetAppNameForTargetType(Rules.Type);
				}
			}

			// Figure out what the project directory is. If we have a uproject file, use that. Otherwise use the engine directory.
			if (ProjectFile != null)
			{
				ProjectDirectory = ProjectFile.Directory;
			}
			else if (Rules.File.IsUnderDirectory(BuildTool.EnterpriseDirectory))
			{
				ProjectDirectory = BuildTool.EnterpriseDirectory;
			}
			else
			{
				ProjectDirectory = BuildTool.EngineDirectory;
			}

			// Build the project intermediate directory
			ProjectIntermediateDirectory = DirectoryReference.Combine(ProjectDirectory, PlatformIntermediateFolder, TargetName, Configuration.ToString());

			// Build the engine intermediate directory.
			// If we're building agnostic engine binaries, we can use the engine intermediates folder.
			// Otherwise we need to use the project intermediates directory.
			if (!bUseSharedBuildEnvironment)
			{
				EngineIntermediateDirectory = ProjectIntermediateDirectory;
			}
			else if (Configuration == TargetConfiguration.DebugGame)
			{
				EngineIntermediateDirectory = DirectoryReference.Combine(BuildTool.EngineDirectory, PlatformIntermediateFolder, AppName, TargetConfiguration.Development.ToString());
			}
			else
			{
				EngineIntermediateDirectory = DirectoryReference.Combine(BuildTool.EngineDirectory, PlatformIntermediateFolder, AppName, Configuration.ToString());
			}

			// Get the receipt path for this target
			// D:\UERelease\Engine\Binaries\Win64\DefaultGame-Win64-Debug.target
			ReceiptFileName = TargetReceipt.GetDefaultPath(ProjectDirectory, TargetName, TargetPlatform, Configuration, Architecture);

			// Read the project descriptor
			if (ProjectFile != null)
			{
				ProjectDescriptor = UProjectDescriptor.FromFile(ProjectFile);
			}

			if(BuildTarget.DefaultResourceDirectory == null)
			{
				FileReference ResourcePath = FileReference.Combine
				(
                    BuildTool.EngineDirectory,
                    Tag.Directory.Build,
                    Tag.Directory.Windows,
                    Tag.Directory.Resources,
                    Tag.Binary.DefaultResource + Tag.Ext.RC2
				);

				if(FileReference.Exists(ResourcePath))
				{
					BuildTarget.DefaultResourceDirectory = ResourcePath;
				}
#if DEBUG
				else
				{
					new BuildException(ResourcePath.FullName + "doesn't exist.");
				}
#endif
			}
		}

		#region STATIC_FUNCTIONS

		// Creates a target object for the specified target name.
		public static BuildTarget CreateNewBuildTarget(BuildTargetDescriptor InDescriptor, bool bSkipRulesCompile, bool bUsePrecompiledEngineEnterpriseBuild)
		{
			// make sure we are allowed to build this platform
			if (!BuildPlatform.IsPlatformAvailable(InDescriptor.Platform))
			{
				throw new BuildException("Platform {0} is not a valid platform to build. Check that the SDK is installed properly.", InDescriptor.Platform);
			}

			RulesAssembly RulesAssembly;

            // ProgramRules.dll(*.Target.cs 70여개 모아놓은거)이고, Parent는 EngineRules.dll(*.Build.cs 1113개 모아놓은거)
            // GenerateFiles모드에서는 PromgramRules
            RulesAssembly = RulesCompiler.CreateTargetRulesAssembly
			(
                InDescriptor.ProjectFile,
                InDescriptor.Name,
                bSkipRulesCompile,
                bUsePrecompiledEngineEnterpriseBuild,
                InDescriptor.ForeignPlugin
			);

            TargetRules TargetRules_cs_Object;

            TargetRules_cs_Object = RulesAssembly.CreateTargetRules(InDescriptor.Name, InDescriptor.Platform, InDescriptor.Configuration, InDescriptor.Architecture, InDescriptor.ProjectFile, InDescriptor.AdditionalArguments);

            #region VALID_CHECK
#if RELEASE
			if ((ProjectFileGenerator.bGenerateProjectFiles == false) && !TargetRules_cs_Object.GetSupportedPlatforms().Contains(InDescriptor.Platform))
			{
				throw new BuildException("{0} does not support the {1} platform.", InDescriptor.Name, InDescriptor.Platform.ToString());
			}
			// Make sure this configuration is supports
			if (!TargetRules_cs_Object.GetSupportedConfigurations().Contains(InDescriptor.Configuration))
			{
				throw new BuildException("{0} does not support the {1} configuration", InDescriptor.Name, InDescriptor.Configuration);
			}
			// Make sure this target type is supported. Allow UHT in installed builds as a special case for now.
			if (!InstalledPlatformInfo.IsValid(TargetRules_cs_Object.Type, InDescriptor.Platform, InDescriptor.Configuration, EProjectType.Code, InstalledPlatformState.Downloaded) && InDescriptor.Name != "HeaderTool")
			{
				if (InstalledPlatformInfo.IsValid(TargetRules_cs_Object.Type, InDescriptor.Platform, InDescriptor.Configuration, EProjectType.Code, InstalledPlatformState.Supported))
				{
					throw new BuildException("Download support for building {0} {1} targets from the launcher.", InDescriptor.Platform, TargetRules_cs_Object.Type);
				}
				else if (!InstalledPlatformInfo.IsValid(TargetRules_cs_Object.Type, null, null, EProjectType.Code, InstalledPlatformState.Supported))
				{
					throw new BuildException("{0} targets are not currently supported from this engine distribution.", TargetRules_cs_Object.Type);
				}
				else if (!InstalledPlatformInfo.IsValid(TargetRules_cs_Object.Type, TargetRules_cs_Object.Platform, null, EProjectType.Code, InstalledPlatformState.Supported))
				{
					throw new BuildException("The {0} platform is not supported from this engine distribution.", TargetRules_cs_Object.Platform);
				}
				else
				{
					throw new BuildException("Targets cannot be built in the {0} configuration with this engine distribution.", TargetRules_cs_Object.Configuration);
				}
			}
			// If we're using the shared build environment, make sure all the settings are valid
			if (TargetRules_cs_Object.BuildEnvironment == TargetBuildEnvironment.Shared)
			{
				ValidateSharedEnvironment(RulesAssembly, InDescriptor.Name, InDescriptor.AdditionalArguments, TargetRules_cs_Object);
			}
#endif
            #endregion VALID_CHECK

            // If we're precompiling, generate a list of all the files that we depend on
            if (TargetRules_cs_Object.bPrecompile)
			{
				DirectoryReference DependencyListDir;
				if (TargetRules_cs_Object.ProjectFile == null)
				{
					DependencyListDir = DirectoryReference.Combine
					(
                        BuildTool.EngineDirectory,
                        Tag.Directory.Generated,
                        Tag.Directory.DependencyLists,
                        TargetRules_cs_Object.Name,
                        TargetRules_cs_Object.Configuration.ToString(),
                        TargetRules_cs_Object.Platform.ToString()
					);
				}
				else
				{
					DependencyListDir = DirectoryReference.Combine
					(
                        TargetRules_cs_Object.ProjectFile.Directory,
						Tag.Directory.Generated,
						Tag.Directory.DependencyLists,
						TargetRules_cs_Object.Name,
                        TargetRules_cs_Object.Configuration.ToString(),
                        TargetRules_cs_Object.Platform.ToString()
					);
				}

				if (InDescriptor.Architecture.HasValue())
				{
					DependencyListDir = new DirectoryReference(DependencyListDir.FullName + InDescriptor.Architecture);
				}

				FileReference DependencyListFile;

				if (TargetRules_cs_Object.bBuildAllModules)
				{
					DependencyListFile = FileReference.Combine(DependencyListDir, Tag.TxtFileName.DependencyListAllModule);
				}
				else
				{
					DependencyListFile = FileReference.Combine(DependencyListDir, Tag.TxtFileName.DependencyList);
				}

				TargetRules_cs_Object.PrecompileDependencyFiles.Add(DependencyListFile);
			}

			// If we're compiling just a single file, we need to prevent unity builds from running
			if (InDescriptor.SingleFileToCompile != null)
			{
				TargetRules_cs_Object.bUseUnityBuild   = false;
				TargetRules_cs_Object.bForceUnityBuild = false;
				TargetRules_cs_Object.bUsePCHFiles     = false;
				TargetRules_cs_Object.bDisableLinking  = true;
			}

			// If we're compiling a plugin, and this target is monolithic, just create the object files
			if (InDescriptor.ForeignPlugin != null && TargetRules_cs_Object.LinkType == TargetLinkType.Monolithic)
			{
				// Don't actually want an executable
				TargetRules_cs_Object.bDisableLinking = true;

				// Don't allow using shared PCHs; they won't be distributed with the plugin
				TargetRules_cs_Object.bUseSharedPCHs = false;
			}

			// Don't link if we're just preprocessing
			if (TargetRules_cs_Object.bPreprocessOnly)
			{
				TargetRules_cs_Object.bDisableLinking = true;
			}

			// Include generated code plugin if not building an editor target and project is configured for nativization
			FileReference NativizedPluginFile = TargetRules_cs_Object.GetNativizedPlugin();
			if (NativizedPluginFile != null)
			{
				RulesAssembly = RulesCompiler.CreatePluginRulesAssembly(NativizedPluginFile, RulesAssembly, bSkipRulesCompile, false);
			}

			// Generate a build target from this rules module
			BuildTarget OutTarget;

			OutTarget = new BuildTarget(InDescriptor, new ReadOnlyTargetRules(TargetRules_cs_Object), RulesAssembly);
			OutTarget.PreBuildSetup();
			
			return OutTarget;
		}

		// Validates that the build environment matches the shared build environment, 
		// by comparing the TargetRules instance to the vanilla target rules for the current target type.
		public static void ValidateSharedEnvironment
		(
            RulesAssembly        RulesAssembly,
            string               ThisTargetName,
            CommandLineArguments Arguments,
            TargetRules          InTargetRules
		)
		{
			// Allow disabling these checks
			if (InTargetRules.bOverrideBuildEnvironment)
			{
				return;
			}

			// Get the name of the target with default settings
			string BaseTargetName;
			switch (InTargetRules.Type)
			{
				case TargetType.Game:
					BaseTargetName = Tag.Binary.DefaultGame;
					break;
				case TargetType.Editor:
					BaseTargetName = Tag.Binary.DefaultEditor;
					break;
				case TargetType.Client:
					BaseTargetName = Tag.Binary.DefaultClient;
					break;
				case TargetType.Server:
					BaseTargetName = Tag.Binary.DefaultServer;
					break;
				default:
					return;
			}

			// Create the target rules for it
			TargetRules BaseRules = RulesAssembly.CreateTargetRules
			(
                BaseTargetName,
                InTargetRules.Platform,
                InTargetRules.Configuration,
                InTargetRules.Architecture,
                null,
                Arguments
			);

			// Get all the configurable objects
			object[] BaseObjects = BaseRules.GetConfigurableObjects().ToArray();
			object[] ThisObjects = InTargetRules.GetConfigurableObjects().ToArray();

			if (BaseObjects.Length != ThisObjects.Length)
			{
				throw new BuildException("Expected same number of configurable objects from base rules object.");
			}

			// Iterate through all fields with the [SharedBuildEnvironment] attribute
			// [0] = {DefaultGameTarget}
			// [1] = {BuildToolAndroidTargetRules}
			// ...
			// [8] = {BuildToolWindowsTargetRules}
			// ...
			for (int Idx = 0; Idx < BaseObjects.Length; ++Idx)
			{
				Type ObjectType = BaseObjects[Idx].GetType();
				foreach (FieldInfo Field in ObjectType.GetFields())
				{
					if (Field.GetCustomAttribute<RequiresUniqueBuildEnvironmentAttribute>() != null)
					{
						object ThisValue = Field.GetValue(ThisObjects[Idx]);
						object BaseValue = Field.GetValue(BaseObjects[Idx]);
						CheckValuesMatch(InTargetRules.GetType(), ThisTargetName, BaseTargetName, Field.Name, Field.FieldType, ThisValue, BaseValue);
					}
				}
				foreach (PropertyInfo Property in ObjectType.GetProperties())
				{
					if (Property.GetCustomAttribute<RequiresUniqueBuildEnvironmentAttribute>() != null)
					{
						object ThisValue = Property.GetValue(ThisObjects[Idx]);
						object BaseValue = Property.GetValue(BaseObjects[Idx]);
						CheckValuesMatch(InTargetRules.GetType(), ThisTargetName, BaseTargetName, Property.Name, Property.PropertyType, ThisValue, BaseValue);
					}
				}
			}

			// Make sure that we don't explicitly enable or disable any plugins through the target rules. We can't do this with the shared build environment because it requires recompiling the "Projects" engine module.
			if (0 < InTargetRules.EnablePlugins.Count ||
				0 < InTargetRules.DisablePlugins.Count)
			{
				throw new BuildException("Explicitly enabling and disabling plugins for a target is only supported when using a unique build environment (eg. for monolithic game targets).");
			}
		}

		// Check that two values match between a base and derived rules type
		private static void CheckValuesMatch
		(
            Type   RulesType,
            string ThisTargetName,
            string BaseTargetName,
            string FieldName,
            Type   ValueType,
            object ThisValue,
            object BaseValue
		)
		{
			// Check if the fields match, treating lists of strings (eg. definitions) differently to value types.
			bool bFieldsMatch;
			if (ThisValue == null || BaseValue == null)
			{
				bFieldsMatch = (ThisValue == BaseValue);
			}
			else if (typeof(IEnumerable<string>).IsAssignableFrom(ValueType))
			{
				bFieldsMatch = Enumerable.SequenceEqual((IEnumerable<string>)ThisValue, (IEnumerable<string>)BaseValue);
			}
			else
			{
				bFieldsMatch = ThisValue.Equals(BaseValue);
			}

			// Throw an exception if they don't match
			if (!bFieldsMatch)
			{
				throw new BuildException("{0} modifies the value of {1}. This is not allowed, as {0} has build products in common with {2}.\nRemove the modified setting, change {0} to use a unique build environment by setting 'BuildEnvironment = TargetBuildEnvironment.Unique;' in the {3} constructor, or set bOverrideBuildEnvironment = true to force this setting on.", ThisTargetName, FieldName, BaseTargetName, RulesType.Name);
			}
		}

		// Gets the app name for a given target type
		public static string GetAppNameForTargetType(TargetType Type)
		{
#warning Look-up Table Issue.
			switch (Type)
			{
				case TargetType.Game:
					return Tag.Binary.DefaultGame;
				case TargetType.Client:
					return Tag.Binary.DefaultClient;
				case TargetType.Server:
					return Tag.Binary.DefaultServer;
				case TargetType.Editor:
					return Tag.Binary.DefaultEditor;
				default:
					throw new BuildException("Invalid target type ({0})", (int)Type);
			}
		}

		#endregion STATIC_FUNCTIONS

		// Writes a list of all the externally referenced files required to use the precompiled data for this target
		private void WriteDependencyList
		(
            FileReference DependencyLocation,
            List<RuntimeDependency> RuntimeDependencies,
            Dictionary<FileReference, FileReference> RuntimeDependencyTargetFileToSourceFile // Map of runtime dependencies to their location in the source tree, before they are staged
		)
		{
			HashSet<FileReference> Files = new HashSet<FileReference>();

			// Find all the runtime dependency files in their original location
			foreach(RuntimeDependency RuntimeDependency in RuntimeDependencies)
			{
				if (!RuntimeDependencyTargetFileToSourceFile.TryGetValue(RuntimeDependency.Path, out FileReference SourceFile))
				{
					SourceFile = RuntimeDependency.Path;
				}
				if (RuntimeDependency.Type != StagedFileType.RawDebugFile || FileReference.Exists(SourceFile))
				{
					Files.Add(SourceFile);
				}
			}

			// Figure out all the modules referenced by this target.
			// This includes all the modules that are referenced, not just the ones compiled into binaries.
			HashSet<BuildModule> Modules = new HashSet<BuildModule>();
			foreach (BuildBinary Binary in AllApplicationBuildBinaries)
			{
				foreach(BuildModule Module in Binary.LinkTogetherModules)
				{
					Modules.Add(Module);
					Modules.UnionWith(Module.GetAllModules(true, true));
				}
			}

			// Get the platform we're building for
			BuildPlatform BuildPlatform = BuildPlatform.GetBuildPlatform(TargetPlatform);
			foreach (BuildModule Module in Modules)
			{
				// Skip artificial modules
				if(Module.RulesFile == null)
				{
					continue;
				}

				// Create the module rules
				ModuleRules Rules = CreateModuleRulesAndSetDefaults(Module.ModuleRuleFileName, "external file list option");

				// Add Additional Bundle Resources for all modules
				foreach (ModuleRules.BundleResource Resource in Rules.AdditionalBundleResources)
				{
					if (Directory.Exists(Resource.ResourcePath))
					{
						Files.UnionWith(DirectoryReference.EnumerateFiles(new DirectoryReference(Resource.ResourcePath), "*", SearchOption.AllDirectories));
					}
					else
					{
						Files.Add(new FileReference(Resource.ResourcePath));
					}
				}

				// Add any zip files from Additional Frameworks
				foreach (ModuleRules.Framework Framework in Rules.PublicAdditionalFrameworks)
				{
					if (!String.IsNullOrEmpty(Framework.ZipPath))
					{
						Files.Add(FileReference.Combine(Module.ModuleDirectory, Framework.ZipPath));
					}
				}

				// Add the rules file itself
				Files.Add(Rules.File);

				// Add the subclass rules
				if (Rules.SubclassRules != null)
				{
					foreach (string SubclassRule in Rules.SubclassRules)
					{
						Files.Add(new FileReference(SubclassRule));
					}
				}

				// Get a list of all the library paths
				List<string> LibraryPaths = new List<string> { Directory.GetCurrentDirectory() };

				List<string> SystemLibraryPaths = new List<string> { Directory.GetCurrentDirectory() };
				SystemLibraryPaths.AddRange(Rules.PublicSystemLibraryPaths.Where(x => !x.StartsWith("$(")).Select(x => Path.GetFullPath(x.Replace('/', Path.DirectorySeparatorChar))));

				// Get all the extensions to look for
				List<string> LibraryExtensions = new List<string>
				{
					BuildPlatform.GetBinaryExtension(BuildBinaryType.StaticLibrary),
					BuildPlatform.GetBinaryExtension(BuildBinaryType.DynamicLinkLibrary)
				};

				// Add all the libraries
				foreach (string LibraryExtension in LibraryExtensions)
				{
					foreach (string LibraryName in Rules.PublicAdditionalLibraries)
					{
						foreach (string LibraryPath in LibraryPaths)
						{
							ResolveLibraryName(LibraryPath, LibraryName, LibraryExtension, Files);
						}
				    }

					foreach (string LibraryName in Rules.PublicSystemLibraryPaths)
					{
						foreach (string LibraryPath in SystemLibraryPaths)
						{
							ResolveLibraryName(LibraryPath, LibraryName, LibraryExtension, Files);
						}
					}
				}

				// Find all the include paths
				List<string> AllIncludePaths = new List<string>();
				AllIncludePaths.AddRange(Rules.PublicIncludePaths);
				AllIncludePaths.AddRange(Rules.PublicSystemIncludePaths);

				// Add all the include paths
				foreach (string IncludePath in AllIncludePaths.Where(x => !x.StartsWith("$(")))
				{
					if (Directory.Exists(IncludePath))
					{
						foreach (string IncludeFileName in Directory.EnumerateFiles(IncludePath, "*", SearchOption.AllDirectories))
						{
							string Extension = Path.GetExtension(IncludeFileName).ToLower();
							if (Extension == Tag.Ext.Header || 
								Extension == Tag.Ext.Inline || 
								Extension == Tag.Ext.CppHeader)
							{
								Files.Add(new FileReference(IncludeFileName));
							}
						}
					}
				}
			}

			// Write the file
			Log.TraceInformation("Writing dependency list to {0}", DependencyLocation);
			DirectoryReference.CreateDirectory(DependencyLocation.Directory);
			FileReference.WriteAllLines(DependencyLocation, Files.Where(x => x.IsUnderDirectory(BuildTool.RootDirectory)).Select(x => x.MakeRelativeTo(BuildTool.RootDirectory).Replace(Path.DirectorySeparatorChar, '/')).OrderBy(x => x));
		}

		private void ResolveLibraryName(string LibraryPath, string LibraryName, string LibraryExtension, HashSet<FileReference> Files)
		{
			string LibraryFileName = Path.Combine(LibraryPath, LibraryName);
			if (File.Exists(LibraryFileName))
			{
				Files.Add(new FileReference(LibraryFileName));
			}

			if (LibraryName.IndexOfAny(new char[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) == -1)
			{
				string UnixLibraryFileName = Path.Combine(LibraryPath, Tag.Directory.Lib + LibraryName + LibraryExtension);
				if (File.Exists(UnixLibraryFileName))
				{
					Files.Add(new FileReference(UnixLibraryFileName));
				}
			}
		}
		
		// Generates a public manifest file for writing out
		public void GenerateManifest(FileReference ManifestPath, List<KeyValuePair<FileReference, BuildProductType>> BuildProducts)
		{
			BuildManifest Manifest = new BuildManifest();

			// Add the regular build products
			foreach (KeyValuePair<FileReference, BuildProductType> BuildProductPair in BuildProducts)
			{
				Manifest.BuildProducts.Add(BuildProductPair.Key.FullName);
			}

			// Add all the dependency lists
			foreach(FileReference DependencyListFileName in Rules.DependencyListFileNames)
			{
				Manifest.BuildProducts.Add(DependencyListFileName.FullName);
			}

			if (!Rules.bDisableLinking)
			{
				Manifest.AddBuildProduct(ReceiptFileName.FullName);

				if (bDeployAfterCompile)
				{
					Manifest.DeployTargetFiles.Add(ReceiptFileName.FullName);
				}
			}

			// Remove anything that's not part of the plugin
			if(ForeignPlugin != null)
			{
				DirectoryReference ForeignPluginDir = ForeignPlugin.Directory;
				Manifest.BuildProducts.RemoveAll(x => !new FileReference(x).IsUnderDirectory(ForeignPluginDir));
			}

			Manifest.BuildProducts.Sort();
			Manifest.DeployTargetFiles.Sort();

			Log.TraceInformation("Writing manifest to {0}", ManifestPath);
			StringUtils.WriteClass<BuildManifest>(Manifest, ManifestPath.FullName, "");
		}

		// Prepare all the module manifests for this target
		Dictionary<FileReference, ModuleManifest> PrepareModuleManifests()
		{
			Dictionary<FileReference, ModuleManifest> FileNameToModuleManifest = new Dictionary<FileReference, ModuleManifest>();
			if (!bCompileMonolithic)
			{
				// Create the receipts for each folder
				foreach (BuildBinary Binary in AllApplicationBuildBinaries)
				{
					if(Binary.Type == BuildBinaryType.DynamicLinkLibrary)
					{
						// i.e) OutputFilePath = {D:\UERelease\Engine\Binaries\Win64\DefaultGame-Win64-Debug.exe}
						// Binary.PrimaryModule ={ Launch }
						DirectoryReference DirectoryName = Binary.OutputFilePath.Directory;
						bool bIsGameBinary = Binary.PrimaryModule.ModuleRule.Context.bCanBuildDebugGame;
						FileReference ManifestFileName = FileReference.Combine
						(
                            DirectoryName,
                            ModuleManifest.GetStandardFileName(AppName, TargetPlatform, Configuration, Architecture, bIsGameBinary)
						);

						if (!FileNameToModuleManifest.TryGetValue(ManifestFileName, out ModuleManifest Manifest))
						{
							Manifest = new ModuleManifest("");
							FileNameToModuleManifest.Add(ManifestFileName, Manifest);
						}

						foreach (BuildModuleCPP Module in Binary.LinkTogetherModules.OfType<BuildModuleCPP>())
						{
							Manifest.ModuleNameToFileName[Module.ModuleRuleFileName] = Binary.OutputFilePath.GetFileName();
						}
					}
				}
			}
			return FileNameToModuleManifest;
		}
		
		// Prepare all the receipts this target (all the .target and .modules files).
		// See the VersionManifest class for an explanation of what these files are.
		TargetReceipt PrepareReceipt
		(
            ToolChain ToolChain,
            List<KeyValuePair<FileReference, BuildProductType>> BuildProducts,
            List<RuntimeDependency> InRuntimeDependencies
		)
		{
			// Read the version file
			if (!BuildVersion.TryRead(BuildVersion.GetDefaultFileName(), out BuildVersion Version))
			{
				Version = new BuildVersion();
			}

			// Create a unique identifier for this build which can be used to identify modules which are compatible.
			// It's fine to share this between runs with the same makefile.
			// By default we leave it blank when compiling a subset of modules (for hot reload, etc...),
			// otherwise it won't match anything else. When writing to a directory
			// that already contains a manifest, we'll reuse the build id that's already in there (see below).
			if (String.IsNullOrEmpty(Version.BuildId))
			{
				if(Rules.bFormalBuild)
				{
					// If this is a formal build, we can just the compatible changelist as the unique id.
					Version.BuildId = String.Format("{0}", Version.EffectiveCompatibleChangelist);
				}
			}

			// If this is an installed engine build, clear the promoted flag on the output binaries. This will ensure we will rebuild them.
			if (Version.IsPromotedBuild && BuildTool.IsEngineInstalled())
			{
				Version.IsPromotedBuild = false;
			}

			// Create the receipt
			TargetReceipt Receipt = new TargetReceipt(ProjectFile, TargetName, TargetType, TargetPlatform, Configuration, Version, Architecture);

			if (!Rules.bShouldCompileAsDLL)
			{
				// Set the launch executable if there is one
				foreach (KeyValuePair<FileReference, BuildProductType> Pair in BuildProducts)
				{
					if (Pair.Value == BuildProductType.Executable)
					{
						Receipt.Launch = Pair.Key;
						break;
					}
				}
			}
			else
			{
				Receipt.AdditionalProperties.Add(new ReceiptProperty(Tag.RecieptProperty.Name.CompileAsDll, Tag.RecieptProperty.Value.True));
			}

			// Find all the build products and modules from this binary
			foreach (KeyValuePair<FileReference, BuildProductType> BuildProductPair in BuildProducts)
			{
				if(BuildProductPair.Value != BuildProductType.BuildResource)
				{
					Receipt.AddBuildProduct(BuildProductPair.Key, BuildProductPair.Value);
				}
			}

			// Add the project file
			if(ProjectFile != null)
			{
				Receipt.RuntimeDependencies.Add(ProjectFile, StagedFileType.CustomPAKFile);
			}

			// Add the descriptors for all enabled plugins
			foreach(BuildPlugin EnabledPlugin in EnabledPlugins)
			{
				if(EnabledPlugin.bDescriptorNeededAtRuntime || 
				   EnabledPlugin.bDescriptorReferencedExplicitly)
				{
					Receipt.RuntimeDependencies.Add(EnabledPlugin.File, StagedFileType.CustomPAKFile);

                    // Only add child plugins that are named for the current Platform or Groups that it's part of
                    if (0 < EnabledPlugin.ChildFiles.Count)
                    {
                        List<string> ValidFileNames = new List<string> { EnabledPlugin.Name + "_" + TargetPlatform.ToString() };

                        foreach (BuildPlatformGroup Group in BuildPlatformGroup.GetValidGroups())
                        {
                            if (BuildPlatform.IsPlatformInGroup(TargetPlatform, Group))
                            {
                                ValidFileNames.Add(EnabledPlugin.Name + "_" + Group.ToString());
                            }
                        }

                        foreach (FileReference ChildFile in EnabledPlugin.ChildFiles)
                        {
                            if (ValidFileNames.Contains(ChildFile.GetFileNameWithoutExtension(), StringComparer.InvariantCultureIgnoreCase))
                            {
                                Receipt.RuntimeDependencies.Add(ChildFile, StagedFileType.CustomPAKFile);
                            }
                        }
                    }
                }
            }

			// Add all the other runtime dependencies
			HashSet<FileReference> UniqueRuntimeDependencyFiles = new HashSet<FileReference>();
			foreach(RuntimeDependency RuntimeDependency in InRuntimeDependencies)
			{
				if(UniqueRuntimeDependencyFiles.Add(RuntimeDependency.Path))
				{
					Receipt.RuntimeDependencies.Add(RuntimeDependency);
				}
			}

			// Find all the modules which are part of this target
			HashSet<BuildModule> UniqueLinkedModules = new HashSet<BuildModule>();
			foreach (BuildBinary Binary in AllApplicationBuildBinaries)
			{
				foreach (BuildModule Module in Binary.LinkTogetherModules)
				{
					if (UniqueLinkedModules.Add(Module))
					{
						Receipt.AdditionalProperties.AddRange(Module.ModuleRule.AdditionalPropertiesForReceipt.Inner);
					}
				}
			}

			// add the SDK used by the tool chain
			Receipt.AdditionalProperties.Add(new ReceiptProperty(Tag.RecieptProperty.Name.SDK, ToolChain.GetSDKVersion()));

			return Receipt;
		}
		
		// Gathers dependency modules for given binaries list.
		static HashSet<BuildModuleCPP> GatherDependencyModules(List<BuildBinary> Binaries)
		{
			HashSet<BuildModuleCPP> OutDependencyModules = new HashSet<BuildModuleCPP>();

			foreach (BuildBinary Binary in Binaries)
			{
				List<BuildModule> DependencyModules = Binary.GetAllDependencyModules(bIncludeDynamicallyLoaded: false, bIgnoreCircularDependencies: false);
				foreach (BuildModuleCPP Module in DependencyModules.OfType<BuildModuleCPP>())
				{
					if (Module.Binary != null)
					{
						OutDependencyModules.Add(Module);
					}
				}
			}

			return OutDependencyModules;
		}

		// return New compile environment.
		public CppCompileEnvironment CreateCppCompileEnvironment()
		{
			CppConfiguration CppConfiguration = GetCppConfiguration(Configuration);

			SourceFileMetadataCache MetadataCache = SourceFileMetadataCache.CreateHierarchy(ProjectFile);

			CppCompileEnvironment OutGlobalCppCompileEnvironment = new CppCompileEnvironment(TargetPlatform, CppConfiguration, Architecture, MetadataCache);
			LinkEnvironment       GlobalLinkEnvironment          = new LinkEnvironment(TargetPlatform, CppConfiguration, Architecture);

			ToolChain TargetToolChain = CreateToolchain(TargetPlatform);

			SetupGlobalEnvironment(OutGlobalCppCompileEnvironment, GlobalLinkEnvironment, TargetToolChain);
			FindSharedPCHs(OutGlobalCppCompileEnvironment, AllApplicationBuildBinaries);

			return OutGlobalCppCompileEnvironment;
		}

		// Builds the target, appending list of output files and returns building result.
		// API_Define
		public TargetMakefile Build
		(
            BuildConfiguration    BuildConfiguration,
            ISourceFileWorkingSet WorkingSet,
            bool                  bIsAssemblingBuild,
            FileReference         SingleFileToCompile
		)
		{
			CppConfiguration CppConfiguration = GetCppConfiguration(Configuration);

			SourceFileMetadataCache MetadataCache = SourceFileMetadataCache.CreateHierarchy(ProjectFile);

			// GlobalCompileEnvironment의 Definitions의 API_DEFINE은
			// BuildModule.UEBuildBinary가 결정
			CppCompileEnvironment GlobalCompileEnvironment = new CppCompileEnvironment(TargetPlatform, CppConfiguration, Architecture, MetadataCache);
			LinkEnvironment       GlobalLinkEnvironment    = new LinkEnvironment(GlobalCompileEnvironment.Platform, GlobalCompileEnvironment.Configuration, GlobalCompileEnvironment.Architecture);

			ToolChain TargetToolChain = CreateToolchain(TargetPlatform);
			TargetToolChain.SetEnvironmentVariables(); // Set VCToolPath to Environment Variables
			SetupGlobalEnvironment(GlobalCompileEnvironment, GlobalLinkEnvironment, TargetToolChain);

			// Save off the original list of binaries.
			// We'll use this to figure out which PCHs to create later,
			// to avoid switching PCHs when compiling single modules.
			List<BuildBinary> ReplicaAllBuildBinaries = AllApplicationBuildBinaries;

			// For installed builds, filter out all the binaries that aren't in mods
			if (BuildTool.IsProjectInstalled())
			{
				List<DirectoryReference> ModDirectories = EnabledPlugins.Where(x => x.Type == PluginType.Mod).Select(x => x.Directory).ToList();

				List<BuildBinary> FilteredBinaries = new List<BuildBinary>();
				foreach (BuildBinary DLLBinary in AllApplicationBuildBinaries)
				{
					if(ModDirectories.Any(x => DLLBinary.OutputFilePath.IsUnderDirectory(x)))
					{
						FilteredBinaries.Add(DLLBinary);
					}
				}
				AllApplicationBuildBinaries = FilteredBinaries;

				if (AllApplicationBuildBinaries.Count == 0)
				{
					throw new BuildException("No modules found to build. All requested binaries were already part of the installed data.");
				}
			}

			// Build a mapping from module to its plugin
			Dictionary<BuildModule, BuildPlugin> ModuleToPlugin = new Dictionary<BuildModule, BuildPlugin>();
			foreach(BuildPlugin Plugin in BuildPlugins)
			{
				foreach(BuildModule Module in Plugin.Modules)
				{
					if (!ModuleToPlugin.ContainsKey(Module))
					{
						ModuleToPlugin.Add(Module, Plugin);
					}
				}
			}

			// Check there aren't any engine binaries with dependencies on game modules. This can happen when game-specific plugins override engine plugins.
			foreach(BuildModule Module in Modules.Values)
			{
				if(Module.Binary != null)
				{
					HashSet<BuildModule> ReferencedModules = Module.GetAllModules(bWithIncludePathModules: true, bWithDynamicallyLoadedModules: true);
					foreach(BuildModule ReferencedModule in ReferencedModules)
					{
						if(!(Module.ModuleRule.Context.Scope.Contains(ReferencedModule.ModuleRule.Context.Scope) || 
						   IsWhitelistedEnginePluginReference(Module.ModuleRuleFileName, ReferencedModule.ModuleRuleFileName)))
						{
							throw new BuildException("Module '{0}' ({1}) should not reference module '{2}' ({3}). Hierarchy is {4}.", 
								Module.ModuleRuleFileName, Module.ModuleRule.Context.Scope.Name, 
								ReferencedModule.ModuleRuleFileName, ReferencedModule.ModuleRule.Context.Scope.Name, 
								ReferencedModule.ModuleRule.Context.Scope.FormatHierarchy());
						}
					}
				}
			}

			// Check that each plugin declares its dependencies explicitly
			foreach(BuildPlugin Plugin in BuildPlugins)
			{
				foreach(BuildModule Module in Plugin.Modules)
				{
					HashSet<BuildModule> DependencyModules = Module.GetAllModules(bWithIncludePathModules: true, bWithDynamicallyLoadedModules: true);
					foreach(BuildModule DependencyModule in DependencyModules)
					{
						if (ModuleToPlugin.TryGetValue(DependencyModule, out BuildPlugin DependencyPlugin) && 
							DependencyPlugin != Plugin                                                     && 
							!Plugin.Dependencies.Contains(DependencyPlugin))
						{
							Log.TraceWarning("Warning: Plugin '{0}' does not list plugin '{1}' as a dependency, but module '{2}' depends on '{3}'.", 
								Plugin.Name, DependencyPlugin.Name, 
								Module.ModuleRuleFileName, DependencyModule.ModuleRuleFileName);
						}
					}
				}
			}

			// Create the makefile
			string ExternalMetadata = BuildPlatform.GetBuildPlatform(TargetPlatform).GetExternalBuildMetadata(ProjectFile);
			TargetMakefile Makefile = new TargetMakefile
			(
                ExternalMetadata,
                AllApplicationBuildBinaries[0].OutputFilePaths[0],
                ReceiptFileName,
                ProjectIntermediateDirectory,
                TargetType,
                Rules.ConfigValueTracker,
                bDeployAfterCompile,
                bHasProjectScriptPlugin
			);

			// Get diagnostic info to be printed before each build
			TargetToolChain.GetVersionInfo(Makefile.Diagnostics);
			Rules.GetBuildSettingsInfo(Makefile.Diagnostics);

			// Setup the hot reload module list
			Makefile.HotReloadModuleNames = GetHotReloadModuleNames();

			// If we're compiling monolithic, make sure the executable knows about all referenced modules
			if (ShouldCompileMonolithic())
			{
				BuildBinary ExecutableBinary = AllApplicationBuildBinaries[0];

				// Add all the modules that the executable depends on. Plugins will be already included in this list.
				List<BuildModule> AllReferencedModules =
                    ExecutableBinary.GetAllDependencyModules
                    (
                        bIncludeDynamicallyLoaded   : true,
                        bIgnoreCircularDependencies : true
                    );


                foreach (BuildModule CurModule in AllReferencedModules)
				{
					if (CurModule.Binary == null             || 
						CurModule.Binary == ExecutableBinary || 
						CurModule.Binary.Type == BuildBinaryType.StaticLibrary)
					{
						ExecutableBinary.AddModule(CurModule);
					}
				}
			}

			// Add global definitions for project-specific binaries.
			// HACK: Also defining for monolithic builds in binary releases.
			// Might be better to set this via command line instead?
			if(!bUseSharedBuildEnvironment || bCompileMonolithic)
			{
				BuildBinary ExecutableBinary = AllApplicationBuildBinaries[0];

				bool IsCurrentPlatform = Utils.IsRunningOnMono ? 
					TargetPlatform == BuildTargetPlatform.Mac || 
					(BuildPlatform.IsPlatformInGroup(TargetPlatform, BuildPlatformGroup.Unix) && 
					TargetPlatform == BuildHostPlatform.Current.Platform)
                    : TargetPlatform.IsInGroup(BuildPlatformGroup.Windows) || TargetPlatform == BuildTargetPlatform.HoloLens;

                if (IsCurrentPlatform)
				{
					// The hardcoded engine directory needs to be a relative path to match the normal EngineDir format. Not doing so breaks the network file system (TTP#315861).
					string OutputFilePath = ExecutableBinary.OutputFilePath.FullName;
					if (TargetPlatform == BuildTargetPlatform.Mac && OutputFilePath.Contains(".app/Contents/MacOS"))
					{
						OutputFilePath = OutputFilePath.Substring(0, OutputFilePath.LastIndexOf(".app/Contents/MacOS") + 4);
					}
					string EnginePath = StringUtils.CleanDirectorySeparators(BuildTool.EngineDirectory.MakeRelativeTo(ExecutableBinary.OutputFilePath.Directory), '/');
					if (EnginePath.EndsWith("/") == false)
					{
						EnginePath += "/";
					}
					GlobalCompileEnvironment.Definitions.Add(String.Format("UE_ENGINE_DIRECTORY=\"{0}\"", EnginePath));
				}
			}

			// On Mac and Linux we have actions that should be executed after all the binaries are created
			TargetToolChain.SetupBundleDependencies(AllApplicationBuildBinaries, TargetName);

			// Generate headers
			// foreach (UEBuildBinary Binary in Binaries)
			//{
			//	List<UEBuildModule> DependencyModules = Binary.GetAllDependencyModules(bIncludeDynamicallyLoaded: false, bForceCircular: false);
			//	foreach (UEBuildModuleCPP Module in DependencyModules.OfType<UEBuildModuleCPP>())
			//	{
			//		if (Module.Binary != null)
			//		{
			//			Output.Add(Module);
			//		}
			//	}
			//}
			//return Output;
			HashSet<BuildModuleCPP> ModulesToGenerateHeadersFor = GatherDependencyModules(ReplicaAllBuildBinaries.ToList());

            // 멀티스레딩
            HeaderToolExecution.SetupUObjectModules
            (
				ModulesToGenerateHeadersFor, 
				Rules.Platform, 
				ProjectDescriptor, 
				Makefile.UObjectModules, 
				Makefile.UObjectModuleHeaders,
                Rules.GeneratedCodeVersion, 
				/*bIsAssemblingBuild,*/
				MetadataCache
			);

            // NOTE: Even in Gather mode, we need to run UHT to make sure the files exist for the static action graph to be setup correctly.
            // This is because UHT generates .cpp files that are injected as top level prerequisites.
            // If UHT only emitted included header files, we wouldn't need to run it during the Gather phase at all.
            if (0 < Makefile.UObjectModules.Count)
			{
				FileReference ModuleInfoFileName = FileReference.Combine(ProjectIntermediateDirectory, TargetName + Tag.Ext.HeaderToolManifest);
				HeaderToolExecution.ExecuteHeaderToolIfNecessary
				(
					BuildConfiguration, 
					ProjectFile, 
					TargetName, 
					TargetType, 
					bHasProjectScriptPlugin, 
					Makefile.UObjectModules, 
					ModuleInfoFileName, 
					true, 
					bIsAssemblingBuild, 
					WorkingSet
				);
			}

			// Find all the shared PCHs.
			// TargetRules가 bUseSharedPCHs(기본값 참)일시 모두 SharedPCH찾음
			if (Rules.bUseSharedPCHs)
			{
				FindSharedPCHs(GlobalCompileEnvironment, ReplicaAllBuildBinaries);
			}

			// Compile the resource files common to all DLLs on Windows
			if (!ShouldCompileMonolithic())
			{
				if (TargetPlatform == BuildTargetPlatform.Win32 || TargetPlatform == BuildTargetPlatform.Win64)
				{
					if(!Rules.bFormalBuild)
					{
						FileReference DefaultResourceLocation = FileReference.Combine(BuildTool.EngineDirectory, Tag.Directory.Build, Tag.Directory.Windows, Tag.Directory.Resources, Tag.Binary.DefaultResource + Tag.Ext.RC2);
						if (!BuildTool.IsFileInstalled(DefaultResourceLocation))
						{
							CppCompileEnvironment DefaultResourceCompileEnvironment = new CppCompileEnvironment(GlobalCompileEnvironment);

							FileItem DefaultResourceFile = FileItem.GetItemByFileReference(DefaultResourceLocation);

							CPPOutput DefaultResourceOutput = TargetToolChain.CompileRCFiles(DefaultResourceCompileEnvironment, new List<FileItem> { DefaultResourceFile }, EngineIntermediateDirectory, Makefile);

							if (DefaultResourceOutput != null && 0 < DefaultResourceOutput.ObjectFiles.Count)
							{
								GlobalLinkEnvironment.DefaultResourceFiles.AddRange(DefaultResourceOutput.ObjectFiles);
							}
						}
					}
				}
			}

			// Find the set of binaries to build. If we're just compiling a single file, filter the list of binaries to only include the file we're interested in.
			List<BuildBinary> BuildBinaries = AllApplicationBuildBinaries;

			if (SingleFileToCompile != null)
			{
				BuildBinaries = AllApplicationBuildBinaries.Where(x => x.LinkTogetherModules.Any(y => y.ContainsFile(SingleFileToCompile))).ToList();
				if (BuildBinaries.Count == 0)
				{
					throw new BuildException("Couldn't find any module containing {0} in {1}.", SingleFileToCompile, TargetName);
				}
			}

			// Build the target's binaries.
			DirectoryReference ExeDir = GetExecutableDir();

            foreach (BuildBinary Binary in BuildBinaries)
            {
                List<FileItem> BinaryOutputItems = Binary.Build(Rules, TargetToolChain, GlobalCompileEnvironment, GlobalLinkEnvironment, SingleFileToCompile, WorkingSet, ExeDir, Makefile);
                Makefile.OutputItems.AddRange(BinaryOutputItems);
            }

            // Prepare all the runtime dependencies, copying them from their source folders if necessary
            List<RuntimeDependency> RuntimeDependencies = new List<RuntimeDependency>();
			Dictionary<FileReference, FileReference> RuntimeDependencyTargetFileToSourceFile = new Dictionary<FileReference, FileReference>();

			foreach(BuildBinary Binary in AllApplicationBuildBinaries)
			{
				Binary.PrepareRuntimeDependencies(RuntimeDependencies, RuntimeDependencyTargetFileToSourceFile, ExeDir);
			}
			TargetToolChain.PrepareRuntimeDependencies(RuntimeDependencies, RuntimeDependencyTargetFileToSourceFile, ExeDir);

			foreach(KeyValuePair<FileReference, FileReference> Pair in RuntimeDependencyTargetFileToSourceFile)
			{
				if(!BuildTool.IsFileInstalled(Pair.Key))
				{
					Makefile.OutputItems.Add(Makefile.CreateCopyAction(Pair.Value, Pair.Key));
				}
			}

			// If we're just precompiling a plugin, only include output items which are part of it
			if(ForeignPlugin != null)
			{
				HashSet<FileItem> RetainOutputItems = new HashSet<FileItem>();

				foreach(BuildPlugin Plugin in BuildPlugins)
				{
					if(Plugin.File == ForeignPlugin)
					{
						foreach (BuildModule Module in Plugin.Modules)
						{
							if (Makefile.ModuleNameToOutputItems.TryGetValue(Module.ModuleRuleFileName, out FileItem[] ModuleOutputItems))
							{
								RetainOutputItems.UnionWith(ModuleOutputItems);
							}
						}
					}
				}
				Makefile.OutputItems.RemoveAll(x => !RetainOutputItems.Contains(x));
			}

			// Allow the toolchain to modify the final output items
			TargetToolChain.FinalizeOutput(Rules, Makefile);

			// Get all the regular build products
			List<KeyValuePair<FileReference, BuildProductType>> BuildProducts = new List<KeyValuePair<FileReference, BuildProductType>>();
			foreach (BuildBinary Binary in AllApplicationBuildBinaries)
			{
				Dictionary<FileReference, BuildProductType> BinaryBuildProducts = new Dictionary<FileReference, BuildProductType>();
				Binary.GetBuildProducts(Rules, TargetToolChain, BinaryBuildProducts, GlobalLinkEnvironment.bCreateDebugInfo);
				BuildProducts.AddRange(BinaryBuildProducts);
			}
			BuildProducts.AddRange(RuntimeDependencyTargetFileToSourceFile.Select(x => new KeyValuePair<FileReference, BuildProductType>(x.Key, BuildProductType.RequiredResource)));

			// Remove any installed build products that don't exist. They may be part of an optional install.
			if(BuildTool.IsEngineInstalled())
			{
				BuildProducts.RemoveAll(x => BuildTool.IsFileInstalled(x.Key) && !FileReference.Exists(x.Key));
			}

			// Make sure all the checked headers were valid
			List<string> InvalidIncludeDirectiveMessages = Modules.Values.OfType<BuildModuleCPP>().Where(x => x.InvalidIncludeDirectiveMessages != null).SelectMany(x => x.InvalidIncludeDirectiveMessages).ToList();
			if (0 < InvalidIncludeDirectiveMessages.Count)
			{
				foreach (string InvalidIncludeDirectiveMessage in InvalidIncludeDirectiveMessages)
				{
					Log.WriteLine(0, LogEventType.Warning, LogFormatOptions.NoSeverityPrefix, "{0}", InvalidIncludeDirectiveMessage);
				}
			}

			// Finalize and generate metadata for this target
			if(!Rules.bDisableLinking)
			{
				// Also add any explicitly specified build products
				if(0 < Rules.AdditionalBuildProducts.Count)
				{
					Dictionary<string, string> Variables = GetTargetVariables(null);
					foreach(string AdditionalBuildProduct in Rules.AdditionalBuildProducts)
					{
						FileReference BuildProductFile = new FileReference(StringUtils.ExpandVariables(AdditionalBuildProduct, Variables));
						BuildProducts.Add(new KeyValuePair<FileReference, BuildProductType>(BuildProductFile, BuildProductType.RequiredResource));
					}
				}

				// Get the path to the version file unless this is a formal build (where it will be compiled in)
				FileReference VersionFile = null;
				if(Rules.LinkType                      != TargetLinkType.Monolithic && 
				   AllApplicationBuildBinaries[0].Type == BuildBinaryType.Executable)
				{
					TargetConfiguration VersionConfig = Configuration;
					if(VersionConfig == TargetConfiguration.DebugGame &&
						TargetType != TargetType.Program                    && 
						bUseSharedBuildEnvironment                          &&
						!bCompileMonolithic)
					{
						VersionConfig = TargetConfiguration.Development;
					}
					VersionFile = BuildVersion.GetFileNameForTarget
					(
                        ExeDir,
                        bCompileMonolithic ? TargetName : AppName,
                        TargetPlatform,
                        VersionConfig,
                        Architecture
					);
				}

				// Also add the version file as a build product
				if(VersionFile != null)
				{
					BuildProducts.Add(new KeyValuePair<FileReference, BuildProductType>(VersionFile, BuildProductType.RequiredResource));
				}

				// Prepare the module manifests, and add them to the list of build products
				Dictionary<FileReference, ModuleManifest> FileNameToModuleManifest = PrepareModuleManifests();
				BuildProducts.AddRange(FileNameToModuleManifest.Select(x => new KeyValuePair<FileReference, BuildProductType>(x.Key, BuildProductType.RequiredResource)));

				// Prepare the receipt
				TargetReceipt Receipt = PrepareReceipt(TargetToolChain, BuildProducts, RuntimeDependencies);

				// Create an action which to generate the receipts
				WriteMetadataTargetInfo MetadataTargetInfo = new WriteMetadataTargetInfo(ProjectFile, VersionFile, ReceiptFileName, Receipt, FileNameToModuleManifest);
				FileReference           MetadataTargetFile = FileReference.Combine(ProjectIntermediateDirectory, Tag.Binary.MetaData + Tag.Ext.Dat);
				BinaryFormatterUtils.SaveIfDifferent(MetadataTargetFile, MetadataTargetInfo);

				StringBuilder WriteMetadataArguments = new StringBuilder();
				WriteMetadataArguments.AppendFormat(Tag.Argument.Input + "{0}", StringUtils.MakePathSafeToUseWithCommandLine(MetadataTargetFile));
				WriteMetadataArguments.AppendFormat(" " + Tag.Argument.Version +"{0}", WriteMetadataMode.CurrentVersionNumber);
				if(Rules.bNoManifestChanges)
				{
					WriteMetadataArguments.Append(" " + Tag.Argument.NoManifestChanges);
				}

				Action WriteMetadataAction = Makefile.CreateRecursiveAction<WriteMetadataMode>(ActionType.WriteMetadata, WriteMetadataArguments.ToString());
				WriteMetadataAction.WorkingDirectory = BuildTool.EngineSourceDirectory;
				WriteMetadataAction.StatusDescription = ReceiptFileName.GetFileName();
				WriteMetadataAction.bCanExecuteRemotely = false;
				WriteMetadataAction.PrerequisiteItems.Add(FileItem.GetItemByFileReference(MetadataTargetFile));
				WriteMetadataAction.PrerequisiteItems.AddRange(Makefile.OutputItems);
				WriteMetadataAction.ProducedItems.Add(FileItem.GetItemByFileReference(ReceiptFileName));

				Makefile.OutputItems.AddRange(WriteMetadataAction.ProducedItems);

				// Create actions to run the post build steps
				FileReference[] PostBuildScripts = CreatePostBuildScripts();
				foreach(FileReference PostBuildScript in PostBuildScripts)
				{
					FileReference OutputFile = new FileReference(PostBuildScript.FullName + Tag.Ext.Ran);

					Action PostBuildStepAction = Makefile.CreateAction(ActionType.PostBuildStep);
					PostBuildStepAction.CommandPath = BuildHostPlatform.Current.ShellPath;
					if(BuildHostPlatform.Current.ShellType == ShellType.Cmd)
					{
						// PostBuildStepAction.CommandArguments = String.Format("/C \"call \"{0}\" && type NUL >\"{1}\"\"", PostBuildScript, OutputFile);
						PostBuildStepAction.CommandArguments = String.Format
						(
							Tag.Command.CommandPrompt.CarryOutStop + 
							" \"" + Tag.Command.CommandPrompt.Call +
							" \"" + "{0}\" " + Tag.Command.CommandSeperator + " " +
							Tag.Command.CommandPrompt.Type + " " + 
							Tag.Command.CommandPrompt.Null + " " + 
							Tag.Command.CommandPrompt.Redirection + "\"{1}\"\"", 
							PostBuildScript, OutputFile
						);
					}
					else
					{
						PostBuildStepAction.CommandArguments = String.Format("\"{0}\"" + Tag.Command.CommandSeperator + Tag.Command.CommandPrompt.Touch + " \"{1}\"", PostBuildScript, OutputFile);
					}
					PostBuildStepAction.WorkingDirectory = BuildTool.EngineSourceDirectory;
					PostBuildStepAction.StatusDescription = String.Format("Executing post build script ({0})", PostBuildScript.GetFileName());
					PostBuildStepAction.bCanExecuteRemotely = false;
					PostBuildStepAction.PrerequisiteItems.Add(FileItem.GetItemByFileReference(ReceiptFileName));
					PostBuildStepAction.ProducedItems.Add(FileItem.GetItemByFileReference(OutputFile));

					Makefile.OutputItems.AddRange(PostBuildStepAction.ProducedItems);
				}
			}

			// Build a list of all the files required to build
			// Only PublicInclude
			foreach(FileReference DependencyListFileName in Rules.DependencyListFileNames)
			{
				WriteDependencyList(DependencyListFileName, RuntimeDependencies, RuntimeDependencyTargetFileToSourceFile);
			}

			// If we're only generating the manifest, return now
			foreach(FileReference ManifestFileName in Rules.ManifestFileNames)
			{
				GenerateManifest(ManifestFileName, BuildProducts);
			}

			// Check there are no EULA or restricted folder violations
			if(!Rules.bDisableLinking)
			{
				// Check the distribution level of all binaries based on the dependencies they have
				if (ProjectFile == null && !Rules.bLegalToDistributeBinary)
				{
					List<DirectoryReference> RootDirectories = new List<DirectoryReference> { BuildTool.EngineDirectory };
					if (ProjectFile != null)
					{
						DirectoryReference ProjectDir = DirectoryReference.FromFile(ProjectFile);
						RootDirectories.Add(ProjectDir);
						if (ProjectDescriptor != null)
						{
							ProjectDescriptor.AddAdditionalPaths(RootDirectories/*, ProjectDir*/);
						}
					}

					Dictionary<BuildModule, Dictionary<RestrictedFolder, DirectoryReference>> ModuleRestrictedFolderCache = new Dictionary<BuildModule, Dictionary<RestrictedFolder, DirectoryReference>>();

					bool bResult = true;
					foreach (BuildBinary Binary in AllApplicationBuildBinaries)
					{
						bResult &= Binary.CheckRestrictedFolders(RootDirectories, ModuleRestrictedFolderCache);
					}
					foreach(KeyValuePair<FileReference, FileReference> Pair in RuntimeDependencyTargetFileToSourceFile)
					{
						bResult &= CheckRestrictedFolders(Pair.Key, Pair.Value);
					}

					if(!bResult)
					{
						throw new BuildException("Unable to create binaries in less restricted locations than their input files.");
					}
				}

				// Check for linking against modules prohibited by the EULA
				CheckForEULAViolation();
			}

			// Add all the plugins to be tracked
			foreach(FileReference PluginFile in global::BuildTool.Plugins.EnumeratePlugins(ProjectFile))
			{
				FileItem PluginFileItem = FileItem.GetItemByFileReference(PluginFile);
				Makefile.PluginFiles.Add(PluginFileItem);
			}

			// Add all the input files to the predicate store
			Makefile.ExternalDependencies.Add(FileItem.GetItemByFileReference(TargetRulesFile));

			foreach(BuildModule Module in Modules.Values)
			{
				Makefile.ExternalDependencies.Add(FileItem.GetItemByFileReference(Module.RulesFile));
				foreach(string ExternalDependency in Module.ModuleRule.ExternalDependencies)
				{
					FileReference Location = FileReference.Combine(Module.RulesFile.Directory, ExternalDependency);
					Makefile.ExternalDependencies.Add(FileItem.GetItemByFileReference(Location));
				}
				if (Module.ModuleRule.SubclassRules != null)
				{
					foreach (string SubclassRule in Module.ModuleRule.SubclassRules)
					{
						FileItem SubclassRuleFileItem = FileItem.GetItemByFileReference(new FileReference(SubclassRule));
						Makefile.ExternalDependencies.Add(SubclassRuleFileItem);
					}
				}
			}
			Makefile.ExternalDependencies.UnionWith(Makefile.PluginFiles);

			// Write a header containing public definitions for this target
			// API_Define
			if (Rules.ExportPublicHeader != null)
			{
				BuildBinary Binary = AllApplicationBuildBinaries[0];
				FileReference Header = FileReference.Combine(Binary.OutputDir, Rules.ExportPublicHeader);
				WritePublicHeader(Binary, Header, GlobalCompileEnvironment);
			}

			// Clean any stale modules which exist in multiple output directories. This can lead to the wrong DLL being loaded on Windows.
			CleanStaleModules();

			return Makefile;
		}

		// Gets the output directory for the main executable
		// <returns>The executable directory</returns>
		public DirectoryReference GetExecutableDir()
		{
			DirectoryReference ExeDir = AllApplicationBuildBinaries[0].OutputDir;
			if (TargetPlatform == BuildTargetPlatform.Mac && ExeDir.FullName.EndsWith(Tag.Directory.AppFolder))
			{
				ExeDir = ExeDir.ParentDirectory.ParentDirectory.ParentDirectory;
			}
			return ExeDir;
		}

		// Check that copying a file from one location to another does not violate rules regarding restricted folders
		bool CheckRestrictedFolders(FileReference TargetFile, FileReference SourceFile)
		{
			List<RestrictedFolder> TargetRestrictedFolders = GetRestrictedFolders(TargetFile);
			List<RestrictedFolder> SourceRestrictedFolders = GetRestrictedFolders(SourceFile);
			foreach(RestrictedFolder SourceRestrictedFolder in SourceRestrictedFolders)
			{
				if(!TargetRestrictedFolders.Contains(SourceRestrictedFolder))
				{
					Log.TraceError("Runtime dependency '{0}' is copied to '{1}', which does not contain a '{2}' folder.", 
						SourceFile, TargetFile, SourceRestrictedFolder);
					return false;
				}
			}

			return true;
		}

		// Gets the restricted folders that the given file is in
		List<RestrictedFolder> GetRestrictedFolders(FileReference File)
		{
			// Find the base directory for this binary
			DirectoryReference BaseDir;
			if(File.IsUnderDirectory(BuildTool.RootDirectory))
			{
				BaseDir = BuildTool.RootDirectory;
			}
			else if(ProjectDirectory != null && File.IsUnderDirectory(ProjectDirectory))
			{
				BaseDir = ProjectDirectory;
			}
			else
			{
				return new List<RestrictedFolder>();
			}

			// Find the restricted folders under the base directory
			return RestrictedFolders.FindPermittedRestrictedFolderReferences(BaseDir, File.Directory);
		}

		// Creates a toolchain for the current target.
		// May be overridden by the target rules.
		private ToolChain CreateToolchain(BuildTargetPlatform Platform)
		{
			if (Rules.ToolChainName == null)
			{
				return BuildPlatform.GetBuildPlatform(Platform).CreateToolChain(Rules);
			}
			else
			{
				Type ToolchainType = Assembly.GetExecutingAssembly().GetType(String.Format(nameof(BuildTool) + ".{0}", Rules.ToolChainName), false, true);
				
				if (ToolchainType == null)
				{
					Debugger.Break();
					throw new BuildException("Unable to create toolchain '{0}'. Check that the name is correct.", Rules.ToolChainName);
				}

				return (ToolChain)Activator.CreateInstance(ToolchainType, Rules);
			}
		}

		// Cleans any stale modules that have changed moved output folder.
		// 
		// On Windows, the loader reads imported DLLs from the first location it finds them.
		// If modules are moved from one place to another, we have to be sure to clean up the old versions
		// so that they're not loaded accidentally causing unintuitive import errors.
		void CleanStaleModules()
		{
			// Find all the output files
			HashSet<FileReference> OutputFiles = new HashSet<FileReference>();
			foreach(BuildBinary Binary in AllApplicationBuildBinaries)
			{
				OutputFiles.UnionWith(Binary.OutputFilePaths);
			}

			// Build a map of base filenames to their full path
			Dictionary<string, FileReference> OutputNameToLocation = new Dictionary<string, FileReference>(StringComparer.InvariantCultureIgnoreCase);
			foreach(FileReference OutputFile in OutputFiles)
			{
				OutputNameToLocation[OutputFile.GetFileName()] = OutputFile;
			}

			// Search all the output directories for files with a name matching one of our output files
			foreach(DirectoryReference OutputDirectory in OutputFiles.Select(x => x.Directory).Distinct())
			{
				if (DirectoryReference.Exists(OutputDirectory))
				{
					foreach (FileReference ExistingFile in DirectoryReference.EnumerateFiles(OutputDirectory))
					{
						if (OutputNameToLocation.TryGetValue(ExistingFile.GetFileName(), out FileReference OutputFile) && !OutputFiles.Contains(ExistingFile))
						{
							Log.TraceInformation("Deleting '{0}' to avoid ambiguity with '{1}'", ExistingFile, OutputFile);
							try
							{
								FileReference.Delete(ExistingFile);
							}
							catch (Exception Ex)
							{
								Log.TraceError("Unable to delete {0} ({1})", ExistingFile, Ex.Message.TrimEnd());
							}
						}
					}
				}
			}
		}

		// Check whether a reference from an engine module to a plugin module is allowed.
		// Temporary hack until these can be fixed up propertly.
		static bool IsWhitelistedEnginePluginReference(string EngineModuleName, string PluginModuleName)
		{
			if(EngineModuleName == Tag.Module.EngineAndEditor.AndroidDeviceDetection && PluginModuleName == Tag.Module.Plugins.TcpMessaging)
			{
				return true;
			}
			if(EngineModuleName == Tag.Module.Engine.Online.Voice && PluginModuleName == Tag.Module.Plugins.AndroidPermission)
			{
				return true;
			}

			return false;
		}

		// Export the definition of this target to a JSON file
		public void ExportJson(FileReference OutputFile)
		{
			DirectoryReference.CreateDirectory(OutputFile.Directory);
			using (JsonWriter Writer = new JsonWriter(OutputFile))
			{
				Writer.WriteObjectStart();

				Writer.WriteValue(Tag.JSONField.Name, TargetName);
				Writer.WriteValue(Tag.JSONField.Configuration, Configuration.ToString());
				Writer.WriteValue(Tag.JSONField.Platform, TargetPlatform.ToString());
				if (ProjectFile != null)
				{
					Writer.WriteValue(Tag.JSONField.ProjectFile, ProjectFile.FullName);
				}

				Writer.WriteArrayStart(Tag.JSONField.Binaries);

				foreach (BuildBinary Binary in AllApplicationBuildBinaries)
				{
					Writer.WriteObjectStart();
					Binary.ExportJson(Writer);
					Writer.WriteObjectEnd();
				}
				Writer.WriteArrayEnd();

				Writer.WriteObjectStart(Tag.JSONField.Modules);
				foreach (BuildModule Module in Modules.Values)
				{
					Writer.WriteObjectStart(Module.ModuleRuleFileName);
					Module.ExportJson(Module.Binary?.OutputDir, GetExecutableDir(), Writer);
					Writer.WriteObjectEnd();
				}

				Writer.WriteObjectEnd();
				Writer.WriteObjectEnd();
			}
		}

		// Writes a header for the given binary that allows including headers from it in an external application
		void WritePublicHeader(BuildBinary Binary, FileReference HeaderFileToWrite, CppCompileEnvironment GlobalCompileEnvironment)
		{
			DirectoryReference.CreateDirectory(HeaderFileToWrite.Directory);

			List<string> Definitions = new List<string>(GlobalCompileEnvironment.Definitions);

			foreach(BuildModule Module in Binary.LinkTogetherModules)
			{
				Module.AddModuleToCompileEnvironment
				(
                    null,
                    new HashSet<DirectoryReference>(),
                    new HashSet<DirectoryReference>(),
                    Definitions,
                    new List<BuildFramework>(),
                    new List<FileItem>(),
                    false
				);
			}

			// Write the header
			using(StreamWriter Writer = new StreamWriter(HeaderFileToWrite.FullName))
			{
				Writer.WriteLine(Tag.CppContents.PragmaOnce);
				Writer.WriteLine();
				foreach(string Definition in Definitions)
				{
					int EqualsIdx = Definition.IndexOf('=');
					if(EqualsIdx == -1)
					{
						Writer.WriteLine(String.Format(Tag.CppContents.Define + " {0} 1", Definition));
					}
					else
					{
						Writer.WriteLine(String.Format(Tag.CppContents.Define + " {0} {1}", 
							Definition.Substring(0, EqualsIdx), Definition.Substring(EqualsIdx + 1)));
					}
				}
			}

			Log.TraceInformation("Written public header to {0}", HeaderFileToWrite);
		}

		// Check for EULA violation dependency issues.
		private void CheckForEULAViolation()
		{
			if (TargetType != TargetType.Editor  && 
				TargetType != TargetType.Program && 
				Configuration == TargetConfiguration.Shipping &&
				Rules.bCheckLicenseViolations)
			{
				bool bLicenseViolation = false;
				foreach (BuildBinary Binary in AllApplicationBuildBinaries)
				{
					List<BuildModule> AllDependencies = Binary.GetAllDependencyModules(true, false);

					IEnumerable<BuildModule> NonRedistModules = AllDependencies.Where
					(
						(DependencyModule) =>
						!IsRedistributable(DependencyModule) && 
						DependencyModule.ModuleRuleFileName != AppName
					);

					if (NonRedistModules.Count() != 0)
					{
						IEnumerable<BuildModule> NonRedistDeps = AllDependencies.Where
						(
							(DependantModule) =>
							DependantModule.PublicDependencyModules.Concat(DependantModule.PrivateDependencyModules).
							Concat(DependantModule.DynamicallyLoadedModules).Intersect(NonRedistModules).Any()
						);

						string Message = string.Format("Non-editor build cannot depend on non-redistributable modules. {0} depends on '{1}'.", Binary.ToString(), string.Join("', '", NonRedistModules));
						if (NonRedistDeps.Any())
						{
							Message = string.Format("{0}\nDependant modules '{1}'", Message, string.Join("', '", NonRedistDeps));
						}
						if(Rules.bBreakBuildOnLicenseViolation)
						{
							Log.TraceError("ERROR: {0}", Message);
						}
						else
						{
							Log.TraceWarning("WARNING: {0}", Message);
						}
						bLicenseViolation = true;
					}
				}

				if (Rules.bBreakBuildOnLicenseViolation && bLicenseViolation)
				{
					throw new BuildException("Non-editor build cannot depend on non-redistributable modules.");
				}
			}
		}

		// Tells if this module can be redistributed.
		public static bool IsRedistributable(BuildModule Module)
		{
			if(Module.ModuleRule != null && 
			   Module.ModuleRule.IsRedistributableOverride.HasValue)
			{
				return Module.ModuleRule.IsRedistributableOverride.Value;
			}

			if(Module.RulesFile != null)
			{
				return !Module.RulesFile.IsUnderDirectory(BuildTool.EngineSourceDeveloperDirectory) && 
					   !Module.RulesFile.IsUnderDirectory(BuildTool.EngineSourceEditorDirectory);
			}

			return true;
		}

		// Setup target before build.
		// This method finds dependencies, sets up global environment etc.
		public void PreBuildSetup()
		{
			Log.TraceVerbose("Building {0} - {1} - {2} - {3}", AppName, TargetName, TargetPlatform, Configuration);

			// Setup the target's binaries.
			// Setup Launch Module(LAUNCH_API)
			// => Add Launch Module to Binaries[Container]
			SetupAndAddLaunchModule();

			// Setup the target's plugins
			SetupPlugins();

			// Add the plugin binaries to the build
			// 외부 플ㄹ러그인 추가, uplugin 확장자에 있는 dll 추가
			foreach (BuildPlugin Plugin in BuildPlugins)
			{
				foreach(BuildModuleCPP Module in Plugin.Modules)
				{
					// ie)
					// BuildPlugins.File = {D:\UERelease\Engine\Plugins\2D\Paper2D\Paper2D.uplugin}
					// { Paper2D, Paper2D Editor, ... (3개 더) }
					AddModuleToBinary(Module);
				}
			}

			// Add all of the extra modules, including game modules, that need to be compiled along with this app.
			// These modules are always statically linked in monolithic targets, but not necessarily linked to anything in modular targets,
			// and may still be required at runtime in order for the application to load and function properly!
			AddExtraModules();

			// Create all the modules referenced by the existing binaries
			foreach(BuildBinary Binary in AllApplicationBuildBinaries)
			{
				Binary.CreateAllDependentModules(FindOrCreateModuleByName);
			}

			// Bind every referenced C++ module to a binary
			// 위의 모듈들은 외부 모듈이였다면 여기는 본격적인 내부 모듈(In 엔진 모듈)
			// [0] = {D:\UERelease\Engine\Binaries\Win64\DefaultGame-Win64-Debug.exe}
			// DependencyModules = Count = 292 
			// => CORE_API{RulesFile{D:\UERelease\Engine\Source\Runtime\Core\Core.Build.cs}	Tools.DotNETCommon.FileReference}}
			//    PrivateIncludePathModules {TARGETPLATFORM_API, DERIVEDDATACACHE_API, INPUTDEVICE_API, ANALYTICS_API, RHI_API}
			// => DX11_API, D3D11RHI_API, DX12_API, RENDERER_API, RHI_API, RENDERCORE_API, ENGINE_API, LAUNCH_API, DefaultGame_API
			for (int Idx = 0; Idx < AllApplicationBuildBinaries.Count; ++Idx)
			{
				List<BuildModule> DependencyModules = AllApplicationBuildBinaries[Idx].GetAllDependencyModules(true, true);
				foreach (BuildModuleCPP DependencyModule in DependencyModules.OfType<BuildModuleCPP>())
				{
					// ModuleApiDefine = "BUILDSETTINGS_API", 
					if (DependencyModule.Binary == null)
					{
						AddModuleToBinary(DependencyModule);
					}
				}		
			}

#if DEBUG
			if(Rules.bBuildAllModules == false)
            {
				Debugger.Break();
            }
#endif

			// Add all the modules to the target if necessary.
			if(Rules.bBuildAllModules)
			{
				AddAllValidModulesToTarget();
			}

#if DEBUG
			Debugger.Break();
#endif

			// Add the external and non-C++ referenced modules to the binaries that reference them.
			foreach (BuildModuleCPP Module in Modules.Values.OfType<BuildModuleCPP>())
			{
				if(Module.Binary != null)
				{
					foreach (BuildModule ReferencedModule in Module.GetUnboundReferences())
					{
						Module.Binary.AddModule(ReferencedModule);
					}
				}
			}

			if (!bCompileMonolithic)
			{
				if (TargetPlatform == BuildTargetPlatform.Win64 || 
					TargetPlatform == BuildTargetPlatform.Win32)
				{
					// On Windows create import libraries for all binaries ahead of time, since linking binaries often causes bottlenecks
					foreach (BuildBinary Binary in AllApplicationBuildBinaries)
					{
						Binary.SetCreateImportLibrarySeparately(true);
					}
				}
				else
				{
					// On other platforms markup all the binaries containing modules with circular references
					foreach (BuildModule Module in Modules.Values.Where(x => x.Binary != null))
					{
						foreach (string CircularlyReferencedModuleName in Module.ModuleRule.CircularlyReferencedDependentModules)
						{
							if (Modules.TryGetValue(CircularlyReferencedModuleName, out BuildModule CircularlyReferencedModule) && CircularlyReferencedModule.Binary != null)
							{
								CircularlyReferencedModule.Binary.SetCreateImportLibrarySeparately(true);
							}
						}
					}
				}
			}
		}

		// Creates scripts for executing the pre-build scripts
		public FileReference[] CreatePreBuildScripts()
		{
			// Find all the pre-build steps
			List<Tuple<string[], BuildPlugin>> PreBuildCommandBatches = new List<Tuple<string[], BuildPlugin>>();
			if(ProjectDescriptor != null &&
			   ProjectDescriptor.PreBuildSteps != null)
			{
				AddCustomBuildSteps(ProjectDescriptor.PreBuildSteps, null, PreBuildCommandBatches);
			}
			if(0 < Rules.PreBuildSteps.Count)
			{
				PreBuildCommandBatches.Add(new Tuple<string[], BuildPlugin>(Rules.PreBuildSteps.ToArray(), null));
			}
			foreach(BuildPlugin BuildPlugin in BuildPlugins.Where(x => x.Descriptor.PreBuildSteps != null))
			{
				AddCustomBuildSteps(BuildPlugin.Descriptor.PreBuildSteps, BuildPlugin, PreBuildCommandBatches);
			}
			return WriteCustomBuildStepScripts
			(
                BuildHostPlatform.Current.Platform,
                ProjectIntermediateDirectory,
                Tag.ActionDescriptor.PreBuild, // ActionDecriptor as Descriptor
                PreBuildCommandBatches
			);
		}

		// Creates scripts for executing post-build steps
		private FileReference[] CreatePostBuildScripts()
		{
			// Find all the post-build steps
			List<Tuple<string[], BuildPlugin>> PostBuildCommandBatches = new List<Tuple<string[], BuildPlugin>>();
			if(!Rules.bDisableLinking)
			{
				if(ProjectDescriptor != null && 
				   ProjectDescriptor.PostBuildSteps != null)
				{
					AddCustomBuildSteps(ProjectDescriptor.PostBuildSteps, null, PostBuildCommandBatches);
				}
				if(0 < Rules.PostBuildSteps.Count)
				{
					PostBuildCommandBatches.Add(new Tuple<string[], BuildPlugin>(Rules.PostBuildSteps.ToArray(), null));
				}
				foreach(BuildPlugin BuildPlugin in BuildPlugins.Where(x => x.Descriptor.PostBuildSteps != null))
				{
					AddCustomBuildSteps(BuildPlugin.Descriptor.PostBuildSteps, BuildPlugin, PostBuildCommandBatches);
				}
			}
			return WriteCustomBuildStepScripts
			(
                BuildHostPlatform.Current.Platform,
                ProjectIntermediateDirectory, 
				Tag.ActionDescriptor.PostBuild, // ActionDecriptor as Descriptor
				PostBuildCommandBatches
			);
		}

		// Adds custom build steps from the given JSON object to the list of command batches
		private void AddCustomBuildSteps
		(
            CustomBuildSteps                   InCustomBuildSteps,
            BuildPlugin                        InPlugin,
            List<Tuple<string[], BuildPlugin>> CommandBatchesToReceive
		)
		{
			if (InCustomBuildSteps.TryGetCommands(BuildHostPlatform.Current.Platform, out string[] Commands))
			{
				CommandBatchesToReceive.Add(Tuple.Create(Commands, InPlugin));
			}
		}

		// Gets a list of variables that can be expanded in paths referenced by this target
		// EnvironmentVariable
		private Dictionary<string, string> GetTargetVariables(BuildPlugin CurrentPlugin)
		{
			Dictionary<string, string> VariableNamesToValues = new Dictionary<string, string>
			{
				{ Tag.EnvVar.RootDir,             BuildTool.RootDirectory.FullName },
				{ Tag.EnvVar.EngineDir,           BuildTool.EngineDirectory.FullName },
				{ Tag.EnvVar.EnterpriseDir,       BuildTool.EnterpriseDirectory.FullName },
				{ Tag.EnvVar.ProjectDir,          ProjectDirectory.FullName },
				{ Tag.EnvVar.TargetName,          TargetName },
				{ Tag.EnvVar.TargetPlatform,      TargetPlatform.ToString() },
				{ Tag.EnvVar.TargetConfiguration, Configuration.ToString() },
				{ Tag.EnvVar.TargetType,          TargetType.ToString() }
			};

			if (ProjectFile != null)
			{
				VariableNamesToValues.Add(Tag.EnvVar.ProjectFile, ProjectFile.FullName);
			}
			if(CurrentPlugin != null)
			{
				VariableNamesToValues.Add(Tag.EnvVar.PluginDir, CurrentPlugin.Directory.FullName);
			}
			return VariableNamesToValues;
		}

		// Write scripts containing the custom build steps for the given host platform
		private FileReference[] WriteCustomBuildStepScripts
		(
            BuildTargetPlatform               HostPlatform,
            DirectoryReference                 OutputScriptsDirectory,
            string                             FilePrefix,
            List<Tuple<string[], BuildPlugin>> CommandBatchesToBuildPlugins
		)
		{
			List<FileReference> ScriptFiles = new List<FileReference>();
			foreach(Tuple<string[], BuildPlugin> CommandBatch in CommandBatchesToBuildPlugins)
			{
				// Find all the standard variables
				Dictionary<string, string> Variables = GetTargetVariables(CommandBatch.Item2);

				// Get the output path to the script
				string ScriptExtension = (HostPlatform == BuildTargetPlatform.Win64)? Tag.Ext.Bat : Tag.Ext.Shell;
				FileReference ScriptFile = FileReference.Combine(OutputScriptsDirectory, String.Format("{0}-{1}{2}", FilePrefix, ScriptFiles.Count + 1, ScriptExtension));

				// Write it to disk
				List<string> Contents = new List<string>();
				if(HostPlatform == BuildTargetPlatform.Win64)
				{
					Contents.Insert(0, Tag.Command.Batch.EchoOff);
				}
				foreach(string Command in CommandBatch.Item1)
				{
					Contents.Add(StringUtils.ExpandVariables(Command, Variables));
				}

				if(!DirectoryReference.Exists(ScriptFile.Directory))
				{
					DirectoryReference.CreateDirectory(ScriptFile.Directory);
				}

				File.WriteAllLines(ScriptFile.FullName, Contents);

				// Add the output file to the list of generated scripts
				ScriptFiles.Add(ScriptFile);
			}
			return ScriptFiles.ToArray();
		}

		public static FileReference AddModuleFilenameSuffix(string ModuleName, FileReference FilePath, string Suffix)
		{
			int MatchPos = FilePath.FullName.LastIndexOf(ModuleName, StringComparison.InvariantCultureIgnoreCase);

			if (MatchPos < 0)
			{
				throw new BuildException("Failed to find module name \"{0}\" specified on the command line inside of the output filename \"{1}\" to add appendage.", ModuleName, FilePath);
			}

			string Appendage = "-" + Suffix;
			return new FileReference(FilePath.FullName.Insert(MatchPos + ModuleName.Length, Appendage));
		}

		// Finds a list of module names which can be hot-reloaded
		private HashSet<string> GetHotReloadModuleNames()
		{
			HashSet<string> HotReloadModuleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (!ShouldCompileMonolithic())
			{
				foreach (BuildBinary Binary in AllApplicationBuildBinaries)
				{
					List<BuildModule> GameModules = Binary.FindHotReloadModules();
					if (GameModules != null && 0 < GameModules.Count)
					{
						if (!BuildTool.IsProjectInstalled() || EnabledPlugins.Where(x => x.Type == PluginType.Mod).Any(x => Binary.OutputFilePaths[0].IsUnderDirectory(x.Directory)))
						{
							HotReloadModuleNames.UnionWith(GameModules.OfType<BuildModuleCPP>().Where(x => !x.ModuleRule.bUsePrecompiled).Select(x => x.ModuleRuleFileName));
						}
					}
				}
			}
			return HotReloadModuleNames;
		}

		// Determines which modules can be used to create shared PCHs
		// <param name="OriginalBinaries">The list of binaries</param>
		// <param name="GlobalCompileEnvironment">The compile environment. The shared PCHs will be added to the SharedPCHs list in this.</param>
		private void FindSharedPCHs(CppCompileEnvironment OutGlobalCppCompileEnvironment, List<BuildBinary> BuildBinariesToFindSharedPCH)
		{
			// OriginalBinaries안에서 LinkTogetherModules의 타입들의 모듈만 걸러내서,
			// SharedPCHModules로 정재.

			// SharedPCHModules중에서 ModuleRules의 ModuleRulesContext의 값 중 bCanUseForSharedPCH만 걸러내서,
			// NonEngineSharedPCH로 정재해봐서 존재하면 에러.

			// SharedPCHModules 중에서 Dependencies갯수로 정렬해서 SharedPCHModuleToPriority로 정재하고,
			// GlobalCompileEnvironment.SharedPCHs로
			// PCHTemplate안에 CppCompileEnvironment(API_DEFINE)을 넣어서 집어넣음.

			// Find how many other shared PCH modules each module depends on,
			// and use that to sort the shared PCHs by reverse order of size.
			HashSet<BuildModuleCPP> SharedPCHModules = new HashSet<BuildModuleCPP>();
			
			// *.Build.cs 중에서 SharedPCHHeaderFile을 작성한 CPP인 *.Build.cs만 모두 골라내기
			foreach(BuildBinary IterBinary in BuildBinariesToFindSharedPCH)
			{
				foreach(BuildModuleCPP IterCPPModule in IterBinary.LinkTogetherModules.OfType<BuildModuleCPP>())
				{
					if(IterCPPModule.ModuleRule.SharedPCHHeaderFile != null)
					{
						SharedPCHModules.Add(IterCPPModule);
					}
				}
			}

			// Shared PCHs are only supported for engine modules at the moment. Check there are no game modules in the list.
			List<BuildModuleCPP> NonEngineSharedPCHs = SharedPCHModules.Where(x => !x.ModuleRule.Context.bCanUseForSharedPCH).ToList();

			if(0 < NonEngineSharedPCHs.Count)
			{
				throw new BuildException("Shared PCHs are only supported for engine modules (found {0}).", String.Join(", ", NonEngineSharedPCHs.Select(x => x.ModuleRuleFileName)));
			}

			// Find a priority for each shared PCH, determined as the number of other shared PCHs it includes.
			Dictionary<BuildModuleCPP, int> SharedPCHModuleToPriority = new Dictionary<BuildModuleCPP, int>();

			// SharedPCHModules => { CoreUObject, Slate, Core, EditorEd, Engine }
			foreach(BuildModuleCPP IterSharedPCHModule in SharedPCHModules)
			{
				List<BuildModule> Dependencies = new List<BuildModule>();

				IterSharedPCHModule.RecursivelyGetAllDependencyModules(Dependencies, new HashSet<BuildModule>(), false, false, false);
				SharedPCHModuleToPriority.Add(IterSharedPCHModule, Dependencies.Count(x => SharedPCHModules.Contains(x)));
			}

			foreach(BuildModuleCPP IterModule in SharedPCHModuleToPriority.OrderByDescending(x => x.Value).Select(x => x.Key))
			{
				PCHTemplate SharedPCHTemplate = IterModule.CreateSharedPCHTemplate(this, OutGlobalCppCompileEnvironment);
				OutGlobalCppCompileEnvironment.SharedPCHs.Add(SharedPCHTemplate);
			}
		}

		// When building a target, this is called to add any additional modules that should be compiled along
		// with the main target.  If you override this in a derived class, remember to call the base implementation!
		protected virtual void AddExtraModules()
		{
			// Find all the extra module names
			List<string> ExtraModuleNames = new List<string>();
			ExtraModuleNames.AddRange(Rules.ExtraModuleNames);
			BuildPlatform.GetBuildPlatform(TargetPlatform).AddExtraModules(Rules, ExtraModuleNames);

			// Add extra modules that will either link into the main binary (monolithic), or be linked into separate DLL files (modular)
			// ExtraModuleName = "DefaultGame" (BuildMode에서)
			foreach (string ModuleName in ExtraModuleNames)
			{
				BuildModuleCPP Module = FindOrCreateCppModuleByName(ModuleName, TargetRulesFile.GetFileName());
				if (Module.Binary == null)
				{
					AddModuleToBinary(Module);
				}
			}
		}

		// Adds all the precompiled modules into the target.
		// Precompiled modules are compiled alongside the target, but not linked into it unless directly referenced.
		private void AddAllValidModulesToTarget()
		{
			// Find all the modules that are part of the target
			HashSet<string> ValidModuleNames = new HashSet<string>();
			foreach (BuildModuleCPP Module in Modules.Values.OfType<BuildModuleCPP>())
			{
				if(Module.Binary != null)
				{
					ValidModuleNames.Add(Module.ModuleRuleFileName);
				}
			}

			// Find all the platform folders to exclude from the list of valid modules
			ReadOnlyHashSet<string> ExcludeFolders = BuildPlatform.GetBuildPlatform(TargetPlatform).GetExcludedFolderNames();

			// Set of module names to build
			HashSet<string> FilteredModuleNames = new HashSet<string>();

			// Only add engine modules for non-program targets. Programs only compile whitelisted modules through plugins.
			if(TargetType != TargetType.Program)
			{
				// Find all the known module names in this assembly
				List<string> ModuleNames = new List<string>();
				RulesAssembly.GetAllModuleNames(ModuleNames);

				// Find all the directories containing engine modules that may be compatible with this target
				List<DirectoryReference> FindDirectories = new List<DirectoryReference>();

				if (TargetType == TargetType.Editor)
				{
					FindDirectories.AddRange(BuildTool.GetAllEngineDirectories(Tag.Directory.SourceCode + Tag.Directory.EditorOnly));
				}

				FindDirectories.AddRange(BuildTool.GetAllEngineDirectories(Tag.Directory.SourceCode + Tag.Directory.EngineCode));

				// Also allow anything in the developer directory in non-shipping configurations (though we blacklist by default unless the PrecompileForTargets
				// setting indicates that it's actually useful at runtime).
				if(Rules.bBuildDeveloperTools)
				{
					FindDirectories.AddRange(BuildTool.GetAllEngineDirectories(Tag.Directory.SourceCode + Tag.Directory.EngineAndEditor));
					FindDirectories.Add(DirectoryReference.Combine(BuildTool.EnterpriseSourceDirectory, Tag.Directory.EngineAndEditor));
				}

				// Find all the modules that are not part of the standard set
				foreach (string ModuleName in ModuleNames)
				{
					FileReference ModuleFileName = RulesAssembly.GetModuleFileName(ModuleName);

					if (FindDirectories.Any(BaseDir => ModuleFileName.IsUnderDirectory(BaseDir)))
					{
						Type RulesType = RulesAssembly.GetModuleRulesType(ModuleName);

						// Skip platform extension modules. We only care about the base modules, not the platform overrides.
						// The platform overrides get applied at a later stage when we actually come to build the module.
						if (!BuildPlatform.GetAllPlatformFolderNames().Any(Name => RulesType.Name.EndsWith("_" + Name)))
						{
							SupportedPlatformsAttribute SupportedPlatforms = RulesType.GetCustomAttribute<SupportedPlatformsAttribute>();
							if(SupportedPlatforms != null)
							{
								if(SupportedPlatforms.Platforms.Contains(TargetPlatform))
								{
									FilteredModuleNames.Add(ModuleName);
								}
							}
							else
							{
								if (!ModuleFileName.ContainsAnyNames(ExcludeFolders, BuildTool.EngineDirectory))
								{
									FilteredModuleNames.Add(ModuleName);
								}
							}
						}
					}
				}
			}

			// Add all the plugin modules that need to be compiled
			List<PluginInfo> Plugins = RulesAssembly.EnumeratePlugins().ToList();
			foreach(PluginInfo Plugin in Plugins)
			{
				// Ignore plugins which are specifically disabled by this target
				if (Rules.DisablePlugins.Contains(Plugin.Name))
				{
					continue;
				}

				// Ignore plugins without any modules
				if (Plugin.Descriptor.Modules == null)
				{
					continue;
				}

				// Disable any plugin which does not support the target platform.
				// The editor should update such references in the .uproject file on load.
				if (!Rules.bIncludePluginsForTargetPlatforms && !Plugin.Descriptor.SupportsTargetPlatform(TargetPlatform))
				{
					continue;
				}

				// Disable any plugin that requires the build platform
				if(Plugin.Descriptor.bRequiresBuildPlatform && ShouldExcludePlugin(Plugin, ExcludeFolders))
				{
					continue;
				}

				// Disable any plugins that aren't compatible with this program
				if (TargetType == TargetType.Program && 
					(Plugin.Descriptor.SupportedPrograms == null || !Plugin.Descriptor.SupportedPrograms.Contains(AppName)))
				{
					continue;
				}

				// Add all the modules
				foreach (ModuleDescriptor ModuleDescriptor in Plugin.Descriptor.Modules)
				{
					if (ModuleDescriptor.IsCompiledInConfiguration
					(
                        TargetPlatform,
                        Configuration,
                        TargetName,
                        TargetType,
                        Rules.bBuildDeveloperTools,
                        Rules.bBuildRequiresCookedData))
					{
						FileReference ModuleFileName = RulesAssembly.GetModuleFileName(ModuleDescriptor.ModuleName);
						if(ModuleFileName == null)
						{
							throw new BuildException("Unable to find module '{0}' referenced by {1}", ModuleDescriptor.ModuleName, Plugin.File);
						}
						if(!ModuleFileName.ContainsAnyNames(ExcludeFolders, Plugin.RootDirectory))
						{
							FilteredModuleNames.Add(ModuleDescriptor.ModuleName);
						}
					}
				}
			}

			// Create rules for each remaining module, and check that it's set to be compiled
			foreach(string FilteredModuleName in FilteredModuleNames)
			{
				// Try to create the rules object, but catch any exceptions if it fails. Some modules (eg. SQLite) may determine that they are unavailable in the constructor.
				ModuleRules ModuleRules;
				try
				{
					ModuleRules = RulesAssembly.RecursivelyCreateModuleRules(FilteredModuleName, this.Rules, "all modules option");
				}
				catch (BuildException)
				{
					ModuleRules = null;
				}

				// Figure out if it can be precompiled
				if (ModuleRules != null && ModuleRules.IsValidForTarget(ModuleRules.File))
				{
					ValidModuleNames.Add(FilteredModuleName);
				}
			}

			// Now create all the precompiled modules, making sure they don't reference anything that's not in the precompiled set
			HashSet<BuildModuleCPP> ValidModules = new HashSet<BuildModuleCPP>();
			foreach(string ModuleName in ValidModuleNames)
			{
				const string ReferenceChainMessage = "allmodules option";
				BuildModuleCPP Module = (BuildModuleCPP)FindOrCreateModuleByName(ModuleName, ReferenceChainMessage);
				Module.RecursivelyCreateModules(FindOrCreateModuleByName, ReferenceChainMessage);
				ValidModules.Add(Module);
			}

			// Make sure precompiled modules don't reference any non-precompiled modules
			foreach(BuildModuleCPP ValidModule in ValidModules)
			{
				foreach(BuildModuleCPP ReferencedModule in ValidModule.GetAllModules(false, true).OfType<BuildModuleCPP>())
				{
					if(!ValidModules.Contains(ReferencedModule))
					{
						Log.TraceWarning("Module '{0}' is not usable without module '{1}', which is not valid for this target.", 
							ValidModule.ModuleRuleFileName, ReferencedModule.ModuleRuleFileName);
					}
				}
			}

			// Make sure every module is built
			foreach(BuildModuleCPP Module in ValidModules)
			{
				if(Module.Binary == null)
				{
					AddModuleToBinary(Module);
				}
			}
		}

		// Note : 여기서 모듈 바이너리 추가.
		public void AddModuleToBinary(BuildModuleCPP Module)
		{
			if (ShouldCompileMonolithic())
			{
				// When linking monolithically, any unbound modules will be linked into the main executable
				Module.Binary = AllApplicationBuildBinaries[0];
				Module.Binary.AddModule(Module);
			}
			else
			{
				// Otherwise create a new module for it
				Module.Binary = CreateDynamicLibraryForModule(Module);
				AllApplicationBuildBinaries.Add(Module.Binary);
			}
		}

		// Finds the base output directory for build products of the given module
		private DirectoryReference GetBaseOutputDirectory(ModuleRules ModuleRules)
		{
			// Get the root output directory and base name (target name/app name) for this binary
			DirectoryReference BaseOutputDirectoryForCompiledObjectFiles;
			if(bUseSharedBuildEnvironment)
			{
				BaseOutputDirectoryForCompiledObjectFiles = ModuleRules.Context.DefaultOutputBaseDir;
			}
			else
			{
				BaseOutputDirectoryForCompiledObjectFiles = ProjectDirectory;
			}

			return BaseOutputDirectoryForCompiledObjectFiles;
		}

		// Finds the base output directory for a module
		private DirectoryReference GetModuleIntermediateDirectory(ModuleRules ModuleRules)
		{
			// Get the root output directory and base name (target name/app name) for this binary
			DirectoryReference BaseOutputDirectory = GetBaseOutputDirectory(ModuleRules);

			// Get the configuration that this module will be built in. Engine modules compiled in DebugGame will use Development.
			TargetConfiguration ModuleConfiguration = Configuration;
			if (Configuration == TargetConfiguration.DebugGame && 
				!ModuleRules.Context.bCanBuildDebugGame && 
				!ModuleRules.Name.Equals(Rules.LaunchModuleName, StringComparison.InvariantCultureIgnoreCase))
			{
				ModuleConfiguration = TargetConfiguration.Development;
			}

			// Get the output and intermediate directories for this module
			DirectoryReference IntermediateDirectory = DirectoryReference.Combine
			(
                BaseOutputDirectory,
                PlatformIntermediateFolder,
                AppName,
                ModuleConfiguration.ToString()
			);

			// Append a subdirectory if the module rules specifies one
			if (ModuleRules != null && ModuleRules.BinariesSubFolder.HasValue())
			{
				IntermediateDirectory = DirectoryReference.Combine(IntermediateDirectory, ModuleRules.BinariesSubFolder);
			}

			return DirectoryReference.Combine(IntermediateDirectory, ModuleRules.ShortName ?? ModuleRules.Name);
		}

		// Adds a dynamic library for the given module.
		// Does not check whether a binary already exists, or whether a binary should be created for this build configuration.
		// <param name="Module">The module to create a binary for</param>
		// <returns>The new binary. This has not been added to the target.</returns>
		private BuildBinary CreateDynamicLibraryForModule(BuildModuleCPP Module)
		{
			// Get the root output directory and base name (target name/app name) for this binary
			DirectoryReference BaseOutputDirectory = GetBaseOutputDirectory(Module.ModuleRule);
			DirectoryReference OutputDirectory     = DirectoryReference.Combine(BaseOutputDirectory, Tag.Directory.Binaries, TargetPlatform.ToString());

			// Append a subdirectory if the module rules specifies one
			if (Module.ModuleRule != null && 
				Module.ModuleRule.BinariesSubFolder.HasValue())
			{
				OutputDirectory = DirectoryReference.Combine(OutputDirectory, Module.ModuleRule.BinariesSubFolder);
			}

			// Get the configuration that this module will be built in. Engine modules compiled in DebugGame will use Development.
			TargetConfiguration ModuleConfiguration = Configuration;

			if (Configuration == TargetConfiguration.DebugGame && !Module.ModuleRule.Context.bCanBuildDebugGame)
			{
				ModuleConfiguration = TargetConfiguration.Development;
			}

			// Get the output filenames
			FileReference BaseBinaryPath = FileReference.Combine
			(
                OutputDirectory,
                MakeBinaryFileName
				(
					AppName + "-" + Module.ModuleRuleFileName, 
					TargetPlatform, 
					ModuleConfiguration, 
					Architecture, 
					Rules.UndecoratedConfiguration, 
					BuildBinaryType.DynamicLinkLibrary
				)
			);
			List<FileReference> OutputFilePaths 
				= BuildPlatform.GetBuildPlatform(TargetPlatform).FinalizeBinaryPaths(BaseBinaryPath, ProjectFile, Rules);

			// Create the binary
			return new BuildBinary
			(
				Type: BuildBinaryType.DynamicLinkLibrary,
				OutputFilePaths: OutputFilePaths,
				IntermediateDirectory: BuildModule.GeneratedDirectory,
				bAllowExports: true,
				bBuildAdditionalConsoleApp: false,
				PrimaryModule: Module,
				bUsePrecompiled: Module.ModuleRule.bUsePrecompiled
			);
		}

		// Makes a filename (without path) for a compiled binary (e.g. "Core-Win64-Debug.lib")
		public static string MakeBinaryFileName
		(
            string                    InBinaryName,
            BuildTargetPlatform      InPlatform,
            TargetConfiguration InConfiguration,
            string                    InTargetArchitecture,
            TargetConfiguration InUndecoratedConfiguration, // The target configuration which doesn't require a platform and configuration suffix. Development by default.
			BuildBinaryType           InBuildBinaryType
		)
		{
			StringBuilder Result = new StringBuilder();

			if (InPlatform == BuildTargetPlatform.Linux && 
			   (InBuildBinaryType == BuildBinaryType.DynamicLinkLibrary || InBuildBinaryType == BuildBinaryType.StaticLibrary))
			{
				Result.Append(Tag.Ext.Lib);
			}

			Result.Append(InBinaryName);

			if (InConfiguration != InUndecoratedConfiguration)
			{
				Result.AppendFormat("-{0}-{1}", InPlatform.ToString(), InConfiguration.ToString());
			}

			BuildPlatform BuildPlatform = BuildPlatform.GetBuildPlatform(InPlatform);
			if(BuildPlatform.RequiresArchitectureSuffix())
			{
				Result.Append(InTargetArchitecture);
			}

			Result.Append(BuildPlatform.GetBinaryExtension(InBuildBinaryType));

			return Result.ToString();
		}

		// Determine the output path for a target's executable
		public static List<FileReference> MakeBinaryPaths
		(
			DirectoryReference        BaseDirectory, // For executable directory, typically either the engine directory or project directory.
			string                    BinaryName, 
			BuildTargetPlatform      BinaryPlatform, 
			TargetConfiguration BinaryTargetConfiguration, 
			BuildBinaryType           BinaryBuildType, 
			string                    InArchitecture, 
			TargetConfiguration UndecoratedConfiguration, // The configuration which doesn't have a "-{$Platform}-{$Configuration}" suffix added to the binary
			string                    ExeSubFolder, // Subfolder for executables. May be null.
			FileReference             InProjectFile, 
			ReadOnlyTargetRules       InTargetRules
		)
		{
			// Build the binary path
			DirectoryReference BinaryDirectory = DirectoryReference.Combine(BaseDirectory, Tag.Directory.Binaries, BinaryPlatform.ToString());

			if (ExeSubFolder.HasValue())
			{
				BinaryDirectory = DirectoryReference.Combine(BinaryDirectory, ExeSubFolder);
			}

            FileReference BinaryFile = FileReference.Combine
            (
                BinaryDirectory,
                MakeBinaryFileName(BinaryName, BinaryPlatform, BinaryTargetConfiguration, InArchitecture, UndecoratedConfiguration, BinaryBuildType)
            );

            // Allow the platform to customize the output path (and output several executables at once if necessary)
            return BuildPlatform.GetBuildPlatform(BinaryPlatform).FinalizeBinaryPaths(BinaryFile, InProjectFile, InTargetRules);
		}

		// Sets up the plugins for this target
		public void SetupPlugins()
		{
			// Find all the valid plugins
			Dictionary<string, PluginInfo> NameToInfo 
				= RulesAssembly.EnumeratePlugins().ToDictionary(x => x.Name, x => x, StringComparer.InvariantCultureIgnoreCase);

			// Remove any plugins for platforms we don't have
			List<BuildTargetPlatform> MissingPlatforms = new List<BuildTargetPlatform>();
			foreach (BuildTargetPlatform TargetPlatform in BuildTargetPlatform.GetValidPlatforms())
			{
				if (BuildPlatform.GetBuildPlatform(TargetPlatform, true) == null)
				{
					MissingPlatforms.Add(TargetPlatform);
				}
			}

			// Get an array of folders to filter out
			string[] ExcludeFolders = MissingPlatforms.Select(x => x.ToString()).ToArray();

			// Set of all the plugins that have been referenced
			HashSet<string> ReferencedNames = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);

			// Map of plugin names to instances of that plugin
			Dictionary<string, BuildPlugin> NameToInstance = new Dictionary<string, BuildPlugin>(StringComparer.InvariantCultureIgnoreCase);

			// Set up the foreign plugin
			if(ForeignPlugin != null)
			{
				PluginReferenceDescriptor PluginReference = new PluginReferenceDescriptor(ForeignPlugin.GetFileNameWithoutExtension(), null, true);
				AddPlugin(PluginReference, "command line", ExcludeFolders, NameToInstance, NameToInfo);
			}

			// Configure plugins explicitly enabled via target settings
			foreach(string PluginName in Rules.EnablePlugins)
			{
				if(ReferencedNames.Add(PluginName))
				{
					PluginReferenceDescriptor PluginReference = new PluginReferenceDescriptor(PluginName, null, true);
					AddPlugin(PluginReference, "target settings", ExcludeFolders, NameToInstance, NameToInfo);
				}
			}

			// Configure plugins explicitly disabled via target settings
			foreach(string PluginName in Rules.DisablePlugins)
			{
				if(ReferencedNames.Add(PluginName))
				{
					PluginReferenceDescriptor PluginReference = new PluginReferenceDescriptor(PluginName, null, false);
					AddPlugin(PluginReference, "target settings", ExcludeFolders, NameToInstance, NameToInfo);
				}
			}

			bool bAllowEnginePluginsEnabledByDefault = true;

			// Find a map of plugins which are explicitly referenced in the project file
			if (ProjectDescriptor != null)
			{
				bAllowEnginePluginsEnabledByDefault = !ProjectDescriptor.DisableEnginePluginsByDefault;
				if (ProjectDescriptor.Plugins != null)
				{
					string ProjectReferenceChain = ProjectFile.GetFileName();
					foreach (PluginReferenceDescriptor PluginReference in ProjectDescriptor.Plugins)
					{
						if (!Rules.EnablePlugins.Contains(PluginReference.Name, StringComparer.InvariantCultureIgnoreCase) && 
							!Rules.DisablePlugins.Contains(PluginReference.Name, StringComparer.InvariantCultureIgnoreCase))
						{
							// Make sure we don't have multiple references to the same plugin
							if (!ReferencedNames.Add(PluginReference.Name))
							{
								Log.TraceWarning("Plugin '{0}' is listed multiple times in project file '{1}'.", PluginReference.Name, ProjectFile);
							}
							else
							{
								AddPlugin(PluginReference, ProjectReferenceChain, ExcludeFolders, NameToInstance, NameToInfo);
							}
						}
					}
				}
			}

			// Also synthesize references for plugins which are enabled by default
			if (Rules.bCompileAgainstEngine || 
				Rules.bCompileWithPluginSupport)
			{
				foreach(PluginInfo Plugin in NameToInfo.Values)
				{
					if(Plugin.IsEnabledByDefault(bAllowEnginePluginsEnabledByDefault) && !ReferencedNames.Contains(Plugin.Name))
					{
						ReferencedNames.Add(Plugin.Name);

						PluginReferenceDescriptor PluginReference = new PluginReferenceDescriptor(Plugin.Name, null, true)
						{
							SupportedTargetPlatforms = Plugin.Descriptor.SupportedTargetPlatforms,
							bOptional                = true
						};

						AddPlugin(PluginReference, "default plugins", ExcludeFolders, NameToInstance, NameToInfo);
					}
				}
			}

			// If this is a program, synthesize references for plugins which are enabled via the config file
			if(TargetType == TargetType.Program)
			{
                ConfigHierarchy EngineConfig = ConfigCache.ReadHierarchy
                    (
                        ConfigHierarchyType.Engine,
                        DirectoryReference.Combine(BuildTool.EngineDirectory, Tag.Directory.ExternalTools, TargetName),
                        TargetPlatform
                    );

                if (EngineConfig.GetArray(Tag.ConfigSection.Plugins, Tag.ConfigKey.ProgramEnabledPlugins, out List<string> PluginNames))
				{
					foreach (string PluginName in PluginNames)
					{
						if (ReferencedNames.Add(PluginName))
						{
							PluginReferenceDescriptor PluginReference = new PluginReferenceDescriptor(PluginName, null, true);
							AddPlugin(PluginReference, "DefaultEngine.ini", ExcludeFolders, NameToInstance, NameToInfo);
						}
					}
				}
			}

			// Create the list of enabled plugins
			EnabledPlugins = new List<BuildPlugin>(NameToInstance.Values);

			// Configure plugins explicitly built but not enabled via target settings
			foreach (string PluginName in Rules.BuildPlugins)
			{
				if (ReferencedNames.Add(PluginName))
				{
					PluginReferenceDescriptor PluginReference = new PluginReferenceDescriptor(PluginName, null, true);
					AddPlugin(PluginReference, "target settings", ExcludeFolders, NameToInstance, NameToInfo);
				}
			}

			// Set the list of plugins that should be built
			// [0] = {Paper2D}, [1] = {AISupport}, [2] = {LightPropagationVolume}, [3] = {CameraShakePreviewer}, ... [104] = WindowsMoviewPlayer
			BuildPlugins = new List<BuildPlugin>(NameToInstance.Values);
		}
		
		// Creates a plugin instance from a reference to it
		private BuildPlugin AddPlugin
		(
			PluginReferenceDescriptor       ReferenceToPlugin, 
			string                          ReferenceChainMessage, 
			string[]                        ExcludeFolders, 
			Dictionary<string, BuildPlugin> NameToBuildPlugin, 
			Dictionary<string, PluginInfo>  NameToPluginInfo
		)
		{
			// Ignore disabled references
			if(!ReferenceToPlugin.bEnabled)
			{
				return null;
			}

			// Try to get an existing reference to this plugin
			if (NameToBuildPlugin.TryGetValue(ReferenceToPlugin.Name, out BuildPlugin Instance))
			{
				// If this is a non-optional reference, make sure that and every referenced dependency is staged
				if (!ReferenceToPlugin.bOptional && !Instance.bDescriptorReferencedExplicitly)
				{
					Instance.bDescriptorReferencedExplicitly = true;
					if (Instance.Descriptor.Plugins != null)
					{
						foreach (PluginReferenceDescriptor NextReference in Instance.Descriptor.Plugins)
						{
							string NextReferenceChain = String.Format("{0} -> {1}", ReferenceChainMessage, Instance.File.GetFileName());
							AddPlugin(NextReference, NextReferenceChain, ExcludeFolders, NameToBuildPlugin, NameToPluginInfo);
						}
					}
				}
			}
			else
			{
				// Check if the plugin is required for this platform
				if (!ReferenceToPlugin.IsEnabledForPlatform(TargetPlatform)           || 
					!ReferenceToPlugin.IsEnabledForTargetConfiguration(Configuration) || 
					!ReferenceToPlugin.IsEnabledForTarget(TargetType))
				{
					Log.TraceLog("Ignoring plugin '{0}' (referenced via {1}) for platform/configuration", ReferenceToPlugin.Name, ReferenceChainMessage);
					return null;
				}

				// Disable any plugin reference which does not support the target platform
				if (!Rules.bIncludePluginsForTargetPlatforms && 
					!ReferenceToPlugin.IsSupportedTargetPlatform(TargetPlatform))
				{
					Log.TraceLog("Ignoring plugin '{0}' (referenced via {1}) due to unsupported target platform.", ReferenceToPlugin.Name, ReferenceChainMessage);
					return null;
				}

				// Find the plugin being enabled
				if (!NameToPluginInfo.TryGetValue(ReferenceToPlugin.Name, out PluginInfo Info))
				{
                    return ReferenceToPlugin.bOptional
                        ? (BuildPlugin)null
                        :  throw new BuildException("Unable to find plugin '{0}' (referenced via {1}). Install it and try again, or remove it from the required plugin list.", ReferenceToPlugin.Name, ReferenceChainMessage);
                }

				// Disable any plugin which does not support the target platform. The editor should update such references in the .uproject file on load.
				if (!Rules.bIncludePluginsForTargetPlatforms && !Info.Descriptor.SupportsTargetPlatform(TargetPlatform))
				{
					throw new BuildException("{0} is referenced via {1} with a mismatched 'SupportedTargetPlatforms' field. This will cause problems in packaged builds, because the .uplugin file will not be staged. Launch the editor to update references from your project file, or update references from other plugins manually.", Info.File.GetFileName(), ReferenceChainMessage);
				}

				// Disable any plugin that requires the build platform
				if (Info.Descriptor.bRequiresBuildPlatform && ShouldExcludePlugin(Info, ExcludeFolders))
				{
					Log.TraceLog("Ignoring plugin '{0}' (referenced via {1}) due to missing build platform", ReferenceToPlugin.Name, ReferenceChainMessage);
					return null;
				}

				// If this plugin supports UHT, flag that we need to pass the project file to UHT when generating headers
				if (Info.Descriptor.SupportedPrograms != null && Info.Descriptor.SupportedPrograms.Contains(Tag.Module.ExternalTool.HeaderTool))
				{
					bHasProjectScriptPlugin = true;
				}

				// Disable any plugins that aren't compatible with this program
				if (Rules.Type == TargetType.Program && (Info.Descriptor.SupportedPrograms == null || !Info.Descriptor.SupportedPrograms.Contains(AppName)))
				{
					Log.TraceLog("Ignoring plugin '{0}' (referenced via {1}) due to absence from supported programs list.", ReferenceToPlugin.Name, ReferenceChainMessage);
					return null;
				}

				// Create the new instance and add it to the cache
				Log.TraceLog("Enabling plugin '{0}' (referenced via {1})", ReferenceToPlugin.Name, ReferenceChainMessage);
				Instance = new BuildPlugin(Info) { bDescriptorReferencedExplicitly = !ReferenceToPlugin.bOptional };
				NameToBuildPlugin.Add(Info.Name, Instance);

				// Get the reference chain for this plugin
				string PluginReferenceChain = String.Format("{0} -> {1}", ReferenceChainMessage, Info.File.GetFileName());

				// Create modules for this plugin
				//UEBuildBinaryType BinaryType = ShouldCompileMonolithic() ? UEBuildBinaryType.StaticLibrary : UEBuildBinaryType.DynamicLinkLibrary;
				if (Info.Descriptor.Modules != null)
				{
					foreach (ModuleDescriptor ModuleInfo in Info.Descriptor.Modules)
					{
						if (ModuleInfo.IsCompiledInConfiguration(TargetPlatform, Configuration, TargetName, TargetType, Rules.bBuildDeveloperTools, Rules.bBuildRequiresCookedData))
						{
							BuildModuleCPP Module = FindOrCreateCppModuleByName(ModuleInfo.ModuleName, PluginReferenceChain);
							if (!Instance.Modules.Contains(Module))
							{
								// This could be in a child plugin so scan thorugh those as well
								if (!Module.RulesFile.IsUnderDirectory(Info.RootDirectory) && !Info.ChildFiles.Any(ChildFile => Module.RulesFile.IsUnderDirectory(ChildFile.Directory)))
								{
									throw new BuildException("Plugin '{0}' (referenced via {1}) does not contain the '{2}' module, but lists it in '{3}'.", Info.Name, ReferenceChainMessage, ModuleInfo.ModuleName, Info.File);
								}
								Instance.bDescriptorNeededAtRuntime = true;
								Instance.Modules.Add(Module);
							}
						}
					}
				}

				// Create the dependencies set
				HashSet<BuildPlugin> Dependencies = new HashSet<BuildPlugin>();
				if (Info.Descriptor.Plugins != null)
				{
					foreach (PluginReferenceDescriptor NextReference in Info.Descriptor.Plugins)
					{
						BuildPlugin NextInstance = AddPlugin(NextReference, PluginReferenceChain, ExcludeFolders, NameToBuildPlugin, NameToPluginInfo);
						if (NextInstance != null)
						{
							Dependencies.Add(NextInstance);
							if (NextInstance.Dependencies == null)
							{
								throw new BuildException("Found circular dependency from plugin '{0}' onto itself.", NextReference.Name);
							}
							Dependencies.UnionWith(NextInstance.Dependencies);
						}
					}
				}
				Instance.Dependencies = Dependencies;

				// Stage the descriptor if the plugin contains content
				if (Info.Descriptor.bCanContainContent || Dependencies.Any(x => x.bDescriptorNeededAtRuntime))
				{
					Instance.bDescriptorNeededAtRuntime = true;
				}
			}

			return Instance;
		}

		// Checks whether a plugin path contains a platform directory fragment
		private bool ShouldExcludePlugin(PluginInfo Plugin, IEnumerable<string> ExcludeFolders)
		{
			if (Plugin.LoadedFrom == PluginLoadedFrom.Engine)
			{
				return Plugin.File.ContainsAnyNames(ExcludeFolders, BuildTool.EngineDirectory);
			}
			else if(ProjectFile != null)
			{
				return Plugin.File.ContainsAnyNames(ExcludeFolders, ProjectFile.Directory);
			}
			else
			{
				return false;
			}
		}

		// Sets up the binaries for the target.
		private void SetupAndAddLaunchModule()
		{
			// If we're using the new method for specifying binaries, fill in the binary configurations now
			if(Rules.LaunchModuleName == null)
			{
				throw new BuildException("LaunchModuleName must be set for all targets.");
			}
			BuildModuleCPP LaunchModule = FindOrCreateCppModuleByName(Rules.LaunchModuleName, TargetRulesFile.GetFileName());

			bool bOutputToPlatformExtensionDirectory = Rules.File.IsUnderDirectory(BuildTool.EnginePlatformExtensionsDirectory) || 
				                                       Rules.File.IsUnderDirectory(BuildTool.AppendSuffixPlatforms(ProjectDirectory));

			bool bOutputToProjectDirectory = (bCompileMonolithic || !bUseSharedBuildEnvironment) && 
				                             (ProjectDirectory != BuildTool.EngineDirectory);

			// Construct the output paths for this target's executable
			DirectoryReference OutputDirectory = BuildTool.EngineDirectory;

			if (bOutputToPlatformExtensionDirectory && bOutputToProjectDirectory)
			{
				OutputDirectory = BuildTool.GetAllProjectDirectories(ProjectDirectory).First(x => x != ProjectDirectory && Rules.File.IsUnderDirectory(x));
			}
			else if(bOutputToPlatformExtensionDirectory)
			{
				OutputDirectory = BuildTool.GetAllEngineDirectories().First(x => x != BuildTool.EngineDirectory && Rules.File.IsUnderDirectory(x));
			} 
			else if (bOutputToProjectDirectory)
			{
				OutputDirectory = ProjectDirectory;
			}

			bool bCompileAsDLL = Rules.bShouldCompileAsDLL && bCompileMonolithic;

			List<FileReference> OutputPaths = MakeBinaryPaths
			(
				OutputDirectory, 
				bCompileMonolithic ? TargetName : AppName, 
				TargetPlatform, 
				Configuration, 
				bCompileAsDLL ? BuildBinaryType.DynamicLinkLibrary : BuildBinaryType.Executable, 
				Rules.Architecture, 
				Rules.UndecoratedConfiguration, 
				// bCompileMonolithic && ProjectFile != null, 
				Rules.ExeBinariesSubFolder, 
				ProjectFile, 
				Rules
			);

			// Get the intermediate directory for the launch module directory. This can differ from the standard engine intermediate directory because it is always configuration-specific.
			DirectoryReference IntermediateDirectory;

			if (LaunchModule.RulesFile.IsUnderDirectory(BuildTool.EngineDirectory) && !ShouldCompileMonolithic())
			{
				IntermediateDirectory = DirectoryReference.Combine(BuildTool.EngineDirectory, PlatformIntermediateFolder, AppName, Configuration.ToString());
			}
			else
			{
				// ProjectIntermediateDirectory = {D:\UERelease\Engine\Intermediate\Build\Win64\DefaultGame\Debug}
				IntermediateDirectory = ProjectIntermediateDirectory;
			}

			// Create the binary
			BuildBinary Binary = new BuildBinary
			(
				Type                      : Rules.bShouldCompileAsDLL? BuildBinaryType.DynamicLinkLibrary : BuildBinaryType.Executable,
				OutputFilePaths           : OutputPaths,
				IntermediateDirectory     : IntermediateDirectory,
				bAllowExports             : Rules.bHasExports,
				bBuildAdditionalConsoleApp: Rules.bBuildAdditionalConsoleApp,
				PrimaryModule             : LaunchModule,
				bUsePrecompiled           : LaunchModule.ModuleRule.bUsePrecompiled && OutputPaths[0].IsUnderDirectory(BuildTool.EngineDirectory)
			);

			AllApplicationBuildBinaries.Add(Binary);

			// Add the launch module to it
			LaunchModule.Binary = Binary;
			Binary.AddModule(LaunchModule);
		}

		// Sets up the global compile and link environment for the target. 
		// i.e) Definition.h (for c++ Engine source code)
		private void SetupGlobalEnvironment
		(
            CppCompileEnvironment OutGlobalCompileEnvironment,
            LinkEnvironment       OutGlobalLinkEnvironment,
            ToolChain             ToolChain
		)
		{
			BuildPlatform BuildPlatform = BuildPlatform.GetBuildPlatform(TargetPlatform);

			// Doing anything in Win64, MSVC
			ToolChain.SetUpGlobalEnvironment(Rules);

			// @Hack: This to prevent UHT from listing CoreUObject.init.gen.cpp as its dependency.
			// We flag the compile environment when we build UHT so that we don't need to check
			// this for each file when generating their dependencies.
			OutGlobalCompileEnvironment.bHackHeaderGenerator = (AppName == Tag.Module.ExternalTool.HeaderTool);

			OutGlobalCompileEnvironment.Definitions.Add(String.Format(Tag.CppContents.Def.IsProgram + "={0}", TargetType == TargetType.Program ? "1" : "0"));
			OutGlobalCompileEnvironment.Definitions.AddRange(Rules.GlobalDefinitions);

			OutGlobalCompileEnvironment.bUseDebugCRT                         = OutGlobalCompileEnvironment.Configuration == CppConfiguration.Debug && Rules.bDebugBuildsActuallyUseDebugCRT;
			OutGlobalCompileEnvironment.bEnableOSX109Support                 = Rules.bEnableOSX109Support;
			OutGlobalCompileEnvironment.bUseSharedBuildEnvironment           = (Rules.BuildEnvironment == TargetBuildEnvironment.Shared);
			OutGlobalCompileEnvironment.bEnableExceptions                    = Rules.bForceEnableExceptions || Rules.bBuildEditor;
			OutGlobalCompileEnvironment.bEnableObjectiveCExceptions          = Rules.bForceEnableObjCExceptions || Rules.bBuildEditor;
			OutGlobalCompileEnvironment.ShadowVariableWarningLevel           = Rules.ShadowVariableWarningLevel;
			OutGlobalCompileEnvironment.UnsafeTypeCastWarningLevel           = Rules.UnsafeTypeCastWarningLevel;
			OutGlobalCompileEnvironment.bUndefinedIdentifierWarningsAsErrors = Rules.bUndefinedIdentifierErrors;
			OutGlobalCompileEnvironment.bOptimizeForSize                     = Rules.bCompileForSize;
			OutGlobalCompileEnvironment.bUseStaticCRT                        = Rules.bUseStaticCRT;
			OutGlobalCompileEnvironment.bOmitFramePointers                   = Rules.bOmitFramePointers;
			OutGlobalCompileEnvironment.bUsePDBFiles                         = Rules.bUsePDBFiles;
			OutGlobalCompileEnvironment.bSupportEditAndContinue              = Rules.bSupportEditAndContinue;
			OutGlobalCompileEnvironment.bPreprocessOnly                      = Rules.bPreprocessOnly;
			OutGlobalCompileEnvironment.bUseIncrementalLinking               = Rules.bUseIncrementalLinking;
			OutGlobalCompileEnvironment.bAllowLTCG                           = Rules.bAllowLTCG;
			OutGlobalCompileEnvironment.bPGOOptimize                         = Rules.bPGOOptimize;
			OutGlobalCompileEnvironment.bPGOProfile                          = Rules.bPGOProfile;
			OutGlobalCompileEnvironment.bAllowRemotelyCompiledPCHs           = Rules.bAllowRemotelyCompiledPCHs;
			OutGlobalCompileEnvironment.bCheckSystemHeadersForModification   = Rules.bCheckSystemHeadersForModification;
			OutGlobalCompileEnvironment.bPrintTimingInfo                     = Rules.bPrintToolChainTimingInfo;
			OutGlobalCompileEnvironment.bUseRTTI                             = Rules.bForceEnableRTTI;
			OutGlobalCompileEnvironment.bUseInlining                         = Rules.bUseInlining;
			OutGlobalCompileEnvironment.bCompileISPC                         = Rules.bCompileISPC;
			OutGlobalCompileEnvironment.bHideSymbolsByDefault                = !Rules.bPublicSymbolsByDefault;
			OutGlobalCompileEnvironment.CppStandard                          = Rules.CppStandard;
			OutGlobalCompileEnvironment.AdditionalArguments                  = Rules.AdditionalCompilerArguments;

			OutGlobalLinkEnvironment.bIsBuildingConsoleApplication = Rules.bIsBuildingConsoleApplication;
			OutGlobalLinkEnvironment.bOptimizeForSize              = Rules.bCompileForSize;
			OutGlobalLinkEnvironment.bOmitFramePointers            = Rules.bOmitFramePointers;
			OutGlobalLinkEnvironment.bSupportEditAndContinue       = Rules.bSupportEditAndContinue;
			OutGlobalLinkEnvironment.bCreateMapFile                = Rules.bCreateMapFile;
			OutGlobalLinkEnvironment.bHasExports                   = Rules.bHasExports;
			OutGlobalLinkEnvironment.bAllowASLR                    = (OutGlobalCompileEnvironment.Configuration == CppConfiguration.Shipping && Rules.bAllowASLRInShipping);
			OutGlobalLinkEnvironment.bUsePDBFiles                  = Rules.bUsePDBFiles;
			OutGlobalLinkEnvironment.BundleDirectory               = BuildPlatform.GetBundleDirectory(Rules, AllApplicationBuildBinaries[0].OutputFilePaths);
			OutGlobalLinkEnvironment.BundleVersion                 = Rules.BundleVersion;
			OutGlobalLinkEnvironment.bAllowLTCG                    = Rules.bAllowLTCG;
			OutGlobalLinkEnvironment.bPGOOptimize                  = Rules.bPGOOptimize;
			OutGlobalLinkEnvironment.bPGOProfile                   = Rules.bPGOProfile;
			OutGlobalLinkEnvironment.bUseIncrementalLinking        = Rules.bUseIncrementalLinking;
			OutGlobalLinkEnvironment.bUseFastPDBLinking            = Rules.bUseFastPDBLinking ?? false;
			OutGlobalLinkEnvironment.bPrintTimingInfo              = Rules.bPrintToolChainTimingInfo;
			OutGlobalLinkEnvironment.AdditionalArguments           = Rules.AdditionalLinkerArguments;

			if (Rules.bPGOOptimize && Rules.bPGOProfile)
			{
				throw new BuildException("bPGOProfile and bPGOOptimize are mutually exclusive.");
			}

			OutGlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.EnableProfileGuidedOptimization + (Rules.bPGOProfile ? Tag.Boolean.One : Tag.Boolean.Zero));

			// Toggle to enable vorbis for audio streaming where available
			OutGlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.UseVorbisForStream + Tag.Boolean.One);

			// Toggle to enable XMA for audio streaming where available (XMA2 only supports up to stereo streams - surround streams will fall back to Vorbis etc)
			OutGlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.UseXMA2ForStream + Tag.Boolean.One);

			// Add the 'Engine/Source' path as a global include path for all modules
			OutGlobalCompileEnvironment.UserIncludePaths.Add(BuildTool.EngineSourceDirectory);

			//@todo.PLATFORM: Do any platform specific tool chain initialization here if required

			TargetConfiguration EngineTargetConfiguration = Configuration == TargetConfiguration.DebugGame ? TargetConfiguration.Development : Configuration;
			DirectoryReference        LinkIntermediateDirectory = DirectoryReference.Combine(BuildTool.EngineDirectory, PlatformIntermediateFolder, AppName, EngineTargetConfiguration.ToString());

			// Installed Engine intermediates go to the project's intermediate folder. Installed Engine never writes to the engine intermediate folder. (Those files are immutable)
			// Also, when compiling in monolithic, all intermediates go to the project's folder.  This is because a project can change definitions that affects all engine translation
			// units too, so they can't be shared between different targets.  They are effectively project-specific engine intermediates.
			if (BuildTool.IsEngineInstalled() || (ProjectFile != null && ShouldCompileMonolithic()))
			{
				if (ProjectFile != null)
				{
					LinkIntermediateDirectory = DirectoryReference.Combine(ProjectFile.Directory, PlatformIntermediateFolder, AppName, Configuration.ToString());
				}
				else if (ForeignPlugin != null)
				{
					LinkIntermediateDirectory = DirectoryReference.Combine(ForeignPlugin.Directory, PlatformIntermediateFolder, AppName, Configuration.ToString());
				}
			}

			// Put the non-executable output files (PDB, import library, etc) in the intermediate directory.
			OutGlobalLinkEnvironment.IntermediateDirectory = LinkIntermediateDirectory;
			OutGlobalLinkEnvironment.OutputDirectory       = OutGlobalLinkEnvironment.IntermediateDirectory;

			// By default, shadow source files for this target in the root OutputDirectory
			OutGlobalLinkEnvironment.LocalShadowDirectory = OutGlobalLinkEnvironment.OutputDirectory;

			#region SET_MACRO_COMPILE_ENVIRONMENT
			if (Rules.ExeBinariesSubFolder.HasValue())
			{
				OutGlobalCompileEnvironment.Definitions.Add(String.Format(Tag.CppContents.Def.EngineBaseDirAdjust + Rules.ExeBinariesSubFolder.Replace('\\', '/').Trim('/').Count(x => x == '/') + 1));
			}

			if (Rules.bForceCompileDevelopmentAutomationTests)
			{
				OutGlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.WithDevAutomationTest + Tag.Boolean.One);
			}
			else
			{
				switch(Configuration)
				{
					case TargetConfiguration.Test:
					case TargetConfiguration.Shipping:
						OutGlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.WithDevAutomationTest + Tag.Boolean.Zero);
						break;
					default:
						OutGlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.WithDevAutomationTest + Tag.Boolean.One);
						break;
				}
			}

			if (Rules.bForceCompilePerformanceAutomationTests)
			{
				OutGlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.WithPerformanceAutomationTests + Tag.Boolean.One);
			}
			else
			{
				switch (Configuration)
				{
					case TargetConfiguration.Shipping:
						OutGlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.WithPerformanceAutomationTests + Tag.Boolean.Zero);
						break;
					default:
						OutGlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.WithPerformanceAutomationTests + Tag.Boolean.One);
						break;
				}
			}

			OutGlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.Unicode);
			OutGlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def._Unicode);

			OutGlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.IsMonolithic + (ShouldCompileMonolithic() ? Tag.Boolean.One : Tag.Boolean.Zero));

			OutGlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.WithEngine + (Rules.bCompileAgainstEngine ? Tag.Boolean.One : Tag.Boolean.Zero));
			OutGlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.WithDeveloperTools + (Rules.bBuildDeveloperTools ? Tag.Boolean.One : Tag.Boolean.Zero));

			// Set a macro to control whether to initialize ApplicationCore. Command line utilities should not generally need this.
			OutGlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.WithApplicationCore + (Rules.bCompileAgainstApplicationCore ? Tag.Boolean.One : Tag.Boolean.Zero));

			OutGlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.WithCoreUObject + (Rules.bCompileAgainstCoreUObject ? Tag.Boolean.One : Tag.Boolean.Zero));

			OutGlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.UseStatsWithoutEngine + (Rules.bCompileWithStatsWithoutEngine ? Tag.Boolean.One : Tag.Boolean.Zero));

			OutGlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.WithPluginSupport + (Rules.bCompileWithPluginSupport ? Tag.Boolean.One : Tag.Boolean.Zero));

			OutGlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.WithAccessiblity + (Rules.bCompileWithAccessibilitySupport && !Rules.bIsBuildingConsoleApplication ? Tag.Boolean.One : Tag.Boolean.Zero));

			OutGlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.WithPerfConters + (Rules.bWithPerfCounters ? Tag.Boolean.One : Tag.Boolean.Zero));

			OutGlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.UseLoggingInShipping + (Rules.bUseLoggingInShipping ? Tag.Boolean.One : Tag.Boolean.Zero));

			OutGlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.UseLoggingInShipping + (Rules.bLoggingToMemoryEnabled ? Tag.Boolean.One : Tag.Boolean.Zero));

			OutGlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.UseCachedFreedOSAllocs + (Rules.bUseCacheFreedOSAllocs ? Tag.Boolean.One : Tag.Boolean.Zero));

			OutGlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.UseChecksInShipping + (Rules.bUseChecksInShipping ? Tag.Boolean.One : Tag.Boolean.Zero));
			// bBuildEditor has now been set appropriately for all platforms, so this is here to make sure the #define
			OutGlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.WithEditor + (Rules.bBuildEditor ? Tag.Boolean.One : Tag.Boolean.Zero));
			
			OutGlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.WithEditoronlyData + (Rules.bBuildWithEditorOnlyData ? Tag.Boolean.One : Tag.Boolean.Zero));

			// if (Rules.bBuildWithEditorOnlyData == false)
			// {
			// 	OutGlobalCompileEnvironment.Definitions.Add("WITH_EDITORONLY_DATA=0");
			// }

			// Check if server-only code should be compiled out.
			OutGlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.WithServerCode + (Rules.bWithServerCode ? Tag.Boolean.One : Tag.Boolean.Zero));

			// Set the defines for Push Model
			OutGlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.WithPushModel + (Rules.bWithPushModel ? Tag.Boolean.One : Tag.Boolean.Zero));

			// Set the define for whether we're compiling with CEF3
			if (Rules.bCompileCEF3 && (TargetPlatform == BuildTargetPlatform.Win32 || TargetPlatform == BuildTargetPlatform.Win64 || TargetPlatform == BuildTargetPlatform.Mac || TargetPlatform == BuildTargetPlatform.Linux))
			{
				OutGlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.WithCEF3 + Tag.Boolean.One);
			}
			else
			{
				OutGlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.WithCEF3 + Tag.Boolean.Zero);
			}


			// Set the define for enabling live coding
			if(Rules.bWithLiveCoding)
			{
				OutGlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.WithLiveCoding + Tag.Boolean.One);
				if (Rules.LinkType == TargetLinkType.Monolithic)
				{
					OutGlobalCompileEnvironment.Definitions.Add(String.Format(Tag.CppContents.Def.LiveCodingEngineDir + "=\"{0}\"", BuildTool.EngineDirectory.FullName.Replace("\\", "\\\\")));
					if(ProjectFile != null)
					{
						OutGlobalCompileEnvironment.Definitions.Add(String.Format(Tag.CppContents.Def.LiveCodingProjectDir + "=\"{0}\"", ProjectFile.FullName.Replace("\\", "\\\\")));
					}
				}
			}
			else
			{
				OutGlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.WithLiveCoding + Tag.Boolean.Zero);
			}

			if (Rules.bUseXGEController &&
				Rules.Type == TargetType.Editor &&
				(TargetPlatform == BuildTargetPlatform.Win32 || TargetPlatform == BuildTargetPlatform.Win64))
			{
				OutGlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.WithXGEController + Tag.Boolean.One);
			}
			else
			{
				OutGlobalCompileEnvironment.Definitions.Add(Tag.CppContents.Def.WithXGEController + Tag.Boolean.Zero);
			}

			// Compile in the names of the module manifests
			OutGlobalCompileEnvironment.Definitions.Add(String.Format(Tag.CppContents.Def.BuildToolModuleManifest + "=\"{0}\"", ModuleManifest.GetStandardFileName(AppName, TargetPlatform, Configuration, Architecture, false)));
			OutGlobalCompileEnvironment.Definitions.Add(String.Format(Tag.CppContents.Def.BuildToolModuleManifestDebuggame + "=\"{0}\"", ModuleManifest.GetStandardFileName(AppName, TargetPlatform, TargetConfiguration.DebugGame, Architecture, true)));

			// tell the compiled code the name of the UBT platform (this affects folder on disk, etc that the game may need to know)
			OutGlobalCompileEnvironment.Definitions.Add(String.Format(Tag.CppContents.Def.BuildToolCompiledPlatform +"=" + TargetPlatform.ToString()));
			OutGlobalCompileEnvironment.Definitions.Add(String.Format(Tag.CppContents.Def.BuildToolCompiledTarget + "=" + TargetType.ToString()));

			// Set the global app name
			OutGlobalCompileEnvironment.Definitions.Add(String.Format(Tag.CppContents.Def.AppName + "=\"{0}\"", AppName));

			#endregion SET_MACRO_COMPILE_ENVIRONMENT

			// Initialize the compile and link environments for the platform, configuration, and project.
			BuildPlatform.SetUpEnvironment(Rules, OutGlobalCompileEnvironment, OutGlobalLinkEnvironment);

			// 이 세개
			// GlobalCompileEnvironment.Definitions.Add()
			// GlobalCompileEnvironment.bCreateDebugInfo =
			// GlobalLinkeEnvironment.bCreateDebugInfo = 
			BuildPlatform.SetUpConfigurationEnvironment(Rules, OutGlobalCompileEnvironment, OutGlobalLinkEnvironment);
		}

		static CppConfiguration GetCppConfiguration(TargetConfiguration Configuration)
		{
			switch (Configuration)
			{
				case TargetConfiguration.Debug:
					return CppConfiguration.Debug;
				case TargetConfiguration.DebugGame:
				case TargetConfiguration.Development:
					return CppConfiguration.Development;
				case TargetConfiguration.Shipping:
					return CppConfiguration.Shipping;
				case TargetConfiguration.Test:
					return CppConfiguration.Shipping;
				default:
					throw new BuildException("Unhandled target configuration");
			}
		}

		// Create a rules object for the given module, and set any default values for this target
		private ModuleRules CreateModuleRulesAndSetDefaults(string ModuleName, string ReferenceChainMessage)
		{
			// Create the rules from the assembly
			ModuleRules RulesObject = RulesAssembly.RecursivelyCreateModuleRules(ModuleName, Rules, ReferenceChainMessage);

			// Set whether the module requires an IMPLEMENT_MODULE macro
			if(!RulesObject.bRequiresImplementModule.HasValue)
			{
				RulesObject.bRequiresImplementModule 
					= RulesObject.Type == ModuleRules.ModuleType.CPlusPlus && 
					  RulesObject.Name != Rules.LaunchModuleName;
			}

			// Reads additional dependencies array for project module from project file and fills PrivateDependencyModuleNames.
			if (ProjectDescriptor != null && 
				ProjectDescriptor.Modules != null)
			{
				ModuleDescriptor Module = ProjectDescriptor.Modules.FirstOrDefault(x => x.ModuleName.Equals(ModuleName, StringComparison.InvariantCultureIgnoreCase));
				if (Module != null && Module.AdditionalDependencies != null)
				{
					RulesObject.PrivateDependencyModuleNames.AddRange(Module.AdditionalDependencies);
				}
			}

			// Make sure include paths don't end in trailing slashes. This can result in enclosing quotes being escaped when passed to command line tools.
			RemoveTrailingSlashes(RulesObject.PublicIncludePaths);
			RemoveTrailingSlashes(RulesObject.PublicSystemIncludePaths);
			RemoveTrailingSlashes(RulesObject.PrivateIncludePaths);
			RemoveTrailingSlashes(RulesObject.PublicSystemLibraryPaths);

			// Validate rules object
			if (RulesObject.Type == ModuleRules.ModuleType.CPlusPlus)
			{
				List<string> InvalidDependencies = RulesObject.DynamicallyLoadedModuleNames.Intersect(RulesObject.PublicDependencyModuleNames.Concat(RulesObject.PrivateDependencyModuleNames)).ToList();
				if (InvalidDependencies.Count != 0)
				{
					throw new BuildException("Module rules for '{0}' should not be dependent on modules which are also dynamically loaded: {1}", ModuleName, String.Join(", ", InvalidDependencies));
				}

				// Make sure that engine modules use shared PCHs or have an explicit private PCH
				if(RulesObject.PCHUsage == ModuleRules.PCHUsageMode.NoSharedPCHs && RulesObject.PrivatePCHHeaderFile == null)
				{
					if(ProjectFile == null || !RulesObject.File.IsUnderDirectory(ProjectFile.Directory))
					{
						Log.TraceWarning("{0} module has shared PCHs disabled, but does not have a private PCH set", ModuleName);
					}
				}

				// If we can't use a shared PCH, check there's a private PCH set
				if(RulesObject.PCHUsage != ModuleRules.PCHUsageMode.NoPCHs && RulesObject.PCHUsage != ModuleRules.PCHUsageMode.UseExplicitOrSharedPCHs && RulesObject.PrivatePCHHeaderFile == null)
				{
					// Try to figure out the legacy PCH file
					FileReference CppFile = DirectoryReference.EnumerateFiles(RulesObject.Directory, Tag.PlaceHolder.WildCard + Tag.Ext.CppSource, SearchOption.AllDirectories).FirstOrDefault();
					if(CppFile != null)
					{
						string IncludeFile = MetadataCache.GetFirstInclude(FileItem.GetItemByFileReference(CppFile));
						if(IncludeFile != null)
						{
							FileReference PchIncludeFile = DirectoryReference.EnumerateFiles(RulesObject.Directory, Path.GetFileName(IncludeFile), SearchOption.AllDirectories).FirstOrDefault();
							if(PchIncludeFile != null)
							{
								RulesObject.PrivatePCHHeaderFile = PchIncludeFile.MakeRelativeTo(RulesObject.Directory).Replace(Path.DirectorySeparatorChar, '/');
							}
						}
					}

					// Print a suggestion for which file to include
					if(RulesObject.PrivatePCHHeaderFile == null)
					{
						Log.TraceWarningOnce(RulesObject.File, "Modules must specify an explicit precompiled header (eg. PrivatePCHHeaderFile = \"Private/{0}PrivatePCH.h\") ", ModuleName);
					}
					else
					{
						Log.TraceWarningOnce(RulesObject.File, "Modules must specify an explicit precompiled header (eg. PrivatePCHHeaderFile = \"{0}\")", RulesObject.PrivatePCHHeaderFile);
					}
				}
			}
			return RulesObject;
		}

		// Utility function to remove trailing slashes from a list of paths
		private static void RemoveTrailingSlashes(List<string> PathsToProcess)
		{
			for(int Idx = 0; Idx < PathsToProcess.Count; ++Idx)
			{
				PathsToProcess[Idx] = PathsToProcess[Idx].TrimEnd('\\');
			}
		}

		// Finds a module given its name.  Throws an exception if the module couldn't be found.
		// ReferenceChainMessage ($(Target)->$(*.Build.cs))
		public BuildModule FindOrCreateModuleByName(string ModuleName, string ReferenceChainMessage)
		{
			// API define
			if (!Modules.TryGetValue(ModuleName, out BuildModule ResultModule))
			{
				// @todo projectfiles: Cross-platform modules can appear here during project generation, but they may have already
				//   been filtered out by the project generator.  This causes the projects to not be added to directories properly.
				ModuleRules RulesObject = CreateModuleRulesAndSetDefaults(ModuleName, ReferenceChainMessage);
				// DirectoryReference ModuleDirectory = RulesObject.ContainingModuleFile.Directory;

				// Clear the bUsePrecompiled flag if we're compiling a foreign plugin; since it's treated like an engine module, it will default to true in an installed build.
				if (RulesObject.Plugin != null && RulesObject.Plugin.File == ForeignPlugin)
				{
					RulesObject.bPrecompile     = true;
					RulesObject.bUsePrecompiled = false;
				}

				// Get the base directory for paths referenced by the module. If the module's under the UProject source directory use that, otherwise leave it relative to the Engine source directory.
				if (ProjectFile != null)
				{
					DirectoryReference ProjectSourceDirectoryName = DirectoryReference.Combine(ProjectFile.Directory, Tag.Directory.SourceCode);
					if (RulesObject.File.IsUnderDirectory(ProjectSourceDirectoryName))
					{
						RulesObject.PublicIncludePaths       = CombinePathList(ProjectSourceDirectoryName, RulesObject.PublicIncludePaths);
						RulesObject.PrivateIncludePaths      = CombinePathList(ProjectSourceDirectoryName, RulesObject.PrivateIncludePaths);
						RulesObject.PublicSystemLibraryPaths = CombinePathList(ProjectSourceDirectoryName, RulesObject.PublicSystemLibraryPaths);
					}
				}

				DirectoryReference GeneratedCodeDirectory = null;
				if (RulesObject.Type != ModuleRules.ModuleType.External)
				{
					// Get the base directory
					if (bUseSharedBuildEnvironment)
					{
						// GeneratedCodeDirectory = {D:\UERelease\Engine}
						GeneratedCodeDirectory = RulesObject.Context.DefaultOutputBaseDir;
					}
					else
					{
						GeneratedCodeDirectory = ProjectDirectory;
					}

					// Get the subfolder containing generated code
					GeneratedCodeDirectory = DirectoryReference.Combine(GeneratedCodeDirectory, PlatformIntermediateFolder, AppName, Tag.Directory.Inc);

					// Append the binaries subfolder, if present. We rely on this to ensure that build products can be filtered correctly.
					if (RulesObject.BinariesSubFolder != null)
					{
						GeneratedCodeDirectory = DirectoryReference.Combine(GeneratedCodeDirectory, RulesObject.BinariesSubFolder);
					}

					// Finally, append the module name.
					GeneratedCodeDirectory = DirectoryReference.Combine(GeneratedCodeDirectory, ModuleName);
				}

				// For legacy modules, add a bunch of default include paths.
				if (RulesObject.Type == ModuleRules.ModuleType.CPlusPlus &&
					RulesObject.bAddDefaultIncludePaths &&
					(RulesObject.Plugin != null || (ProjectFile != null && RulesObject.File.IsUnderDirectory(ProjectFile.Directory))))
				{
					// Add the module source directory
					DirectoryReference BaseSourceDirectory;
					if (RulesObject.Plugin != null)
					{
						BaseSourceDirectory = DirectoryReference.Combine(RulesObject.Plugin.RootDirectory, Tag.Directory.SourceCode);
					}
					else
					{
						BaseSourceDirectory = DirectoryReference.Combine(ProjectFile.Directory, Tag.Directory.SourceCode);
					}

					// If it's a game module (plugin or otherwise), add the root source directory to the include paths.
					if (RulesObject.File.IsUnderDirectory(TargetRulesFile.Directory) || 
						(RulesObject.Plugin != null && RulesObject.Plugin.LoadedFrom == PluginLoadedFrom.Project))
					{
						if (DirectoryReference.Exists(BaseSourceDirectory))
						{
							RulesObject.PublicIncludePaths.Add(NormalizeIncludePath(BaseSourceDirectory));
						}
					}

					// Resolve private include paths against the project source root
					for (int Idx = 0; Idx < RulesObject.PrivateIncludePaths.Count; ++Idx)
					{
						string PrivateIncludePath = RulesObject.PrivateIncludePaths[Idx];
						if (!Path.IsPathRooted(PrivateIncludePath))
						{
							PrivateIncludePath = DirectoryReference.Combine(BaseSourceDirectory, PrivateIncludePath).FullName;
						}
						RulesObject.PrivateIncludePaths[Idx] = PrivateIncludePath;
					}
				}

				// Allow the current platform to modify the module rules
				BuildPlatform.GetBuildPlatform(TargetPlatform).ModifyModuleRulesForActivePlatform(ModuleName, RulesObject, Rules);

				// Allow all build platforms to 'adjust' the module setting.
				// This will allow undisclosed platforms to make changes without
				// exposing information about the platform in publicly accessible
				// locations.
				BuildPlatform.PlatformModifyHostModuleRules(ModuleName, RulesObject, Rules);

				// API_DEFINE
				// Now, go ahead and create the module builder instance
				ResultModule = InstantiateModule(RulesObject, GeneratedCodeDirectory);
				Modules.Add(ResultModule.ModuleRuleFileName, ResultModule);
			}

			return ResultModule;
		}

		public BuildModuleCPP FindOrCreateCppModuleByName(string ModuleName, string ReferenceChainForException)
		{
			if (!(FindOrCreateModuleByName(ModuleName, ReferenceChainForException) is BuildModuleCPP CppModule))
			{
				throw new BuildException("'{0}' is not a C++ module (referenced via {1})", ModuleName, ReferenceChainForException);
			}
			return CppModule;
		}

		private BuildModule InstantiateModule
		(
			ModuleRules RulesObject,
			DirectoryReference GeneratedCodeDirectory
		)
		{
			switch (RulesObject.Type)
			{
				case ModuleRules.ModuleType.CPlusPlus:
					return new BuildModuleCPP
						(
							Rules: RulesObject,
							IntermediateDirectory: GetModuleIntermediateDirectory(RulesObject),
							GeneratedCodeDirectory: GeneratedCodeDirectory
						);

				case ModuleRules.ModuleType.External:
					return new BuildModuleExternal(RulesObject, GetModuleIntermediateDirectory(RulesObject));

				default:
					throw new BuildException("Unrecognized module type specified by 'Rules' object {0}", RulesObject.ToString());
			}
		}

		// Finds a module given its name.  Throws an exception if the module couldn't be found.
		public BuildModule GetModuleByName(string Name)
		{
			if (Modules.TryGetValue(Name, out BuildModule ResultModule))
			{
				return ResultModule;
			}
			else
			{
				throw new BuildException("Couldn't find referenced module '{0}'.", Name);
			}
		}

		// TODO : Move to BuildUtilities
		// Combines a list of paths with a base path.
		private static List<string> CombinePathList(DirectoryReference BasePath, List<string> PathList)
		{
			if (BasePath is null) { throw new ArgumentNullException(nameof(BasePath)); }
			if (PathList is null) { throw new ArgumentNullException(nameof(PathList)); }

			List<string> OutPathList = new List<string>();
			foreach (string Path in PathList)
			{
				OutPathList.Add(System.IO.Path.Combine(BasePath.FullName, Path));
			}
			return OutPathList;
		}
	}
}
