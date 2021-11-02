using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.CodeDom.Compiler;
using System.Reflection;
using System.Runtime.Serialization;
using Microsoft.CSharp;
using BuildToolUtilities;

// RulesAssembly ┬── Assembly                          CompiledAssembly
//               ├── List<PluginInfo>                  Plugins
//               ├── Dic<FileRef, ModuleRulesContext>  ModuleFileToContext
//               ├── Dictionary<string, FileReference> ModuleNameToModuleFile(*.Build.cs)
//               └── Dictionary<string, FileReference> TargetNameToTargetFile(*.Target.cs)

namespace BuildTool
{
	// Stores information about a compiled rules assembly and the types it contains
	public sealed class RulesAssembly
	{
		// Outers scope for items created by this assembly.
		// Used for chaining assemblies together.
		internal readonly RulesScope                Scope;
		private readonly  Assembly                  CompiledAssembly; // Rules.dll(*.Build.cs (1113개) 모두 컴파일 된거) or ProgramRules.dll(*.target.cs)
		private readonly  List<DirectoryReference>  BaseDirectories; // The base directories for this assembly
		private readonly  IReadOnlyList<PluginInfo> Plugins;  // All the plugins included in this assembly

		// Maps module names to their actual xxx.Module.cs file on disk
		private readonly Dictionary<string, FileReference> ModuleNameToModuleFile 
			= new Dictionary<string, FileReference>(StringComparer.InvariantCultureIgnoreCase);

		// Maps target names to their actual xxx.Target.cs file on disk
		private readonly Dictionary<string, FileReference> TargetNameToTargetFile 
			= new Dictionary<string, FileReference>(StringComparer.InvariantCultureIgnoreCase);

		// Mapping from module file to its context.
		private readonly Dictionary<FileReference, ModuleRulesContext> ModuleFileToContext;

		// Whether this assembly contains engine modules.
		// Used to set default values for bTreatAsEngineModule.
		private readonly bool bContainsEngineModules;

		// Whether to use backwards compatible default settings for module and target rules. 
		// This is enabled by default for game projects to support a simpler migration path, 
		// but is disabled for engine modules.
		private readonly BuildSettingsVersion? DefaultBuildSettings;

		// Whether the modules and targets in this assembly are read-only
		private readonly bool bReadOnly; // IsEngineInstalled() || bUsePrecompiled

		// The parent rules assembly that this assembly inherits.
		// Game assemblies inherit the engine assembly, and the engine assembly inherits nothing.
		private readonly RulesAssembly ParentRulesAssembly;

		public IEnumerable<PluginInfo> EnumeratePlugins() => global::BuildTool.Plugins.FilterPlugins(EnumeratePluginsInternal());

		internal RulesAssembly
		(
			RulesScope                                    ItemsScopeCreatedByAssembly,
			List<DirectoryReference>                      BaseDirectoryForAssembly,
			IReadOnlyList<PluginInfo>                     PluginsInAssembly,
			Dictionary<FileReference, ModuleRulesContext> ModuleFileToContextToCompile,
			List<FileReference>                           TargetFilesToCompile,
			FileReference                                 OutputPathAssemblyFileName,
			bool                                          bContainsEngineModules, // Whether this assembly contains engine modules. Used to initialize the default value for ModuleRules.bTreatAsEngineModule.
			BuildSettingsVersion?                         DefaultBuildSettings, // Optional override for the default build settings version for modules created from this assembly.
			bool bReadOnly, // Whether the modules and targets in this assembly are installed, and should be created with the bUsePrecompiled flag set
			bool bSkipCompile,
			RulesAssembly ParentRulesAssembly // If Parent null, EngineAssembly(EngineProgramRules.dll or EngineRules.dll)  
		)
		{
			this.Scope = ItemsScopeCreatedByAssembly;
			this.BaseDirectories = BaseDirectoryForAssembly;
			this.Plugins = PluginsInAssembly;
			this.ModuleFileToContext = ModuleFileToContextToCompile;
			this.bContainsEngineModules = bContainsEngineModules;
			this.DefaultBuildSettings = DefaultBuildSettings;
			this.bReadOnly = bReadOnly;
			this.ParentRulesAssembly = ParentRulesAssembly;

			// Find all the source files
			HashSet<FileReference> AssemblySourceFiles = new HashSet<FileReference>();
			AssemblySourceFiles.UnionWith(ModuleFileToContextToCompile.Keys);
			AssemblySourceFiles.UnionWith(TargetFilesToCompile);

			// Compile the assembly (*.Build.cs)
			if (0 < AssemblySourceFiles.Count)
			{
				List<string> PreprocessorDefines = GetPreprocessorDefinitions();
				CompiledAssembly = DynamicCompilation.CompileAndLoadAssembly
					(
						OutputPathAssemblyFileName, 
						AssemblySourceFiles, 
						PreprocessorDefines: 
						PreprocessorDefines, 
						DoNotCompile: bSkipCompile
					);
				// CodeBase = "file://D:/UERelease/Engine/Intermediate/Build/BuildRules/MyEngineRules.dll" => 모든 디렉토리에 있는 *.Build.cs 한꺼번에 컴파일
				// or
				// CodeBase = "file://D:/UERelease/Engine/Intermediate/Build/BuildRules/MyEngineProgramRules.dll" => 모든 디렉토리에 있는 *.target.cs, *.Build.cs 한꺼번에 컴파일
			}

			// Setup the module map
			foreach (FileReference ModuleFile in ModuleFileToContextToCompile.Keys)
			{
				string ModuleName = ModuleFile.GetFileNameWithoutAnyExtensions();
				if (!ModuleNameToModuleFile.ContainsKey(ModuleName))
				{
					ModuleNameToModuleFile.Add(ModuleName, ModuleFile);
				}
			}

			// Setup the target map
			foreach (FileReference TargetFile in TargetFilesToCompile)
			{
				string TargetName = TargetFile.GetFileNameWithoutAnyExtensions();
				if (!TargetNameToTargetFile.ContainsKey(TargetName))
				{
					TargetNameToTargetFile.Add(TargetName, TargetFile);
				}
			}

			// Write any deprecation warnings for methods overriden from a base with the [ObsoleteOverride] attribute.
			// Unlike the [Obsolete] attribute, this ensures the message is given because the method is implemented, not because it's called.
			if (CompiledAssembly != null)
			{
				foreach (Type CompiledType in CompiledAssembly.GetTypes())
				{
					foreach (MethodInfo Method in CompiledType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
					{
						ObsoleteOverrideAttribute Attribute = Method.GetCustomAttribute<ObsoleteOverrideAttribute>(true);
						if (Attribute != null)
						{
							if (!TryGetFileNameFromType(CompiledType, out FileReference Location))
							{
								Location = new FileReference(CompiledAssembly.Location);
							}
							Log.TraceWarning("{0}: warning: {1}", Location, Attribute.Message);
						}
					}
					if(CompiledType.BaseType == typeof(ModuleRules))
					{
						ConstructorInfo Constructor = CompiledType.GetConstructor(new Type[] { typeof(TargetInfo) });
						if(Constructor != null)
						{
							if (!TryGetFileNameFromType(CompiledType, out FileReference Location))
							{
								Location = new FileReference(CompiledAssembly.Location);
							}
							Log.TraceWarning("{0}: warning: Module constructors should take a ReadOnlyTargetRules argument (rather than a TargetInfo argument) and pass it to the base class constructor from 4.15 onwards. Please update the method signature.", Location);
						}
					}
				}
			}
		}

		public bool IsReadOnly(FileSystemReference Location)
		{
			if (BaseDirectories.Any(x => Location.IsUnderDirectory(x)))
			{
				return bReadOnly;
			}
			else if (ParentRulesAssembly != null)
			{
				return ParentRulesAssembly.IsReadOnly(Location);
			}
			else
			{
				return false;
			}
		}

		// Finds all the preprocessor definitions that need to be set for the current engine.
		public static List<string> GetPreprocessorDefinitions()
		{
			List<string> OutPreprocessorDefines = new List<string>
			{
				Tag.CppContents.Def.WithForwardedModuleRulesCTOR,
				Tag.CppContents.Def.WithForwardedTargetRulesCTOR
			};

			if (BuildVersion.TryRead(BuildVersion.GetDefaultFileName(), out BuildVersion Version))
			{
				for (int MinorVersion = 17; MinorVersion <= Version.MinorVersion; ++MinorVersion)
				{
					OutPreprocessorDefines.Add(String.Format(Tag.CppContents.Def.BuildMinorVersion, MinorVersion));
				}
			}

			return OutPreprocessorDefines;
		}

		// Fills a list with all the module names in this assembly (or its parent)
		public void GetAllModuleNames(List<string> ModuleNames)
		{
			if (ParentRulesAssembly != null)
			{
				ParentRulesAssembly.GetAllModuleNames(ModuleNames);
			}
			if (CompiledAssembly != null)
			{
				ModuleNames.AddRange(CompiledAssembly.GetTypes().Where
					(x => x.IsClass && x.IsSubclassOf(typeof(ModuleRules)) 
					&& ModuleNameToModuleFile.ContainsKey(x.Name)).Select(x => x.Name));
			}
		}

		// Fills a list with all the target names in this assembly
		public void GetAllTargetNames(List<string> TargetNames, bool bIncludeParentAssembly)
		{
			if(ParentRulesAssembly != null && bIncludeParentAssembly)
			{
				ParentRulesAssembly.GetAllTargetNames(TargetNames, true);
			}
			TargetNames.AddRange(TargetNameToTargetFile.Keys);
		}

		// Tries to get the filename that declared the given type
		public bool TryGetFileNameFromType(Type ExistingType, out FileReference File)
		{
			if (ExistingType.Assembly == CompiledAssembly)
			{
				string Name = ExistingType.Name;
				if (ModuleNameToModuleFile.TryGetValue(Name, out File))
				{
					return true;
				}

				string NameWithoutTarget = Name;
				
				if (NameWithoutTarget.EndsWith(Tag.Module.TargetSuffix))
				{
					NameWithoutTarget = NameWithoutTarget.Substring(0, NameWithoutTarget.Length - 6);
				}

				if (TargetNameToTargetFile.TryGetValue(NameWithoutTarget, out File))
				{
					return true;
				}
			}
			else
			{
				if (ParentRulesAssembly != null 
					&& ParentRulesAssembly.TryGetFileNameFromType(ExistingType, out File))
				{
					return true;
				}
			}

			File = null;
			return false;
		}

		// Gets the source file containing rules for the given module
		public FileReference GetModuleFileName(string ModuleName)
		{
			if (ModuleNameToModuleFile.TryGetValue(ModuleName, out FileReference ModuleFile))
			{
				return ModuleFile;
			}
			else
			{
				return ParentRulesAssembly?.GetModuleFileName(ModuleName);
			}
		}

		// Gets the type defining rules for the given module
		public Type GetModuleRulesType(string ModuleName)
		{
			if (ModuleNameToModuleFile.ContainsKey(ModuleName))
			{
				return GetModuleRulesTypeInternal(ModuleName);
			}
			else
			{
				return ParentRulesAssembly?.GetModuleRulesType(ModuleName);
			}
		}

		// Gets the type defining rules for the given module within this assembly
		private Type GetModuleRulesTypeInternal(string ModuleName)
		{
			// The build module must define a type named 'Rules' that derives from our 'ModuleRules' type.  
			Type RulesObjectType = CompiledAssembly.GetType(ModuleName);
			if (RulesObjectType == null)
			{
				// Temporary hack to avoid System namespace collisions
				// @todo projectfiles: Make rules assemblies require namespaces.
				RulesObjectType = CompiledAssembly.GetType(nameof(BuildTool) + "." + ModuleName);
			}
			return RulesObjectType;
		}

		// Gets the source file containing rules for the given target
		public FileReference GetTargetFileName(string TargetName)
		{
			if (TargetNameToTargetFile.TryGetValue(TargetName, out FileReference TargetFile))
			{
				return TargetFile;
			}
			else
			{
				return ParentRulesAssembly?.GetTargetFileName(TargetName);
			}
		}

		// Creates an instance of a module rules descriptor object for the specified module name
		public ModuleRules RecursivelyCreateModuleRules(string ModuleName, ReadOnlyTargetRules TargetInfoWithThisModule, string ReferenceChainMessage)
		{
			// Currently, we expect the user's rules object type name to be the same as the module name
			string ModuleTypeName = ModuleName;

			// Make sure the base module file is known to us
			if (!ModuleNameToModuleFile.TryGetValue(ModuleTypeName, out FileReference ModuleFileName))
			{
				if (ParentRulesAssembly == null)
				{
					throw new BuildException("Could not find definition for module '{0}', (referenced via {1})", ModuleTypeName, ReferenceChainMessage);
				}
				else
				{
					return ParentRulesAssembly.RecursivelyCreateModuleRules(ModuleName, TargetInfoWithThisModule, ReferenceChainMessage);
				}
			}

			// get the standard Rules object class from the assembly
			Type BaseRulesObjectType = GetModuleRulesTypeInternal(ModuleTypeName);

			// look around for platform/group modules that we will use instead of the basic module
			Type PlatformRulesObjectType = GetModuleRulesTypeInternal(ModuleTypeName + "_" + TargetInfoWithThisModule.Platform.ToString());
			if (PlatformRulesObjectType == null)
			{
				foreach (BuildPlatformGroup Group in BuildPlatform.GetPlatformGroups(TargetInfoWithThisModule.Platform))
				{
					// look to see if the group has an override
					Type GroupRulesObjectType = GetModuleRulesTypeInternal(ModuleName + "_" + Group.ToString());

					// we expect only one platform group to be found in the extensions
					if (GroupRulesObjectType != null && PlatformRulesObjectType != null)
					{
						throw new BuildException("Found multiple platform group overrides ({0} and {1}) for module {2} without a platform specific override. Create a platform override with the class hierarchy as needed.", 
							GroupRulesObjectType.Name, PlatformRulesObjectType.Name, ModuleName);
					}
					// remember the platform group if we found it, but keep searching to verify there isn't more than one
					if (GroupRulesObjectType != null)
					{
						PlatformRulesObjectType = GroupRulesObjectType;
					}
				}
			}

			// Figure out the best rules object to use
			Type RulesObjectType = PlatformRulesObjectType ?? BaseRulesObjectType;
			if (RulesObjectType == null)
			{
				throw new BuildException("Expecting to find a type to be declared in a module rules named '{0}' in {1}.  This type must derive from the 'ModuleRules' type defined by Build Tool.", ModuleTypeName, CompiledAssembly.FullName);
			}

			// Create an instance of the module's rules object
			try
			{
				// Create an uninitialized ModuleRules object and set some defaults.
				ModuleRules OutCompiledModuleRule = (ModuleRules)FormatterServices.GetUninitializedObject(RulesObjectType);
				// even if we created a platform-extension version of the module rules, we are pretending to be
				// the base type, so that no one else needs to manage this
				OutCompiledModuleRule.Name                 = ModuleName;
				OutCompiledModuleRule.File                 = ModuleFileName;
				OutCompiledModuleRule.Directory            = ModuleFileName.Directory;
				OutCompiledModuleRule.Context              = ModuleFileToContext[OutCompiledModuleRule.File];
				OutCompiledModuleRule.Plugin               = OutCompiledModuleRule.Context.ModulePluginInfo;
				OutCompiledModuleRule.bTreatAsEngineModule = bContainsEngineModules;

				if(DefaultBuildSettings.HasValue)
				{
					OutCompiledModuleRule.DefaultBuildSettings = DefaultBuildSettings.Value;
				}

				OutCompiledModuleRule.bPrecompile     = (OutCompiledModuleRule.bTreatAsEngineModule 
					                                    || ModuleName.Equals("DefaultGame", StringComparison.OrdinalIgnoreCase)) 
						                                && TargetInfoWithThisModule.bPrecompile;
				OutCompiledModuleRule.bUsePrecompiled = bReadOnly;

				// go up the type hierarchy (if there is a hierarchy), looking for any extra directories for the module
				if (RulesObjectType != BaseRulesObjectType 
				 && RulesObjectType != typeof(ModuleRules))
				{
					Type SubType = RulesObjectType;

					OutCompiledModuleRule.DirectoriesForModuleSubClasses = new Dictionary<Type, DirectoryReference>();

					OutCompiledModuleRule.SubclassRules = new List<string>();
					while (SubType != null 
						&& SubType != BaseRulesObjectType)
					{
						if (TryGetFileNameFromType(SubType, out FileReference SubTypeFileName))
						{
							OutCompiledModuleRule.DirectoriesForModuleSubClasses.Add(SubType, SubTypeFileName.Directory);
							OutCompiledModuleRule.SubclassRules.Add(SubTypeFileName.FullName);
						}
						if (SubType.BaseType == null)
						{
							throw new BuildException("{0} is not derived from {1}", RulesObjectType.Name, BaseRulesObjectType.Name);
						}
						SubType = SubType.BaseType;
					}
				}

				// Call the constructor
				ConstructorInfo Constructor = RulesObjectType.GetConstructor(new Type[] { typeof(ReadOnlyTargetRules) });
				if(Constructor == null)
				{
					throw new BuildException("No valid constructor found for {0}.", ModuleName);
				}
				Constructor.Invoke(OutCompiledModuleRule, new object[] { TargetInfoWithThisModule });

				return OutCompiledModuleRule;
			}
			catch (Exception Ex)
			{
				Exception MessageEx = (Ex is TargetInvocationException && Ex.InnerException != null)? Ex.InnerException : Ex;
				throw new BuildException(Ex, "Unable to instantiate module '{0}': {1}\n(referenced via {2})", ModuleName, MessageEx.ToString(), ReferenceChainMessage);
			}
		}

		// Construct an instance of the given target rules
		private TargetRules CreateTargetRulesInstance(string TargetRulesTypeName, TargetInfo TargetInfoToPassToConstructor)
		{
			// The build module must define a type named '<TargetName>Target' that derives from our 'TargetRules' type.  
			Type RulesType = CompiledAssembly.GetType(TargetRulesTypeName);
			if (RulesType == null)
			{
				throw new BuildException("Expecting to find a type to be declared in a target rules named '{0}'.  This type must derive from the 'TargetRules' type defined by  Build Tool.", TargetRulesTypeName);
			}

			// Create an instance of the module's rules object, and set some defaults before calling the constructor.
			TargetRules Rules = (TargetRules)FormatterServices.GetUninitializedObject(RulesType);

			if (DefaultBuildSettings.HasValue)
			{
				// 그냥 Lastest확정
				Rules.DefaultBuildSettings = DefaultBuildSettings.Value;
			}

			// Find the constructor
			ConstructorInfo TargetRulesConstructorTargetInfoParameter = RulesType.GetConstructor(new Type[] { typeof(TargetInfo) });
			if(TargetRulesConstructorTargetInfoParameter == null)
			{
				throw new BuildException("No constructor found on {0} which takes an argument of type TargetInfo.", RulesType.Name);
			}

			// Invoke the regular constructor
			try
			{
				TargetRulesConstructorTargetInfoParameter.Invoke(Rules, new object[] { TargetInfoToPassToConstructor });
			}
			catch (Exception Ex)
			{
				throw new BuildException(Ex, "Unable to instantiate instance of '{0}' object type from compiled assembly '{1}'.   Build Tool creates an instance of your module's 'Rules' object in order to find out about your module's requirements.  The CLR exception details may provide more information:  {2}", TargetRulesTypeName, Path.GetFileNameWithoutExtension(CompiledAssembly.Location), Ex.ToString());
			}

			Rules.File = TargetNameToTargetFile[TargetInfoToPassToConstructor.Name];

			// Set the default overriddes for the configured target type
			Rules.SetGlobalDefinitionsForTargetType();

			// Set the final value for the link type in the target rules
			if(Rules.LinkType == TargetLinkType.Default)
			{
				throw new BuildException("TargetRules.LinkType should be inferred from TargetType");
			}

			// Set the default value for whether to use the shared build environment
			if(Rules.BuildEnvironment == TargetBuildEnvironment.Unique && BuildTool.IsEngineInstalled())
			{
				throw new BuildException("Targets with a unique build environment cannot be built an installed engine.");
			}

			// Automatically include CoreUObject
			if (Rules.bCompileAgainstEngine)
			{
				Rules.bCompileAgainstCoreUObject = true;
			}

			// Must have editor only data if building the editor.
			if (Rules.bBuildEditor)
			{
				Rules.bBuildWithEditorOnlyData = true;
			}

			// Apply the override to force debug info to be enabled
			if (Rules.bForceDebugInfo)
			{
				Rules.bDisableDebugInfo = false;
				Rules.bOmitPCDebugInfoInDevelopment = false;
			}

			// Setup the malloc profiler
			if (Rules.bUseMallocProfiler)
			{
				Rules.bOmitFramePointers = false;
				Rules.GlobalDefinitions.Add(Tag.CppContents.Def.UseMallocProfiler + Tag.Boolean.One);
			}

			// Set a macro if we allow using generated inis
			if (!Rules.bAllowGeneratedIniWhenCooked)
			{
				Rules.GlobalDefinitions.Add(Tag.CppContents.Def.DisableGeneratedIniWhenCooked + Tag.Boolean.One);
			}

			if (!Rules.bAllowNonUFSIniWhenCooked)
			{
				Rules.GlobalDefinitions.Add(Tag.CppContents.Def.DisableNonFileSystemIniWhenCooked + Tag.Boolean.One);
			}

			if (Rules.bDisableUnverifiedCertificates)
			{
				Rules.GlobalDefinitions.Add(Tag.CppContents.Def.DisableUnVerfiedCertificateLoading + Tag.Boolean.One);
			}

			// Allow the platform to finalize the settings
			BuildPlatform Platform = BuildPlatform.GetBuildPlatform(Rules.Platform);
			Platform.ValidateTarget(Rules);

			// Some platforms may *require* monolithic compilation...
			if (Rules.LinkType != TargetLinkType.Monolithic && BuildPlatform.PlatformRequiresMonolithicBuilds(Rules.Platform))
			{
				throw new BuildException(String.Format("{0}: {1} does not support modular builds", Rules.Name, Rules.Platform));
			}

			return Rules;
		}

		// Creates a target rules object for the specified target name.
		public TargetRules CreateTargetRules
		(
			string                    TargetName, 
			BuildTargetPlatform      PlatformBeingCompiled, 
			TargetConfiguration ConfigurationBeingCompiled, 
			string                    ArchitectureBeingBuilt, 
			FileReference             ProjectFile,
			CommandLineArguments      Arguments
		)
		{
			bool bFoundTargetName = TargetNameToTargetFile.ContainsKey(TargetName);
			if (bFoundTargetName == false)
			{
				if (ParentRulesAssembly == null) // if this is Engine Assembly, Parent == null is true.
				{
					string ExceptionMessage = "Couldn't find target rules file for target '";
					ExceptionMessage += TargetName;
					ExceptionMessage += "' in rules assembly '";
					ExceptionMessage += CompiledAssembly.FullName;
					ExceptionMessage += "'." + Environment.NewLine;
					ExceptionMessage += "Location: " + CompiledAssembly.Location + Environment.NewLine;
					ExceptionMessage += "Target rules found:" + Environment.NewLine;

					foreach (KeyValuePair<string, FileReference> entry in TargetNameToTargetFile)
					{
						ExceptionMessage += "\t" + entry.Key + " - " + entry.Value + Environment.NewLine;
					}

					throw new BuildException(ExceptionMessage);
				}
				else
				{
					return ParentRulesAssembly.CreateTargetRules(TargetName, PlatformBeingCompiled, ConfigurationBeingCompiled, ArchitectureBeingBuilt, ProjectFile, Arguments);
				}
			}

			string TargetTypeName = TargetName + Tag.Module.TargetSuffix;

			// The build module must define a type named '<TargetName>Target' that derives from our 'TargetRules' type.  
			return CreateTargetRulesInstance
			(
				TargetTypeName, 
				new TargetInfo
				(
					TargetName, 
					PlatformBeingCompiled, 
					ConfigurationBeingCompiled, 
					ArchitectureBeingBuilt, 
					ProjectFile, 
					Arguments
				)
			);
		}

		// Determines a target name based on the type of target we're trying to build
		// <param name="ProjectFile">Project file for the target being built</param>
		public string GetTargetNameByTypeRecursively
		(
            TargetType TypeToSearch,
            BuildTargetPlatform PlatformBeingBuilt,
            TargetConfiguration ConfigurationBeingBuilt,
            string ArchitectureBeingBuilt,
            FileReference ProjectFile
		)
		{
			// Create all the targets in this assembly 
			List<string> Matches = new List<string>();
			foreach(KeyValuePair<string, FileReference> TargetPair in TargetNameToTargetFile)
			{
				TargetRules Rules = CreateTargetRulesInstance
				(
                    TargetPair.Key + Tag.Module.TargetSuffix,
                    new TargetInfo(TargetPair.Key, PlatformBeingBuilt, ConfigurationBeingBuilt, ArchitectureBeingBuilt, ProjectFile, null)
				);

				if(Rules.Type == TypeToSearch)
				{
					Matches.Add(TargetPair.Key);
				}
			}

			// If we got a result, return it. If there were multiple results, fail.
			if(Matches.Count == 0)
			{
				if(ParentRulesAssembly == null)
				{
					throw new BuildException("Unable to find target of type '{0}' for project '{1}'", TypeToSearch, ProjectFile);
				}
				else
				{
					return ParentRulesAssembly.GetTargetNameByTypeRecursively(TypeToSearch, PlatformBeingBuilt, ConfigurationBeingBuilt, ArchitectureBeingBuilt, ProjectFile);
				}
			}
			else
			{
				if(Matches.Count == 1)
				{
					return Matches[0];
				}
				else
				{
					throw new BuildException("Found multiple targets with TargetType={0}: {1}", TypeToSearch, String.Join(", ", Matches));
				}
			}
		}


        private IEnumerable<PluginInfo> EnumeratePluginsInternal()
		{
			if (ParentRulesAssembly == null)
			{
				return Plugins;
			}
			else
			{
				return Plugins.Concat(ParentRulesAssembly.EnumeratePluginsInternal());
			}
		}

		// Tries to find the PluginInfo associated with a given module file
		private bool TryGetPluginForModuleRecursively(FileReference ModuleFileToSearch, out PluginInfo OutPlugin)
		{
            if (ModuleFileToContext.TryGetValue(ModuleFileToSearch, out ModuleRulesContext Context))
            {
                OutPlugin = Context.ModulePluginInfo;
                return OutPlugin != null;
            }
            if (ParentRulesAssembly == null)
			{
				OutPlugin = null;
				return false;
			}

			return ParentRulesAssembly.TryGetPluginForModuleRecursively(ModuleFileToSearch, out OutPlugin);
		}
	}

	// Methods for dynamically compiling C# source files
	public class DynamicCompilation
	{
		private static string FormatVersionNumber(ReadOnlyBuildVersion Version)
		{
			return string.Format("{0}.{1}.{2}", Version.MajorVersion, Version.MinorVersion, Version.PatchVersion);
		}

		// Dynamically compiles an assembly for the specified source file and
		// loads that assembly into the application's current domain.
		// If an assembly has already been compiled and is not out of date,
		// then it will be loaded and no compilation is necessary.
		public static Assembly CompileAndLoadAssembly
		(
			FileReference          OutputAssemblyPath,
			HashSet<FileReference> SourceFileNames,
			List<string>           ReferencedAssembies = null,
			List<string>           PreprocessorDefines = null,
			bool                   DoNotCompile = false,
			bool                   TreatWarningsAsErrors = false
		)
		{
			// Check to see if the resulting assembly is compiled and up to date
			// OutputAssemblyPath          = {D:\UERelease\Engine\Intermediate\Build\BuildRules\EngineRules.dll}
			// OutputAssemblyPath.FullName = "D:\\UERelease\\Engine\\Intermediate\\Build\\BuildRules\\EngineRules.dll" + Manifest.json\

			// OutputAssemblyPath          = {D:\UERelease\Engine\Intermediate\Build\BuildRules\EngineProgramRules.dll}
			// OutputAssemblyPath.FullName = "D:\\UERelease\\Engine\\Intermediate\\Build\\BuildRules\\EngineProgramRules.dll" + Manifest.json
			FileReference AssemblyManifestFilePath = FileReference.Combine(OutputAssemblyPath.Directory, Path.GetFileNameWithoutExtension(OutputAssemblyPath.FullName) + "Manifest.json");

			bool bNeedsCompilation = false;
			if (!DoNotCompile)
			{
				bNeedsCompilation = RequiresCompilation(SourceFileNames, AssemblyManifestFilePath, OutputAssemblyPath);
			}

			// Load the assembly to ensure it is correct
			Assembly CompiledAssembly = null;
			if (!bNeedsCompilation)
			{
				try
				{
					// Load the previously-compiled assembly from disk
					CompiledAssembly = Assembly.LoadFile(OutputAssemblyPath.FullName);
				}
				catch (FileLoadException Ex)
				{
					Log.TraceInformation(String.Format("Unable to load the previously-compiled assembly file '{0}'. Build Tool will try to recompile this assembly now.  (Exception: {1})", OutputAssemblyPath, Ex.Message));
					bNeedsCompilation = true;
				}
				catch (BadImageFormatException Ex)
				{
					Log.TraceInformation(String.Format("Compiled assembly file '{0}' appears to be for a newer CLR version or is otherwise invalid. Build Tool will try to recompile this assembly now.  (Exception: {1})", OutputAssemblyPath, Ex.Message));
					bNeedsCompilation = true;
				}
				catch (FileNotFoundException)
				{
					throw new BuildException("Precompiled rules assembly '{0}' does not exist.", OutputAssemblyPath);
				}
				catch (Exception Ex)
				{
					throw new BuildException(Ex, "Error while loading previously-compiled assembly file '{0}'.  (Exception: {1})", OutputAssemblyPath, Ex.Message);
				}
			}

			// Compile the assembly if me
			if (bNeedsCompilation)
			{
				CompiledAssembly = CompileAssembly(OutputAssemblyPath, SourceFileNames, ReferencedAssembies, PreprocessorDefines, TreatWarningsAsErrors);
			
				// AssemblyManifestFilePath = {D:\UERelease\Engine\Intermediate\Build\BuildRules\EngineRulesManifest.json}에 목록 있음.
				using (JsonWriter Writer = new JsonWriter(AssemblyManifestFilePath))
				{
					ReadOnlyBuildVersion Version = ReadOnlyBuildVersion.Current;

					Writer.WriteObjectStart();
					// Save out a list of all the source files we compiled.  This is so that we can tell if whole files were added or removed
					// since the previous time we compiled the assembly.  In that case, we'll always want to recompile it!
					Writer.WriteStringArrayField(Tag.JSONField.SourceFiles, SourceFileNames.Select(x => x.FullName));
					Writer.WriteValue(Tag.JSONField.EngineVersion, FormatVersionNumber(Version));
					Writer.WriteObjectEnd();
				}
			}

#if !NET_CORE
			// Load the assembly into our app domain
			// 지금 돌아가는 어셈블리(BuildTool.exe)에 로드
			try
			{
				// CodeBase = "file://D:/UERelease/Engine/Intermediate/Build/BuildRules/MyEngineRules.dll"
				// CodeBase = "file://D:/UERelease/Engine/Intermediate/Build/BuildRules/MyEngineProgramRules.dll"
				AppDomain.CurrentDomain.Load(CompiledAssembly.GetName());
			}
			catch (Exception Ex)
			{
				throw new BuildException(Ex, "Unable to load the compiled build assembly '{0}' into our application's domain.  (Exception: {1})", OutputAssemblyPath, Ex.Message);
			}
#endif
			// CodeBase = "file://D:/UERelease/Engine/Intermediate/Build/BuildRules/MyEngineRules.dll"
			// CodeBase = "file://D:/UERelease/Engine/Intermediate/Build/BuildRules/MyEngineProgramRules.dll"
			return CompiledAssembly;
		}

		// Checks to see if the assembly needs compilation

		// <param name="SourceFiles">Set of source files</param>
		// <param name="AssemblyManifestFilePath">File containing information about this assembly, like which source files it was built with and engine version</param>
		// <param name="OutputAssemblyPath">Output path for the assembly</param>
		// <returns>True if the assembly needs to be built</returns>
		private static bool RequiresCompilation
		(
			HashSet<FileReference> SourceFiles,
			FileReference AssemblyManifestFilePath,
			FileReference OutputAssemblyPath
		)
		{
			// if (BuildTool.IsFileInstalled(OutputAssemblyPath))
			// {
			//     Log.TraceLog("Skipping {0}: File is installed", OutputAssemblyPath);
			//     return false;
			// }

			// Check to see if we already have a compiled assembly file on disk
			// OutputAssemblyPath = {D:\UERelease\Engine\Intermediate\Build\BuildRules\EngineRules.dll}
			FileItem OutputAssemblyFile = FileItem.GetItemByFileReference(OutputAssemblyPath);
			if (!OutputAssemblyFile.Exists) 
			{
				Log.TraceLog("Compiling {0}: Assembly does not exist", OutputAssemblyPath);
				return true;
			}

			FileItem UBTFile = FileItem.GetItemByFileReference(BuildTool.GetBuildToolAssemblyPath());
			if (OutputAssemblyFile.LastWriteTimeUtc < UBTFile.LastWriteTimeUtc)
			{
				Log.TraceLog("Compiling {0}: {1} is newer", OutputAssemblyPath, UBTFile.Name);
				return true;
			}

			FileItem AssemblySourceListFile = FileItem.GetItemByFileReference(AssemblyManifestFilePath);
			if (!AssemblySourceListFile.Exists)
			{
				Log.TraceLog("Compiling {0}: Missing source file list ({1})", OutputAssemblyPath, AssemblyManifestFilePath);
				return true;
			}

			JsonObject Manifest = JsonObject.Read(AssemblyManifestFilePath);

			// check if the engine version is different
			string EngineVersionManifest = Manifest.GetStringField("EngineVersion");
			string EngineVersionCurrent = FormatVersionNumber(ReadOnlyBuildVersion.Current);
			if (EngineVersionManifest != EngineVersionCurrent)
			{
				Log.TraceLog("Compiling {0}: Engine Version changed from {1} to {2}", OutputAssemblyPath, EngineVersionManifest, EngineVersionCurrent);
				return true;
			}

			HashSet<FileItem> CurrentSourceFileItems = new HashSet<FileItem>();
			foreach (string Line in Manifest.GetStringArrayField("SourceFiles"))
			{
				CurrentSourceFileItems.Add(FileItem.GetItemByPath(Line));
			}

			// Get the new source files
			HashSet<FileItem> SourceFileItems = new HashSet<FileItem>();
			foreach (FileReference SourceFile in SourceFiles)
			{
				SourceFileItems.Add(FileItem.GetItemByFileReference(SourceFile));
			}

			// Check if there are any differences between the sets
			foreach (FileItem CurrentSourceFileItem in CurrentSourceFileItems)
			{
				if (!SourceFileItems.Contains(CurrentSourceFileItem))
				{
					Log.TraceLog("Compiling {0}: Removed source file ({1})", OutputAssemblyPath, CurrentSourceFileItem);
					return true;
				}
			}
			foreach (FileItem SourceFileItem in SourceFileItems)
			{
				if (!CurrentSourceFileItems.Contains(SourceFileItem))
				{
					Log.TraceLog("Compiling {0}: Added source file ({1})", OutputAssemblyPath, SourceFileItem);
					return true;
				}
			}

			// Check if any of the timestamps are newer
			foreach (FileItem SourceFileItem in SourceFileItems)
			{
				if (SourceFileItem.LastWriteTimeUtc > OutputAssemblyFile.LastWriteTimeUtc)
				{
					Log.TraceLog("Compiling {0}: {1} is newer", OutputAssemblyPath, SourceFileItem);
					return true;
				}
			}

			return false;
		} // end RequiresCompilation

#if NET_CORE
		private static void LogDiagnostics(IEnumerable<Diagnostic> Diagnostics)
		{
			foreach (Diagnostic Diag in Diagnostics)
			{
				switch (Diag.Severity)
				{
					case DiagnosticSeverity.Error: 
					{
						Log.TraceError(Diag.ToString()); 
						break;
					}
					case DiagnosticSeverity.Hidden: 
					{
						break;
					}
					case DiagnosticSeverity.Warning: 
					{
						Log.TraceWarning(Diag.ToString()); 
						break;
					}
					case DiagnosticSeverity.Info: 
					{
						Log.TraceInformation(Diag.ToString()); 
						break;
					}
				}
			}
		}

		private static Assembly CompileAssembly(FileReference OutputAssemblyPath, List<FileReference> SourceFileNames, List<string> ReferencedAssembies, List<string> PreprocessorDefines = null, bool TreatWarningsAsErrors = false)
		{
			CSharpParseOptions ParseOptions = new CSharpParseOptions(
				languageVersion:LanguageVersion.Latest, 
				kind:SourceCodeKind.Regular,
				preprocessorSymbols:PreprocessorDefines
			);

			List<SyntaxTree> SyntaxTrees = new List<SyntaxTree>();

			foreach (FileReference SourceFileName in SourceFileNames)
			{
				SourceText Source = SourceText.From(File.ReadAllText(SourceFileName.FullName));
				SyntaxTree Tree = CSharpSyntaxTree.ParseText(Source, ParseOptions, SourceFileName.FullName);

				IEnumerable<Diagnostic> Diagnostics = Tree.GetDiagnostics();
				if (Diagnostics.Count() > 0)
				{
					Log.TraceWarning($"Errors generated while parsing '{SourceFileName.FullName}'");
					LogDiagnostics(Tree.GetDiagnostics());
					return null;
				}

				SyntaxTrees.Add(Tree);
			}

			// Create the output directory if it doesn't exist already
			DirectoryInfo DirInfo = new DirectoryInfo(OutputAssemblyPath.Directory.FullName);
			if (!DirInfo.Exists)
			{
				try
				{
					DirInfo.Create();
				}
				catch (Exception Ex)
				{
					throw new BuildException(Ex, "Unable to create directory '{0}' for intermediate assemblies (Exception: {1})", OutputAssemblyPath, Ex.Message);
				}
			}

			List<MetadataReference> MetadataReferences = new List<MetadataReference>();
			if (ReferencedAssembies != null)
			{
				foreach (string Reference in ReferencedAssembies)
				{
					MetadataReferences.Add(MetadataReference.CreateFromFile(Reference));
				}
			}

			MetadataReferences.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
			MetadataReferences.Add(MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location));
			MetadataReferences.Add(MetadataReference.CreateFromFile(Assembly.Load("System.Collections").Location));
			MetadataReferences.Add(MetadataReference.CreateFromFile(Assembly.Load("System.IO").Location));
			MetadataReferences.Add(MetadataReference.CreateFromFile(Assembly.Load("System.IO.FileSystem").Location));
			MetadataReferences.Add(MetadataReference.CreateFromFile(Assembly.Load("System.Console").Location));
			MetadataReferences.Add(MetadataReference.CreateFromFile(Assembly.Load("System.Runtime.Extensions").Location));
			MetadataReferences.Add(MetadataReference.CreateFromFile(Assembly.Load("Microsoft.Win32.Registry").Location));
			MetadataReferences.Add(MetadataReference.CreateFromFile(typeof(BuildTool).Assembly.Location));
			MetadataReferences.Add(MetadataReference.CreateFromFile(typeof(FileReference).Assembly.Location));

			CSharpCompilationOptions CompilationOptions = new CSharpCompilationOptions(
				outputKind:OutputKind.DynamicallyLinkedLibrary,
				optimizationLevel:OptimizationLevel.Release,
				warningLevel:4,
				assemblyIdentityComparer:DesktopAssemblyIdentityComparer.Default,
				reportSuppressedDiagnostics:true
				);

			CSharpCompilation Compilation = CSharpCompilation.Create(
				assemblyName:OutputAssemblyPath.GetFileNameWithoutAnyExtensions(),
				syntaxTrees:SyntaxTrees,
				references:MetadataReferences,
				options:CompilationOptions
				);

			using (FileStream AssemblyStream = FileReference.Open(OutputAssemblyPath, FileMode.Create))
			{
				EmitOptions EmitOptions = new EmitOptions(
					includePrivateMembers:true
				);

				EmitResult Result = Compilation.Emit(
					peStream:AssemblyStream,
					options:EmitOptions);

				if (!Result.Success)
				{
					LogDiagnostics(Result.Diagnostics);
					return null;
				}
			}

			return Assembly.LoadFile(OutputAssemblyPath.FullName);
		}

#else

		private static Assembly CompileAssembly
		(
			FileReference          OutputAssemblyPath,
			HashSet<FileReference> SourceFileNames,
			List<string>           ReferencedAssembies,
			List<string>           PreprocessorDefines = null,
			bool                   TreatWarningsAsErrors = false
		)
		{
			// OutputAssemblyPath => Engine\Intermediate\Build\BuildRules\EngineRules.dll
			// SourceFileName     => *.Build.cs
			// ReferencedAssmbies => null
			// Preprocessors      => [0] = "WITH_FORWARDED_MODULE_RULES_CTOR", [1] = "WITH_FORWARDED_TARGET_RULES_CTOR", [2] = "UE_4_17_OR_LATER", ...

			TempFileCollection TemporaryFiles = new TempFileCollection();

			// Setup compile parameters
			// System.CodeDom.Compiler
			CompilerParameters CompileParams = new CompilerParameters();
			{
				CompileParams.GenerateInMemory        = false; // Always compile the assembly to a file on disk, so that we can load a cached version later if we have one
				CompileParams.GenerateExecutable      = false; // We always want to generate a class library, not an executable
				CompileParams.TreatWarningsAsErrors   = false; // Never fail compiles for warnings
				CompileParams.WarningLevel            = 4;     // Set the warning level so that we will actually receive warnings -  doesn't abort compilation as stated in documentation!
				CompileParams.IncludeDebugInformation = true;  // Always generate debug information as it takes minimal time

				CompileParams.OutputAssembly = OutputAssemblyPath.FullName; // This is the full path to the assembly file we're generating
#if !DEBUG
				// Optimise the managed code in Development
				CompileParams.CompilerOptions += " /optimize";
#endif
				Log.TraceVerbose("Compiling " + OutputAssemblyPath);

				// Keep track of temporary files emitted by the compiler so we can clean them up later
				CompileParams.TempFiles = TemporaryFiles;

				// Warnings as errors if desired
				CompileParams.TreatWarningsAsErrors = TreatWarningsAsErrors;

				// Add assembly references
				{
					if (ReferencedAssembies == null)
					{
						// Always depend on the CLR System assembly
						// Add to CompilerParameters::ReferencedAssemblies.@data
						CompileParams.ReferencedAssemblies.Add(Tag.Binary.SystemDll);
						CompileParams.ReferencedAssemblies.Add(Tag.Binary.SystemCoreDll);
					}
					else
					{
						// Add in the set of passed in referenced assemblies
						CompileParams.ReferencedAssemblies.AddRange(ReferencedAssembies.ToArray());
					}

					// The assembly will depend on this application
					// BuildToolAssembly::CodeBase = "file://D:/UERelease/Engine/Binaries/DotNET/BuildTool.exe"
					Assembly BuildToolAssembly = Assembly.GetExecutingAssembly();
					// BuildToolAssembly.Location = "D:\\UERelease\\Engine\\Binaries\\DotNET\\BuildTool.exe"
					CompileParams.ReferencedAssemblies.Add(BuildToolAssembly.Location);

					// The assembly will depend on the utilities assembly. Find that assembly
					// by looking for the one that contains a common utility class
					// CodeBase = "file://D:/UERelease/Engine/Binaries/DotNET/DotNETUtilities.DLL"
					Assembly UtilitiesAssembly = Assembly.GetAssembly(typeof(FileReference));
					CompileParams.ReferencedAssemblies.Add(UtilitiesAssembly.Location);
				}

				// 현재 CompileParams.ReferencedAssemblies(string)
				// System.dll, System.Core.dll, "D:\\UERelease\\Engine\\Binaries\\DotNET\\BuildTool.exe",
				// "D:\\UERelease\\Engine\\Binaries\\DotNET\\DotNETUtilities.dll"

				// Add preprocessor definitions
				if (PreprocessorDefines != null 
					&& 0 < PreprocessorDefines.Count)
				{
					CompileParams.CompilerOptions += Tag.Argument.CompilerOption.Define;
					for (int DefinitionIndex = 0; DefinitionIndex < PreprocessorDefines.Count; ++DefinitionIndex)
					{
						if (0 < DefinitionIndex)
						{
							CompileParams.CompilerOptions += ";";
						}

						CompileParams.CompilerOptions += PreprocessorDefines[DefinitionIndex];
						// CompileParams.ComilerOptions에, /define:WITH_FORWARDED_MODULE_RULES_CTOR; WITH_FORWARDED_TARGET_RULES_CSTOR; UE_4_17_OR_LATER; ...
					}
				}

				// @todo: Consider embedding resources in generated assembly file (version/copyright/signing)
			} // End CompileParams Edit

			// Create the output directory if it doesn't exist already
			// DirInfo = {D:\UERelease\Engine\Intermediate\Build\BuildRules}
			DirectoryInfo DirInfo = new DirectoryInfo(OutputAssemblyPath.Directory.FullName);
			if (!DirInfo.Exists)
			{
				try
				{
					DirInfo.Create();
				}
				catch (Exception Ex)
				{
					throw new BuildException(Ex, "Unable to create directory '{0}' for intermediate assemblies (Exception: {1})", OutputAssemblyPath, Ex.Message);
				}
			}

			// Compile the code
			CompilerResults CompileResults;
			try
			{
				Dictionary<string, string> ProviderOptions = new Dictionary<string, string>() { { Tag.ReservedStringID.CompilerVersion, Tag.ReservedStringID.ProviderOption } };
				CSharpCodeProvider Compiler = new CSharpCodeProvider(ProviderOptions);

				CompileResults = Compiler.CompileAssemblyFromFile(CompileParams, SourceFileNames.Select(x => x.FullName).ToArray());
			}
			catch (Exception Ex)
			{
				throw new BuildException(Ex, "Failed to launch compiler to compile assembly from source files:\n  {0}\n(Exception: {1})", String.Join("\n  ", SourceFileNames), Ex.ToString());
			}

			// Display compilation warnings and errors
			if (0 < CompileResults.Errors.Count)
			{
				Log.TraceInformation("While compiling {0}:", OutputAssemblyPath);
				foreach (CompilerError CurError in CompileResults.Errors)
				{
					Log.WriteLine(0, CurError.IsWarning ? LogEventType.Warning : LogEventType.Error, LogFormatOptions.NoSeverityPrefix, "{0}", CurError.ToString());
				}
				if (CompileResults.Errors.HasErrors || TreatWarningsAsErrors)
				{
					throw new BuildException("Unable to compile source files.");
				}
			}

			// Grab the generated assembly
			Assembly CompiledAssembly = CompileResults.CompiledAssembly;
			if (CompiledAssembly == null)
			{
				throw new BuildException("BuildTool was unable to compile an assembly for '{0}'", SourceFileNames.ToString());
			}

			// Clean up temporary files that the compiler saved
			TemporaryFiles.Delete();

			return CompiledAssembly;
		}
#endif
	}
}
