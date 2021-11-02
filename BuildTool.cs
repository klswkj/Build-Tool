using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using BuildToolUtilities;
// The example displays the following output when run on a Windows system:
//    Path.DirectorySeparatorChar: '\'
//    Path.AltDirectorySeparatorChar: '/'
//    Path.PathSeparator: ';'
//    Path.VolumeSeparatorChar: ':'
//    Path.GetInvalidPathChars:
// The example displays the following output when run on a Linux system:
//    Path.DirectorySeparatorChar: '/'
//    Path.AltDirectorySeparatorChar: '/'
//    Path.PathSeparator: ':'
//    Path.VolumeSeparatorChar: '/'
//    Path.GetInvalidPathChars:


namespace BuildTool
{
	// These are string compilation whose purpose is
	// to make it easier for developers to find source files
	// and to provide IntelliSense data for the module to Visual Studio
	internal static class Tag
    {
		public static string MyEngineName => "MyEngine";
		public static string My_Engine_Name => "My_Engine";
		public static string MY_ENGINE_NAME => "MY_ENGINE";

		internal static class ActionDescriptor
        {
			public static string Link        => "Link";
			public static string Compile     => "Compile";
			public static string Analyzing   => "Analyzing";
			public static string Archive     => "Archive";
			public static string Arch        => "arch"; // Local tool name with ToLower().
			public static string GenerateTLH => "GenerateTLH";
			public static string LinkLTO     => "Link-LTO";
			public static string LinkPGI     => "Link-PGI";
			public static string LinkPGO     => "Link-PGO";
			public static string ReLink      => "Relink";
			public static string Copy        => "Copy";
			public static string FixDeps     => "FixDeps";
			public static string Touch       => "Touch";
			public static string Resource    => "Resource";
			public static string PreBuild    => "PreBuild";
			public static string PostBuild   => "PostBuild";

			public static string Compiler         => "Compiler";
			public static string AppleCompiler    => "AppleCompiler";
			public static string ResourceCompiler => "ResourceCompiler";
		}
		
		internal static class Argument
        {
			public static string ModuleImplement   => "IMPLEMENT_MODULE_"; // For Module Constructure Boilerplate Macro in c++ Engine Code.
			public static string Input             => "-Input=";
			public static string Version           => "-Version=";
			public static string NoManifestChanges => "-NoManifestChanges";
			public static class CompilerOption
            {
				public static string Define => " /define:";
				public static class MSVC
				{
					public static string ForceIncludeName => "/FI";

					public static class Win32
					{
						public static string ForceSymbolReference => "/INCLUDE:_";
					}
					public static class Win64
					{
						public static string ForceSymbolReference => "/INCLUDE:";
					}
				}
			}
			public static class LinkOption
			{
				public static string WinMainCRTStartup => "WinMainCRTStartup"; // For LinkEnvironment::EntryPointOverride

				// GNU Compiler Collection.
				public static class GCC
                {
                    // Extension for Linking Options.
                    public static class Ext
                    {
                        public static string C  => ".c"; // C source code that must be preprocessed.
                        public static string I  => ".i"; // C source code that should not be preprocessed.
                        public static string II => ".ii"; // C++ source code that should not be preprocessed.
                        public static string M  => ".m"; // Objective-C source code, 
                                                        // Must Link with the libobjc library to make an Obj-c program work.
                        public static string MI  => ".mi"; // Obj-C code that should not be preprocessed.
                        public static string MII => ".mii"; // Obb-C cod ethat should not be preprocessed.
                        public static string H   => ".h"; // C, C++, Obj-C, Obj-C++ hedaer file to be turned into a PCH.
                        public static string S   => ".s"; // Assembler code.
                    }

                    public static string Fuse => "-fuse-ld="; // Set Defulat Linker
                    public static string LLD  => "lld";
                    public static string Gold => "gold"; // a linker for ELF files. https://en.wikipedia.org/wiki/Gold_(linker)
                    public static string BFD  => "bfd"; // Binary File Descriptor Library See : https://en.wikipedia.org/wiki/Binary_File_Descriptor_library

                    public static string C         => "-c"; // Compile or assmble the source files, but do not link.
                    public static string S         => "-S"; // Stop after the stage of compilation proper; do not assemble.
                    public static string E         => "-E"; // Stop after the preprocessing stage; do not run the compiler proper.
                    public static string NoLibc    => "-nolibc"; // Do not use the C library ro system librarires tightly.
                    public static string Nostdlib  => "-nostdlib"; // Do not use the standard system strtup files or libraries when linking.
                    public static string Entry     => "-entry="; // The argument is interpreted by the Linker. The GNU linker accepts either a symbol name or and address.
                    public static string PIE       => "-pie"; // Produce a dynamically linked position independent executable on targets that support it.
                    public static string NoPIE     => "-no-pie";
                    public static string StaticPIE => "-static-pie";
                    public static string O         => "-o "; // Place the primary output in file.
                                                             // This applies to whatever sort of output is being produced.
                    public static string SOName => "-soname="; // Shared (Libraries) Object Name. (*.so[=*.dll]) <=> (.a[=*.lib])

                    // See this : https://stackoverflow.com/questions/49138195/whats-the-difference-between-rpath-link-and-l
                    public static string RPath     => "-rpath="; // Run-time Search Path. Reponse
                    public static string RPathLink => "-rpath-link=";
                    public static string L         => "-L "; // Search the library named library when linking.
                                                             // The option is passed directly to the linker by GCC.

                    public static string StartGroup => "--start-group";
                }
            }
		}

		// Executable Name
		internal static class Binary
        {
			public static string PlatformDLLVersion => "140";
			public static string CluiSubDirName     => "1033";

			// MSVC ToolChain
			public static string CLUI    => "clui.dll";
			public static string ClExe   => "cl.exe"; // C L
			public static string C1Dll   => "c1.dll"; // C L
			public static string C1xxDll => "c1xx.dll"; // C One
			public static string C2Dll   => "c2.dll"; // C Two

			public static string MspdbsrvExe  => "mspdbsrv.exe";
			public static string MspdbCoreDll => "mspdcore.dll";
			public static string Mspft        => "mspft";
			public static string Msobj        => "msobj";
			public static string Mspdb        => "mspdb";

			public static string ClFilter        => "cl-filter.exe";
			public static string ClLinker        => "cl-linker.exe";
			public static string LinkFilter      => "link-filter.exe";
			public static string DefaultResource => "Default";
			public static string MetaData        => "Metadata";
			public static string PCLaunch        => "PCLaunch"; // PCLauncher (Legacy & Obsolete Resource.)
			public static string Unsetup         => "unsetup.bat";
			public static string Setup           => "setup";
			public static string RC              => "rc.exe";
			
			public static string MakefileBin              => "Makefile.bin";
			public static string SourceFileCacheBin       => "SourceFIleCache.bin";
			// public static string XMLConfigCacheBin => "XmlConfigCache.bin";
			public static string BuildConfigurationSchema => "BuildConfiguration.Scema.xsd";
			

			public static string DefaultGame   => "DefaultGame";   // {Name}Game
			public static string DefaultEditor => "DefaultEditor"; // {Name}Editor
			public static string DefaultClient => "DefaultClient"; // {Name}Client
			public static string DefaultServer => "DefulatServer"; // {Name}Server

			public static string DependencyCache        => "DependencyCache";
			public static string SNDBSBat               => "SNDBS.bat";
			public static string DBSBuild               => "dbsbuild.exe";
			public static string IncludeRewriteRulesIni => "include-rewrite-rules.ini";
			public static string SNDBSOutpuName         => "SNDBS";
			public static string XgConsoleExe           => "xgConsole.exe";

			public static string SystemDll     => "System.dll";
			public static string SystemCoreDll => "System.Core.dll";

			public static string DebuggingVisualizer => "MyEngine.nativs"; // {EngineName}.nativs

			public static string ProgramRules => "ProgramRules";
			public static string ModuleRules  => "ModuleRules";
			public static string Rules        => "Rules";

			public static string DataDrivenPlatformInfo => "DataDrivenPlatformInfo.ini";

#if FASTBUILD
			public static string FastBuild => "FBuild";
#endif
		}

		internal static class Boolean
        {
			public static string Zero  => "=0";
			public static string One   => "=1";
			public static string True  => "true";
			public static string False => "false";
        }

		internal static class Command
		{
			public static string CommandSeperator => "&&";

			internal static class CommandPrompt
			{
				public static string CarryOutStop => "/C"; // Execute and then stop.
				public static string CarryOutCont => "/K";
				public static string Redirection  => ">"; // Redirection Operator

                // Calls one batch program from another without stopping the parent batch program.
                // The call command accepts labels as the target of the call 
                public static string Call => "call";
                public static string StandardInputPipe  => "0";
				public static string StandardOutputPipe => "1";
				public static string StandardErrorPipe  => "2";
				public static string SupressConfirmToOverwrite => "/Y";  // To use suppresses prompting to confirm you want to overwrite an existing destination file.
				public static string ConfirmOverwrite          => "-/Y"; // Causes prompting to confirm you want to overwrite an existing destination file.
				public static string Type  => "type"; // built in command which displays the contents of a text file
				public static string Touch => "touch";
				public static string Null  => "nul";
			}

			internal static class PowerShell
            {
				public static string Command => "-c";
				public static string Copy    => "cp";
				public static string Force   => "-f";
            }

			internal static class Batch
            {
				public static string EchoOff => "@ehco off";
            }
		}

		internal static class Compiler
        {

        }

		internal static class Configuration
        {
			public static string Debug       => "Debug";
			public static string Development => "Development";
        }

		internal static class ConfigHierarchy
        {
			public static string Engine               => "{ENGINE}";
			public static string Project              => "{PROJECT}";
			public static string ExtEngine            => "{EXTENGINE}";
			public static string ExtProject           => "{EXTPROJECT}";
			public static string Platform             => "{PLATFORM}";
			public static string RestrictedProjectNFL => "{RESTRICTEDPROJECT_NFL}"; // Not for Licensees
			public static string RestrictedProjectNR  => "{RESTRICTEDPROJECT_NR}";  // Not Redistribution
			public static string Type                 => "{TYPE}";
			public static string User                 => "{USER}";
			public static string UserSettings         => "{USERSETTINGS}";
			public static string ED                   => "{ED}"; // Directory Prefix
			public static string EF                   => "{EF}"; // File Prefix
			public static string NFL                  => "NotForLicensees";
			public static string NR                   => "NotRedist";
			public static string Shippable            => "Shippable";
			public static string Base                 => "Base";
			public static string Default              => "Default";

			public static string LiteralEngine => "Engine";
			public static string Platforms     => "Platforms";
			public static string Restricted    => "Restricted";
			public static string Save          => "Save";
			public static string None          => "None";
		}

		internal static class ConfigKey
		{
			public static string BlueprintNativizationMethod     => "BlueprintNativizationMethod";
			public static string InstalledPlatformConfigurations => "InstalledPlatformConfigurations";
			public static string InstalledPlatformInfo           => "InstalledPlatformInfo";
			public static string Configuration => "Configuration=";
			public static string PlatformName  => "PlatformName=";
			public static string PlatformType  => "PlatformType=";
			public static string Architecture  => "Architecture=";
			public static string RequiredFile  => "RequiredFile=";
			public static string ProjectType   => "ProjectType=";
#pragma warning disable IDE1006 // Naming Styles
            public static string bCanBeDisplayed        => "bCanBeDisplayed=";
			public static string bAllowHotReloadFromIDE => "bAllowHotReloadFromIDE";
#pragma warning restore IDE1006 // Naming Styles

			public static string HasInstalledPlatformInfo => "HasInstalledPlatformInfo=";
			public static string ProgramEnabledPlugins    => "ProgramEnabledPlugins";

			public static string PlatformRequiresDataCrypto => "PlatformRequiresDataCrypto";
			public static string PakSigningRequired         => "PakSigningRequired";
			public static string PakEncryptionRequired      => "PakEncryptionRequired";
			public static string EncryptionKey              => "EncryptionKey";

			public static string SignPak       => "SignPak";
			public static string EncryptPak    => "EncryptPak";
			public static string RSAPrivateexp => "rsa.privateexp";
			public static string RSAModulus    => "rsa.modulus";
			public static string RSAPublicexp  => "rsa.publicexp";
			public static string AESKey        => "aes.key";

			public static string SecondaryEncryptionKeys => "SecondaryEncryptionKeys";
			public static string SigningPrivateExponent  => "SigningPrivateExponent";
			public static string SigningModulus          => "SigningModulus";
			public static string SigningPublicExponent   => "SigningPublicExponent";

			public static string ProjectKeyChain => "ProjectKeyChain";
		}

		internal static class ConfigSection
        {
			public static string ProjectPackagingSettings => "/Script/Editor.ProjectPackagingSettings";
			public static string Plugins                  => "Plugins";
			public static string BuildConfiguration       => "BuildConfiguration";


			public static string ScriptCryptoKeySetting => "/Script/CryptoKeys.CryptoKeysSettings";
			public static string PlatformCrypto         => "PlatformCrypto";
			public static string CoreEncryption         => "Core.Encryption";
			public static string ContentEncryption      => "ContentEncryption";
		}

		internal static class ConfigValue
		{
			public static string Disabled           => "Disabled";
			public static string InstalledPlatforms => "InstalledPlatforms";
		}

		internal static class SolutionContents
        {
			public static string HeaderFileFormatVersion      => "Microsoft Visual Studio Solution File, Format Version {0}";
			public static string HeaderMajorVersion           => "# Visual Studio {0}";
			public static string HeaderFullVersion            => "VisualStudioVersion = {0}";
			public static string HeaderMinimumOldestVSVersion => "MinimumVisualStudioVersion = {0}";

			
			public static string ProjectDeclaration  => "Project(\"{0}\") = \"{1}\", \"{2}\", \"{3}\"";
			public static string EndProject          => "EndProject";
			public static string ProjectSection      => "ProjectSection";
			public static string EndProjectSection   => "EndProjectSection";
			public static string PreProject          => "preProject";
			public static string PostProject         => "postProject";
			public static string SolutionItems       => "(SolutionItems)";
			public static string ProjectDependencies => "(ProjectDependencies)";

			public static string Global           => "Global";
			public static string EndGlobal        => "EndGlobal";
			public static string GlobalSection    => "GlobalSection";
			public static string EndGlobalSection => "EndGlobalSection";

			public static string PreSolution       => "preSolution";
			public static string PostSolution      => "postSolution";
			public static string SetAsStartProject => "StartupProject";


			public static string SolutionConfigurationPlatforms => "(SolutionConfigurationPlatforms)";
			public static string ProjectConfigurationPlatforms  => "(ProjectConfigurationPlatforms)";
			public static string SolutionProperties             => "SolutionProperties";
			public static string NestedProjects                 => "(NestedProjects)";
			public static string ExtensibilityGlobals           => "(ExtensibilityGlobals)";
			
			public static string ActiveConfiguration => ".ActiveCfg";
			public static string BuildConfiguration  => ".Build";
			public static string DeployConfiguration => ".Deploy";

			public static string HideSolutionNode => "HideSolutionNode";

			public static string SpaceIndent(int NumIndent)
			{
				string OutIndentTab = String.Empty;
				for (int i = 0; i < NumIndent; ++i)
				{
					OutIndentTab += "    ";
				}

				return OutIndentTab;
			}

			public static string Indent(int NumIndent)
			{
				string OutIndentTab = String.Empty;
				for (int i = 0; i < NumIndent; ++i)
				{
					OutIndentTab += "\t";
				}

				return OutIndentTab;
			}
		}

		internal static class CppProjectContents
        {
			public static string SolutionDir  => "$(SolutionDir)";
			public static string StdCpp14     => "/std:c++14";
			public static string StdCpp17     => "/std:c++17";
			public static string StdCppLatest => "/std:c++latest";

			// For DebuggerFlavor
			public static string AppHostLocalDebugger => "AppHostLocalDebugger";
			public static string WindowsLocalDebugger => "WindowsLocalDebugger";

			// Only verbatim format string
			internal static class Format
			{
				public static string ProjectStart   => "<Project>";
				public static string ProjectEnd     => "</Project>";
				public static string ItemGroupStart => "<ItemGroup>";
				public static string ItemGroupEnd   => "</ItemGroup>";
				public static string ProjectConfigurationStart  => "<ProjectConfiguration Include=\"{0}|{1}\">";
				public static string ProjectConfigurationEnd    => "</ProjectConfiguration>";
				public static string Configuration              => "<Configuration>{0}</Configuration>";
				public static string Platform                   => "<Platform>{0}</Platform>";
				public static string XMLVersionAndEncoding      => "<?xml version=\"{0}\" encoding=\"{1}\"?>";
				public static string ProjectToolVersionAndxmlns => "Project ToolsVersion=\"{0}\" xmlns=\"{1}\">";

				public static string PropertyGroupStart => "<PropertyGroup{0}{1}>";
				public static string PropertyGroupEnd   => "</PropertyGroup>";

				public static string ProjectGuid       => "<ProjectGuid>{0}</ProjectGuid>";
				public static string UniqueIndentiFier => "<UniqueIdentifier>{0}</UniqueIdentifier>";

				// which of the Windows-specific dependencies you are going to use.
				// ex) MFCProj, MAKEFileProj, Win32Proj
				public static string Keyword                    => "<Keyword>{0}</Keyword>";
				public static string RootNamespace              => "<RootNamespace>{0}</RootNamespace>";
				public static string MinimumVisualStudioVersion => "<MinimumVisualStudioVersion>{0}</MinimumVisualStudioVersion>";
				public static string TargetRuntime      => "<TargetRuntime>{0}</TargetRuntime>";
				public static string ProjectCapability  => "<ProjectCapability {0} />";
				public static string PropertyPageSchema => "<PropertyPageSchema {0} />";

				public static string ConfigurationType => "<ConfigurationType>{0}</ConfigurationType>";
				public static string UseDebugLibraries => "<UseDebugLibraries>{0}</UseDebugLibraries>"; // "true" or "false"
				
				public static string PlatformToolset          => "<PlatformToolset>{0}</PlatformToolset>";
				public static string PlatformToolSetCondition => "<PlatformToolset Condition={0}>{1}</PlatformToolset>";

				public static string WholeProgramOptimization => "<WholeProgramOptimization>{0}</WholeProgramOptimization>";
				public static string CharacterSet             => "<CharacterSet>{0}</CharacterSet>";
				public static string LinkIncremental          => "<LinkIncremental>{0}</LinkIncremental>";
				public static string WarningLevel             => "<WarningLevel>{0}</WarningLevel>";
				public static string SDLCheck                 => "<SDLCheck>{0}</SDLCheck>";
				public static string ConformanceMode          => "<ConformanceMode>{0}</ConformanceMode>";

				public static string Import      => "<Import {0} />";
				public static string ImportGroup => "<ImportGroup {0} />";

				public static string ClCompileStart               => "<ClCompile>";
				public static string ClCompileEnd                 => "</ClCompile>";
				public static string AdditionalOptions            => "<AdditionalOptions>{0}</AdditionalOptions>";
				public static string LocalDebuggerCommandArgument => "<LocalDebuggerCommandArguments>{0}</LocalDebuggerCommandArguments>";
				public static string DebuggerFlavor               => "<DebuggerFlavor>{0}</DebuggerFlavor>";

				public static string PreBuildEvent            => "<PreBuildEvent>{0}</PreBuildEvent>";
				public static string PostBuildEvent           => "<PostBuildEvent>{0}</PostBuildEvent>";
				public static string PreLinkEvent             => "<PreLinkEvent>{0}</PreLinkEvent>";
				public static string PreBuildEventUseInBuild  => "<PreBuildEventUseInBuild>{0}</PreBuildEventUseInBuild>";
				public static string PostBuildEventUseInBuild => "<PostBuildEventUseInBuild>{0}</PostBuildEventUseInBuild>";
				public static string PreLinkEventUseInBuild   => "<PreLinkEventUseInBuild>{0}</PreLinkEventUseBuild>";

				public static string NMakeBuildCommandLine        => "<NMakeBuildCommandLine>{0}</NMakeBuildCommandLine>";
				public static string NMakeReBuildCommandLine      => "<NMakeReBuildCommandLine>{0}</NMakeReBuildCommandLine>";
				public static string NMakeCleanCommandLine        => "<NMakeCleanCommandLine>{0}</NMakeCleanCommandLine>";
				public static string NMakeOutput                  => "<NMakeOutput>{0}</NMakeOutput>";
				public static string NMakePreprocessorDefinitions => "<NMakePreprocessorDefinitions>%(NMakePreprocessorDefinitions){0}</NMakePreprocessorDefinitions>";
				public static string NMakeForcedIncludes          => "<NMakeForcedIncludes>$(NMakeForcedIncludes){0}</NMakeForcedIncludes>";
				public static string NMakeAssemblySearchPath      => "<NMakeAssemblySearchPath>$(NMakeAssemblySearchPath){0}</NMakeAssemblySearchPath>";

				public static string OutDir      => "<OutDir>{0}{1}</OutDir>";
				public static string IntDir      => "<IntDir>{0}{1}</IntDir>"; // IntermediateDirectory
				public static string IntDirStart => "<IntDir>";

				public static string IncludePath      => "<IncludePath>$(IncludePath){0}</IncludePath>";
				public static string AdditonalOptions => "<AdditionalOptions>{0}</AdditionalOptions>";

				public static string Include                      => "<{0} Include=\"{1}\">";
				public static string AdditionalIncludeDirectories => "<AdditionalIncludeDirectories>{0};{1}</AdditionalIncludeDirectories>";
				public static string ForcedIncludeFiles           => "<ForcedIncludeFiles>{0};{1}</ForcedIncludeFiles>";

				public static string Filter          => "<Filter>{0}</Filter>"; // For project filter files.
				public static string SourcePathStart => "<SourcePath>";
				public static string SourcePathEnd   => "</SourcePath>";

				public static string ItemDefinitionGroupStart => "<ItemDefinitionGroup>";
				public static string ItemDefinitionGroupEnd   => "</ItemDefinitionsGroup>";
				public static string CleanDependsOn           => "<CleanDependsOn>$(CleanDependsOn){0};</CleanDependsOn>";
				public static string CppCleanDependsOn        => "<CppCleanDependsOn>{0}</CppCleanDependsOn>";

				public static string IncludePathEmpty   => "<IncludePath/>";
				public static string ReferencePathEmpty => "<ReferencePath/>";
				public static string LibraryPathEmpty   => "<LibraryPath/>";
				public static string LibraryWPathEmpty  => "<LibraryWPath/>";
				public static string SourcePathEmpty    => "<SourcePath/>";
				public static string ExcludePathEmpty   => "<ExcludePath/>";

				// public static string PropertyGroupCondition = "";
				// public static string PropertyGroupLabel = "";
			}

			public static string Indent(int NumIndent)
			{
				string OutIndentTab = String.Empty;
				for (int i = 0; i < NumIndent; ++i)
                {
					OutIndentTab += "  ";
                }

				return OutIndentTab;
			}
		}

		internal static class CppContents
		{
            #region REST
            public static string PragmaOnce => "#pragma once";
			public static string Include    => "#include ";
			public static string Define     => "#define ";
			public static string Import     => "#import";
			public static string Undef      => "#undef ";
			public static string If         => "#if ";
			public static string Ifdef      => "#ifdef ";
			public static string Ifndef     => "#ifndef ";
			public static string Else       => "#else";
			public static string Elif       => "#elif ";
			public static string Endif      => "#endif";

			public static string Pragma         => "#pragma ";
			public static string Warning        => "warning";
			public static string Deprecated     => "deprecated";
			// When you link the project, the linker throws a LNK2038 error
			// if the project contains two objects that have the same name but each has a different value.
			public static string DetectMismatch => "detect_mismatch";
			public static string Message        => "message"; // #pragma message ($(message))
			public static string Comment        => "comment"; // #pragma comment ($(comment))
			public static string Intrinsic      => "intrinsic"; // tells the compiler that a function has known behavior.
			public static string PushMacro      => "push_macro";
			public static string PopMacro       => "pop_macro";

			public static string RuntimeCheck         => "runtime_check";
			public static string RuntimeCheck_s       => "s";       // Enables stack (frame) verification.
			public static string RuntimeCheck_c       => "c";       // Reports when a value is assigned to a smaller data type that results in a data loss.
			public static string RuntimeCheck_u       => "u";       // Reports when a variable is used before it's defined.
			public static string RuntimeCheck_off     => "off";     // it turns the run-time error checks listed in the table above, off.
			public static string RUntimeCheck_restore => "restore"; // resets the run-time error checks to the ones that you specified using the /RTC compiler option.

			public static string Optimize     => "optimize";
			public static string Optimize_g   => "g"; // Enable global optimizations.
			public static string Optimize_s   => "s"; // Specify short or fast sequences of machine code.
			public static string Optimize_y   => "y"; // Generate frame pointers on the program stack.
			public static string Optimize_on  => "on";
			public static string Optimize_off => "off";

			// For /clr Compiler Option.
			public static string Managed   => "managed";
			public static string Unmanaged => "unmanaged";

			public static string Region    => "region ";
			public static string EndRegion => "#endregion ";

#endregion Rest
            // Will be insert into C++ Header
            internal static class Def
			{
				public static string API         => "_API";
				public static string DllExport   => "=DLLEXPORT";
				public static string DllImport   => "=DLLIMPORT";
				public static string AssignEmpty => "=";

				public static string IsEngineModule    => "IS_ENGINE_MODULE";
				public static string IsProgram         => "IS_PROGRAM";
				public static string IsMonolithic      => "IS_MONOLITHIC";
				public static string DeprecatedForGame => "DEPRECATED_FORGAME";
				public static string DeprecatedValue   => "DEPRECATED";

				public static string Unicode  => "UNICODE";
#pragma warning disable IDE1006 // Naming Styles
                public static string _Unicode => "_UNICODE";
#pragma warning restore IDE1006 // Naming Styles
                public static string Debug                     => "_DEBUG";
				public static string NDebug                     => "NDEBUG";
				public static string BuildDebug                 => "BUILD_DEBUG";
				public static string BuildDevelopment           => "BUILD_DEVELOPMENT";
				public static string BuildShipping              => "BUILD_SHIPPING";
				public static string BuildTest                  => "BUILD_TEST";
				public static string OverridePlatformHeaderName => "OVERRIDE_PLATFORM_HEADER_NAME"; // Literal Value Fix Suggestion : 
				public static string Editor                     => "_EDITOR";                       // If set TargetType == TargetType.Editor. then =1

				#region RULES_ASSEMBLY
				public static string WithForwardedModuleRulesCTOR => "WITH_FORWARDED_MODULE_RULES_CTOR"; // ModuleRules Constructor
				public static string WithForwardedTargetRulesCTOR => "WITH_FORWARDED_MODULE_RULES_CTOR";
				public static string BuildMinorVersion => "MINOR_VER_{0}_OR_LATER"; // must be used String.Format (Verbatim expr.) // "UE_4_{0}_OR_LATER"
				public static string UseMallocProfiler => "USE_MALLOC_PROFILER";
				public static string DisableGeneratedIniWhenCooked => "DISABLE_GENERATED_INI_WHEN_COOKED";
				public static string DisableNonFileSystemIniWhenCooked => "DISABLE_NON_FILESYSTEM_INI_WHEN_COOKED";
				public static string DisableUnVerfiedCertificateLoading => "DISABLE_UNVERIFIED_CERTIFICATE_LOADING";
				#endregion RULES_ASSEMBLY

				public static string IncludeChaos                => "INCLUDE_CHAOS";
				public static string WihtPhysX                   => "WITH_PHYSX";
				public static string WithChaos                   => "WITH_CHAOS";
				public static string WithChaosClothing           => "WITH_CHAOS_CLOTHING";
				public static string WithChaosNeedsToBeFixed     => "WITH_CHAOS_NEEDS_TO_BE_FIXED";
				public static string PhysicsInterfacePhysX       => "PHYSICS_INTERFACE_PHYSX";
				public static string WithApex                    => "WITH_APEX";
				public static string WithApexClothing            => "WITH_APEX_CLOTHING";
				public static string WithClothCollisionDetection => "WITH_CLOTH_COLLISION_DETECTION";
				public static string WithPhysXCooking            => "WITH_PHYSX_COOKING";
				public static string WithNVCloth                 => "WITH_NVCLOTH";
				public static string WithCustomSQStructure       => "WITH_CUSTOM_SQ_STRUCTURE";
				public static string WithImmediatePhysX          => "WITH_IMMEDIATE_PHYSX";

				public static string WithEngine               => "WITH_ENGINE"; // TargetRules.bCompileAgainstEngine
				public static string WithEditor               => "WITH_EDITOR"; // TargetRules.bBuildEditor
				public static string WithEditoronlyData       => "WITH_EDITORONLY_DATA";
				public static string WithServerCode           => "WITH_SERVER_CODE";
				public static string WithDeveloperTools => "WITH_DEVELOPER_TOOLS";
				public static string WithApplicationCore      => "WITH_APPLICATION_CORE"; // TargetRules.bCompileAgainstApplicationCore
				public static string WithCoreUObject          => "WITH_COREUOBJECT";
				public static string WithPluginSupport        => "WITH_PLUGIN_SUPPORT";

				// For SLATE_API, UMG and platform accessbility at Applicationcore,
				// #include <uiautomationcore.lib>
				public static string WithAccessiblity               => "WITH_ACCESSIBILITY";
				public static string WithPerfConters                => "WITH_PERFCOUNTERS";
				public static string WithDevAutomationTest          => "WITH_DEV_AUTOMATION_TESTS";
				public static string WithPerformanceAutomationTests => "WITH_PERF_AUTOMATION_TESTS";
				public static string WithLoggingToMemory            => "WITH_LOGGING_TO_MEMORY";
				public static string WithPushModel                  => "WITH_PUSH_MODEL"; // For supporting network. without changing changing the underlying replication machinery.
				public static string WithCEF3                       => "WITH_CEF3";
				public static string WithXGEController              => "WITH_XGE_CONTROLLER"; // IncrediBuild
				public static string WithLiveCoding                 => "WITH_LIVE_CODING";
				public static string LiveCodingEngineDir            => "LIVE_CODING_ENGINE_DIR";
				public static string LiveCodingProjectDir           => "LIVE_CODING_PROJECT_DIR";

				public static string EnableProfileGuidedOptimization => "ENABLE_PGO_PROFILE"; // See more : https://docs.microsoft.com/en-us/cpp/build/profile-guided-optimizations?view=msvc-160
				public static string UseVorbisForStream              => "USE_VORBIS_FOR_STREAMING"; // For .ogg extension
				public static string UseXMA2ForStream                => "USE_XMA2_FOR_STREAM"; // .xma(Xbox Media Audio File)
				public static string UseStatsWithoutEngine           => "USE_STATS_WITHOUT_ENGINE";
				public static string UseLoggingInShipping            => "USE_LOGGING_IN_SHIPPING";

				// Core/Misc/App.h , WindowsPlatformCrashContext.cpp
				// USE_NULL_RHI =1 => BuildHoloLens, BuildWindows, BuildLinux Target::Server 
				public static string UseNullRHI             => "USE_NULL_RHI"; 
				public static string UseCachedFreedOSAllocs => "USE_CACHE_FREED_OS_ALLOCS";
				public static string UseChecksInShipping    => "USE_CHECKS_IN_SHIPPING";

				public static string EnableMeshEditor                 => "ENABLE_MESH_EDITOR";
				public static string ImplementEncryptionKey           => "IMPLEMENT_ENCRYPTION_KEY_REGISTRATION()";
				public static string RegisterEncryptionKey            => "REGISTER_ENCRYPTION_KEY_REGISTRATION()";
				public static string ImplementSigningKey              => "IMPLEMENT_SIGNING_KEY_REGISTRATION()";
				public static string RegisterSigningKey               => "REGISTER_SIGNING_KEY";
				public static string ListArgument                     => "LIST_ARGUMENT";
				public static string EngineBaseDirAdjust              => "ENGINE_BASE_DIR_ADJUST";
				public static string BuildToolModuleManifest          => "BUILDTOOL_MODULE_MANIFEST";
				public static string BuildToolModuleManifestDebuggame => "BUILDTOOL_MODULE_MANIFEST_DEBUGGAME";
				public static string BuildToolCompiledPlatform        => "BUILDTOOL_COMPILED_PLATFORM";
				public static string BuildToolCompiledTarget          => "BUILDTOOL_COMPILED_PLATFORM";

				public static string PlatformSupportsLLM => "PLATFORM_SUPPORTS_LLM"; // For EngineCode/Generic/PAL/LowLevelMemoryTracker.h

				public static string SuppressMonoithicsHeaderWarning => "SUPPRESS_MONOLITHIC_HEADER_WARNINGS";
				
				public static string ProjectName => "PROJET_NAME=";
				public static string TargetName  => "TARGET_NAME=";
				public static string AppName     => "APP_NAME";

				// Target with Configuration
				public static string TargetGame   => "TARGET_GAME";
				public static string TargetClient => "TARGET_CLIENT";
				public static string TargetEditor => "TARGET_EDITOR";
				public static string TargetServer => "TARGET_SERVER";

				// Resource
				public static string OriginalFileName    => "ORIGINAL_FILE_NAME=\"";
				public static string BuiltFormChangeList => "BUILT_FROM_CHANGELIST=";
				public static string BuildVersion        => "BUILD_VERSION=";

			}
		}

		internal static class Directory
		{
			public static string Build           => "Build";
			public static string Engine          => "Engine";
			public static string SourceCode      => "SourceCode";    // Source -> SourceCode
			public static string Platforms       => "Platforms";
			public static string EngineCode      => "EngineCode";    // Runtime -> EngineCode
			public static string EngineAndEditor => "Engine&Editor"; // Developer -> Engine&Editor
			public static string EditorOnly      => "EditorOnly";    // Editor -> EditorOnly
			public static string ExternalTools   => "ExternalTools"; // Programs -> ExternalTools

			public static string Project    => "Project";
			public static string Generated  => "Generated";     // Intermediate -> Generated
			public static string ThirdParty => "ThirdParty";
			public static string Enterprise => "Enterprise";
			public static string Plugins    => "Plugins";
			public static string Logs       => "Logs";
			public static string Binaries   => "Binaries";
			public static string Contents   => "Contents";
			public static string Content    => "Content"; // For ProjectKeyChain

			public static string BuildRules => "BuildRules";

			public static string DotVS                 => ".vs";
			public static string V14                   => "v14";
			public static string V15                   => "v15";
			public static string DependencyLists       => "DependencyLists";
			public static string Lib                   => "lib";
			public static string Script                => "Script";
			public static string BuildVersion          => "BuildVersion";
			public static string Resources             => "Resources";
			public static string NativizedAssets       => "NativizedAssets";
			public static string Mods                  => "Mods";
			public static string UnzippedFrameworks    => "UnzippedFrameworks";
			public static string Inc                   => "Inc";
			public static string Config                => "Config";
			public static string Saved                 => "Saved";
			public static string BatchFiles            => "BatchFiles";
			public static string Extras                => "Extras";
			public static string VisualStudioDebugging => "VisualStduioDebugging";
			public static string EngineName            => "MyEngine";
			public static string Documents             => "Documents"; // For Platform mac or Unix OS
			public static string EditorRuns            => "EditorRuns";
			public static string Bin                   => "bin";
			public static string X86                   => "x86";
			public static string X64                   => "x64";
			public static string Win64                 => "Win64";
			public static string Redist                => "Redist";
			public static string MSVC                  => "MSVC";
			public static string Common                => "Common";
			public static string SN_DBS                => "SN-DBS";
			public static string SNDBS                 => "SNDBS";
			public static string SNDBSTemplates        => "SNDBSTemplates";

			public static string XgConsole             => "xgConsole";
			public static string IbConsole             => "ib_console";

			public static string Library           => "Library";
			public static string ApplcationSupport => "Application Support";
			public static string Mac   => "Mac";
			public static string Linux => "Linux";

			public static string AppFolder => ".app/Contents/MacOS/";

			// "Public" Folder, which is added as an include path if another module depends on your module

			// For leaf modules like a game there is little benefit to a public/private split,
			// but for library modules it helps to clearly mark the public and private portions of the module and
			// indicate which parts are interface versus implementation.

			public static string Public     => "Public";
			public static string PublicOnly => "PublicOnly";
			public static string Private    => "Private";

			// For UClass, UObject
			public static string Classes  => "Classes"; 
			public static string PreBuild => "PreBuild";

			public static string Windows => "Windows";
			public static string MacOS   => "MacOS";
			public static string MacOSVersion     => "/System/Library/CoreServices/SystemVersion.plist";
			public static string MacDistcc        => "/Library/Application Support/Developer/Shared/Xcode/Plug-ins/Distcc 3.2.xcplugin/Contents/usr/bin";
			public static string MacDistCodePlist => "/Library/Preferences/com.marksatt.DistCode.plist";

			public static string ShellPath => "/bin/sh";
			public static string User      => "User";
#if FASTBUILD
			public static string FastBuild => "FBuild";
#endif
		}

		// EnvironmentVariableTarget.System
		internal static class EnvVar
		{
			public static string PATH                => "PATH";
			public static string HOME                => "HOME";
			public static string RootDir             => "RootDir";
			public static string EngineDir           => "EngineDir";
			public static string EnterpriseDir       => "EnterpriseDir";
			public static string ProjectDir          => "ProjectDir";
			public static string TargetName          => "TargetName";
			public static string TargetPlatform      => "TargetPlatform";
			public static string TargetConfiguration => "TargetConfiguration";
			public static string TargetType          => "TargetType";
			public static string ProjectFile         => "ProjectFile";
			public static string PluginDir           => "PluginDir";
			
			// BuildHostPlatform
			public static string COMSPEC => "COMSPEC";

			public static string NumBuildToolTasks => "NUM_BUILDTOOL_TASKS";
			public static string DistccFallback    => "DISTCC_FALLBACK";
			public static string DistccVerbose     => "DISTCC_VERBOSE";
			public static string CommandPath       => "{COMMAND_PATH}";
#if FASTBUILD
			public static string FASTBuildExecutablePath => "FASTBUILD_EXECUTABLE_PATH";
			public static string FASTBuildCachePath      => "FASTBUILD_CACHE_PATH";
			public static string FASTBuildBrokeragePath  => "FASTBUILD_BROKERAGE_PATH";
			public static string FASTBuildCoordinator    => "FASTBUILD_COORDINATOR";
#endif
			public static string DXSDKDirMacro           => "$(DXSDK_DIR)";
			public static string CommonProgramFilesMacro => "$(CommonProgramFiles)";

			public static string DXSDKDir           => "$DXSDK_DIR$";
			public static string CommonProgramFiles => "$CommonProgramFiles$";
			public static string WindowsSDKBasePath => "$WindowsSDKBasePath$";
			public static string CLFilterRoot       => "$CLFilterRoot$";
			public static string Root               => "$Root$";

			public static string RawDXSDKDir           => "DXSDK_DIR";
			public static string RawCommonProgramFiles => "CommonProgramFiles";
			public static string RawDurangoXDK         => "DurangoXDK";

			public static string SCERootDir => "SCE_ROOT_DIR";
		}

		// Filename Extension
		internal static class Ext
		{
			public static string Exe      => ".exe";
			public static string Dll      => ".dll";
			public static string Bin      => ".bin";
			public static string Obj      => ".obj";
			public static string O        => ".o";
			public static string Lib      => ".lib";
			public static string Manifest => ".manifest";
			public static string App      => ".app";
			public static string Response => ".response";
			public static string Res      => ".res";
			public static string RcRes    => ".rc.res"; // ResourceScript's Resorce
			public static string RC       => ".rc";
			public static string RC2      => ".rc2";
			public static string Dat      => ".dat";
			public static string JSON     => ".json";
			public static string Txt      => ".txt";
			public static string Bat      => ".bat";
			public static string Shell    => ".sh";
			public static string Pch      => ".pch";
			public static string GCCPch   => ".gch";
			public static string Html     => ".html";

			public static string Dylib => ".dylib"; 
			public static string Tlh   => ".tlh";
			public static string Tli   => ".tli";

			public static string Solution                 => ".sln"; // Text-based, shared
			public static string SolutionUserOption       => ".suo"; // Binary, user-specific solution options
			public static string VS11SolutionUserOption   => ".v11.suo";
			public static string VS12SolutionUserOption   => ".v12.suo";
			public static string VCSharpProject           => ".vcsproj";
			public static string VCSharpProjectReferences => ".csproj.References";
			public static string VCPlusPlusProject        => ".vcxproj";

			public static string Header      => ".h";
			public static string CppHeader   => ".hpp";
			public static string CppSource   => ".cpp";
			public static string CSource     => ".c";
			public static string CCSource    => ".cc";
			public static string Inline      => ".inl";
			public static string ObjCSource  => ".m";
			public static string ObjCSource2 => ".mm";
			public static string ISPH        => ".isph";
			public static string ISPC        => ".ispc";
			public static string PList       => ".plist";
			public static string Ini         => ".ini";

			public static string SQLServerCompactDatabaseFile => ".sdf";

			public static string DummyHeader => ".dummy.h";

			public static string BuildCS                => ".build.cs"; // Build Module (Binaries)
			public static string TargetCS               => ".target.cs"; // Build Executable or Modules.
			public static string AutomationCharpProject => ".automation.csproj";

			public static string Out        => ".out";
			public static string Plugin     => ".uplugin";
			public static string Project    => ".uproject";
			public static string ProjectDir => ".uprojectdirs";
			public static string Document   => ".udn";

			// For AssertionMacro.h
			// PLATFORM_CODE_SECTION(DebugExt) retval FuncName()
			// __declspec(code_seg(DebugExt)) retval FuncName()
			// See this : https://docs.microsoft.com/en-us/cpp/cpp/code-seg-declspec?view=msvc-160
			public static string Debug              => ".uedbg";
			public static string Target             => ".target";
			public static string Modules            => ".modules";
			public static string Version            => ".version";
			public static string Gen                => ".gen";
			public static string GenCPP             => ".gen.cpp";
			public static string InitGenCPP         => ".init.gen.cpp";
			public static string HeaderToolPath     => ".htpath";
			public static string HeaderToolManifest => ".htmanifest";
			public static string Dependencies       => ".dependencies";
			public static string BuildSettings      => ".BuildSettings";
			public static string CRT                => ".CRT";
			public static string Ran                => ".ran";
			public static string Lc                 => ".lc";
			public static string LcObj              => ".lc.obj";

			public static string BFF                => ".bff"; // Building a Backend for Frontend 
			public static string SNDBSToolIni       => ".sn-dbs-tool.ini";
			public static string XgeXML             => ".xge.xml";

			// Procompiled Manifest Ext
			public static string PreCompiled  => ".precompiled"; 

			// Intermediate File Extension for Engine SourceCode Header File(C++)
			public static string Optimized    => ".Optimized";
			public static string NonOptimized => ".NonOptimized";
			public static string RTTI         => ".RTTI";
			public static string NonRTTI      => ".NonRTTI";

			public static string Exceptions   => ".Exceptions";
			public static string NoExceptions => ".NoExceptions";

			// For Shadow Variable Warning
			public static string ShadowErrors => ".ShadowErrors";
			public static string ShadowWarnings => ".ShadowWWarnings";
			public static string NoShadow => ".NoShadow";

			// For CppCompileEnvirnment::UnsafeTypeCastWarningLevel
			public static string TypeCastErrors => ".TypeCastErrors";
			public static string TypeCastWarnings => ".TypeCastWarnings";
			public static string NoTypeCast => ".NoTypeCast";
			
			// For CppCompileEnvirnment::bEnableUndefinedIdentiferWarnings
			public static string Undef => ".UnDef";
			public static string NoUndef => ".NoUndef";

			public static string GeneratedWrapper => ".GeneratedWrapper";
		}

		internal static class TxtFileName
        {
			public static string CachedEngineInstalled => "InstalledBuild.txt";
			public static string DependencyListAllModule => "DependencyList-AllModules.txt";
			public static string DependencyList => "DependencyList.txt";

		}

		internal static class GlobalArgument
		{
			// BuildDescriptor.cs
			public static string ModuleWithSuffix => "-ModuleWithSuffix=";
			public static string TargetList => "-TargetList=";
			public static string Target => "-Target=";
			public static string TargetType => "-TargetType=";
			public static string Project => "-Project=";
			public static string Architecture => "-Architecture=";
			public static string UsePrecompiled => "-UsePrecompiled";
			public static string Quiet => "-Quiet";
			public static string WaitMutex => "-WaitMutex";
			public static string FromMsBuild => "-FromMsBuild";
			public static string FastPDB => "-FastPDB";
			public static string SkipCompile => "-skipcompile";

			public static string Input => "-Input=";
			public static string Version => "-Version=";
			public static string NoManifestChanges => "-NoManifestChanges";

			public static string VS2015 => "-2015";
			public static string VS2017 => "-2017";
			public static string VS2019 => "-2019";

			public static string LowLevelMemoryTracker => "-LLM";
			public static string LowLevelMemoryStatsCSV => "-LLMCSV";
		}

		internal static class GUID
		{
            #region Reserved GUID
            public static string VCSharpProject => "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}";
			public static string VCPlusPlusProject => "{8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942}";
			public static string SolutionFolders => "{2150E333-8FDC-42A3-9474-1A3956D46DE8}";
			public static string ProjectFolders => "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}";
            #endregion Reserved GUID

            #region Custom GUID
			public static string DebuggerVisualizer => "{94665DEA-390D-4926-8047-3E9DF88986D8}"; // .natvis
            #endregion Custom GUID
        }

        internal static class JSONField
        {
			public static string EngineDir => "$(EngineDir)";
			public static string ProjectDir => "$(ProjectDir)";
			public static string Project => "Project";
			public static string Architecture => "Architecture";
			public static string Platform => "Platform";
			public static string Environment => "Environment";
			public static string Launch => "Launch";
			public static string BuildProducts => "BuildProducts";
			public static string Path => "Path";
			public static string Type => "Type";
			public static string RuntimeDependencies => "RuntimeDependencies";
			public static string IgnoreIfMissing => "IgnoreIfMissing";
			public static string AdditionalProperties => "AdditionalProperties";
			public static string Name => "Name";
			public static string Value => "Value";
			public static string TargetName => "TargetName";
			public static string TargetType => "TargetType";
			public static string Configuration => "Configuration";
			public static string Version => "Version";
			public static string Actions => "Actions";
			public static string ProjectFile => "ProjectFile";
			public static string Binaries => "Binaries";
			public static string Modules => "Modules";

			// BuildVersion
			public static string MajorVersion => "MajorVersion";
			public static string MinorVersion => "MinorVersion";
			public static string BuildVersion => "BuildVersion";
			public static string PatchVersion => "PatchVersion";
			public static string ChangeList => "ChangeList";
			public static string CompatibleChangeList => "CompatibleChange";
			public static string IsLicenseeVersion => "IsLicenseeVersion";
			public static string IsPromotedBuild => "IsPromotedBuild";
			public static string BranchName => "BranchName";
			public static string BuildId => "BuildId";

			#region RULES_ASSEMBLY
			public static string SourceFiles => "SourceFiles";
			public static string EngineVersion => "SourceVersion";
            #endregion RULES_ASSEMBLY
        }

        // ModuleName
        internal static class Module
		{
			public static string TargetSuffix => "Target";
			public static string TargetRulesSuffix => "TargetRules";

			// Plugins && ThirdPary
			internal static class ThirdParty
            {
				public static string Apex => "APEX";
				public static string NvCloth => "NvCloth";
				public static string PhysX => "PhysX";
				public static string ZLib => "zlib";
				public static string LibPNG => "UElibPng";
				public static string Vorbis => "Vorbis";
				public static string Ogg => "UEOgg";
				public static string CEF3 => "CEF3";
			}

			internal static class Plugins
            {
				public static class GameplayAbilities
                {
					public static string GameplayAbilitiesEditor => "GameplayAbilitiesEditor";
				}
				public static string TcpMessaging => "TcpMessaging";
				public static string AndroidPermission => "AndroidPermission";
			}

			internal static class Engine
            {
				public static string EngineModule => "Engine";
				public static string Chaos => "Chaos"; // Runtime(EngineCode)
				public static string CoreUObject => "CoreUObject";
				public static string FieldSystemCore => "FieldSystemCore";
				public static string Launch => "Launch";
				public static string PhysicsCore => "PhysicsCore";
				public static string Landscape => "Landscape";
				public static string UMG => "UMG"; // Motion Graphic
				public static string GameplayTags => "GameplayTags";
				public static string MaterialShaderQualitySettings => "MaterialShaderQualitySettings";
				public static string AudioMixer => "AudioMixer";
				public static string AIModule => "AIModule";
				public static string CinematicCamera => "CinematicCamera";
				public static string GameplayTasks => "GameplayTasks";
				public static string NavigationSystem => "NavigationSystem";
				public static string BuildSettings => "BuildSettings";
				public static string CEF3Utils => "CEF3Utils";
				public static string ShaderCompileWorker => "ShaderCompileWorker";
				public static string LiveCodingConsole => "LiveCodingConsole";

				internal static class Online
                {
					public static string Voice => "Voice";
                }
				internal static class PacketHandlers
                {
					// Module in Module
					public static string PacketHandler => "PacketHandler"; // .dll (module)
					public static string ReliabilityHandlerComponent => "ReliabilityHandlerComponent"; // .dll (module)

				}

				public static string VulkanRHI => "VulkanRHI";
				public static string OpenGLDrv => "OpenGLDrv";
				public static string MetalRHI => "MetalRHI";

				internal static class RHI
                {
					// SourceCode (.h / .cpp)
					public static string DynamicRHI => "DynamicRHI"; // .h / .cpp
					public static string RHICommandList => "RHICommandList"; // .h / .cpp
					public static string RHIUtilities => "RHIUtilities"; // .h / .cpp
                }

				internal static class Windows
                {
					// ModuleName
					public static string D3D11RHI => "D3D11RHI";
					public static string D3D12RHI => "D3D12RHI";
                }

			}

			// Developer
			internal static class EngineAndEditor
			{
				public static string GameplayDebugger => "GameplayDebugger";
				public static string CollisionAnalyzer => "CollisionAnalyzer";
				public static string LogVisualizer => "LogVisualizer";
				public static string MaterialUtilities => "MaterialUtilities";
				public static string LocalizationService => "LocalizationService";
				public static string SourceControl => "SourceControl";
				public static string AITestSuite => "AITestSuite";
				public static string AndroidDeviceDetection => "AndroidDeviceDetection";
			}

			internal static class Editor
			{
				public static string EditorEd=> "EditorEd";
				public static string Documentation => "Documentation";
				public static string Kismet => "Kismet";
				public static string KismetCompiler => "KismetCompiler";
				public static string LocalizationDashBoard => "LocalizationDashBoard";
				public static string MainFrame => "MainFrame";
				public static string TranslationEditor => "TranslationEditor";
				public static string GraphEditor => "GraphEditor";
				public static string AudioEditor => "AudioEditor";
				public static string LandscapeEditor => "LandscapeEditor";
				public static string PropertyEditor => "PropertyEditor";
				public static string BlueprintGraph => "BlueprintGraph";
				public static string UMGEditor => "UMGEditor"; // Motion Graphic Editor
				public static string ConfigEditor => "ConfigEditor";
				public static string MovieSceneTools => "MovieSceneTools";
				public static string Sequencer => "Sequencer";
				public static string AnimGraph => "AnimGraph";
				public static string HierarchicalLODOutliner => "HierarchicalLODOutliner";
				public static string PixelInspector => "PixelInspector"; // PixelInspectorModule
				public static string ViewportInteraction => "ViewportInteraction";
				public static string VREditor => "VREditor";
				public static string FoliageEdit => "FoliageEdit";
				public static string MeshPaint => "MeshPaint";
				public static string MeshPaintMode => "MeshPaintMode";
			}

			internal static class ExternalTool
            {
				public static string AutomationTool => "AutomationTool";
				public static string BuildTool => "BuildTool";
				public static string HeaderTool => "HeaderTool";
			}
        }

		internal static class MSBuildToolsVersion
		{
			public static string VS2012 => "4.0";
			public static string VS2013 => "12.0";
			public static string VS2015 => "14.0";
			public static string VS2017 => "15.0";
			public static string VS2019 => "16.0"; // or 15.0
		}

		internal static class PlatformToolsVersion
        {
			public static string VS2012 => "v110";
			public static string VS2013 => "v120";
			public static string VS2015 => "v140";
			public static string VS2017 => "v141";
			public static string VS2019 => "v142";
        }

		// EngineCode & Generated Folder
		internal static class OutputFile
        {
			public static string PCH => "PCH";
			public static string SharedPCH => "SharedPCH";
			public static string Definitions => "Definitions";
			public static string LiveCodingManifest => "LiveCodingUnityManifest";
			public static string BuildToolExport => "BuildToolExport";
			public static string XGETaskXML => "XGETask.xml";
			public static string DebugEngineSolution => "DebugEngineSolution.xml";
		}

		internal static class PlaceHolder
		{
			public static string Parent => "..";
			public static string WildCard => "*";
		}

		internal static class Platform
        {
			public static string Win32 => "Win32";
			public static string Win64 => "Win64";
			public static string HoloLens => "HoloLens";
			public static string Android => "Android";
			public static string XboxOne => "XboxOne";
			public static string Linux => "Linux";

			public static string IOS => "IOS";
			public static string Mac => "Mac";
			public static string PS4 => "PS4";
			public static string PS5 => "PS5";
			public static string HTML5 => "HTML5";
			public static string LinuxAArch64 => "LinuxAArch64";
			public static string AllDesktop => "AllDesktop";

			public static string TVOS => "TVOS";
			public static string Switch => "Switch";
			public static string Lumin => "Lumin";
		}

		internal static class PlatformGroup
        {
			public static string Microsoft => "Microsoft";
			public static string Windows => "Windows";
			public static string HoloLens => "HoloLens";
			public static string Apple => "Apple";
			public static string IOS => "IOS";
			public static string Unix => "Unix";
			public static string Linux => "Linux";
			public static string Android => "Android";
			public static string Sony => "Sony";
			public static string XboxCommon => "XboxCommon";
			public static string AllDesktop => "AllDesktop";
			public static string Desktop => "Desktop";
		}

		internal static class Project
        {
			public static string StubProjectConfiguration => "BuiltWithBuildTool";
			public static string StubProjectPlatformName => "Win64";

			public static string ShaderCompileWorker => "ShaderCompileWorker";
			public static string LightMass => "LightMass";
        }

		internal static class RecieptProperty
        {
			internal static class Name
			{
				public static string CompileAsDll => "CompileAsDll";
				public static string SDK => "SDK";
			}
			internal static class Value
            {
				public static string True => "true";
            }
        }

		// System Registry Address
		internal static class Registry
        {
			public static string HKeyCurrentUser => "HKEY_CURRENT_USER\\";
			public static string HKeyLocalMachine => "HKEY_LOCAL_MACHINE\\";
			public static string Software => "SOFTWARE\\";
			public static string Wow6432Node => "Wow6432Node\\";
			public static string XoreaxIncrediBuildBuilder => "Xoreax\\Incredibuild\\Builder";

			internal static class Key
			{
				public static string Folder => "Folder";
			}
		}

		// Regular Expression
		internal static class Regex
        {
			public static string CompiledPlatformHeader => @"pattern1=^COMPILED_PLATFORM_HEADER\(\s*([^ ,]+)\)";
			public static string CompiledPlatformHeaderWithPrefix => @"pattern2=^COMPILED_PLATFORM_HEADER_WITH_PREFIX\(\s*([^ ,]+)\s*,\s*([^ ,]+)\)";
			public static string PlatformHeaderName => @"pattern3=^[A-Z]{5}_PLATFORM_HEADER_NAME\(\s*([^ ,]+)\)";
			public static string PlatformHeaderNameWithPrefix => @"pattern4=^[A-Z]{5}_PLATFORM_HEADER_NAME_WITH_PREFIX\(\s*([^ ,]+)\s*,\s*([^ ,]+)\)";
		}

		internal static class Scope
        {
			public static string Engine => "Engine";
			public static string Enterprise => "Enterprise";
			public static string Project => "Project";
			public static string Program => "Program";
			public static string Plugins => "Plugins";
			public static string Plugin => "Plugin";
        }

		internal static class Serialization // Empty
        {
        }

		internal static class ReservedStringID
		{
			public static string LogTraceListener => "LogTraceListener";
			public static string BuildToolMutex => "BuildTool_Mutex";
			public static string BuildToolMutexXMLConfig => "BuildTool_Mutex_XmlConfig";
			public static string CompilerVersion => "CompilerVersion";
			public static string ProviderOption => "v4.0";
			public static string BuildToolWriteMetaData => "BuildTool_WriteMetadata";
		}

		internal static class XML
        {
			internal static class Element
			{
				public static string BuildSet => "BuildSet";
				public static string Project => "Project";
				public static string Environments => "Environments";
				public static string Environment => "Environment";
				public static string Tools => "Tools";
				public static string Tool => "Tool";
				public static string Variables => "Variables";
				public static string Variable => "Variable";
				public static string Task => "Task";

				public static string ProjectGuid => "ProjectGuid";
				public static string ProjectReference => "ProjectReference";

				public static string PropertyGroup => "PropertyGroup";

				public static string ItemGroup => "ItemGroup";
				public static string Name => "Name";
				public static string Private => "Private";
			}

			internal static class Attribute
            {
				public static string ToolsVersion => "ToolsVersion";
				public static string FormatVersion => "FormatVersion";
				public static string Name => "Name";
				public static string Value => "Value";
				public static string AllowRemote => "AllowRemote";
				public static string AllowIntercept => "AllowIntercept";
				public static string OutputPrefix => "OutputPrefix";
				public static string GroupPrefix => "GroupPrefix";
				public static string Params => "Params";
				public static string Path => "Path";
				public static string SkipIfProjectFailed => "SkipIfProjectFailed";
				public static string AutoReserveMemory => "AutoReserveMemory";
				public static string OutputFileMasks => "OutputFileMasks";
				public static string AutoRecover => "AutoRecover";
				public static string Env => "Env";
				public static string SourceFile => "SourceFile";
				public static string Caption => "Caption";
				public static string Tool => "Tool";
				public static string WorkingDir => "WorkingDir";
				public static string AllowRestartOnLocal => "AllowRestartOnLocal";
				public static string DependsOn => "DependsOn";

				public static string Include => "Include";

				public static string Condition => "Condition";
				public static string DefaultTargets => "DefaultTargets";
			}
        }
	}

    internal static class BuildTool
	{
		public static DateTime StartTimeUtc = DateTime.UtcNow; // This can be used as the timestamp for build makefiles,
															   // to determine a base time after which any modifications should invalidate it
		public static IDictionary InitialEnvironment;          // The environment at boot time

		private static bool? bIsEngineInstalled;     // Whether we're running with engine installed
		private static bool? bIsEnterpriseInstalled; // Whether we're running with enterprise installed
		private static bool? bIsProjectInstalled;    // Whether we're running with an installed project

		private static FileReference      InstalledProjectTXTFile;           // If we are running with an installed project, specifies the path to it
		private static DirectoryReference CachedEngineProgramSavedDirectory; // Directory for saved application settings (typically Engine/Programs)

		public static readonly FileReference BuildToolAssemblyPath = FileReference.FindCorrectCase(new FileReference(Assembly.GetExecutingAssembly().GetOriginalLocation()));

		public static readonly DirectoryReference RootDirectory = DirectoryReference.Combine(BuildToolAssemblyPath.Directory, Tag.PlaceHolder.Parent, Tag.PlaceHolder.Parent, Tag.PlaceHolder.Parent); // \

		public static readonly DirectoryReference EngineDirectory                   = DirectoryReference.Combine(RootDirectory, Tag.Directory.Engine);       // \Engine
		public static readonly DirectoryReference EngineSourceDirectory             = DirectoryReference.Combine(EngineDirectory, Tag.Directory.SourceCode); // \Engine\Source
		public static readonly DirectoryReference EnginePlatformExtensionsDirectory = DirectoryReference.Combine(EngineDirectory, Tag.Directory.Platforms);  // \Engine\Platforms

		public static readonly DirectoryReference EngineSourceRuntimeDirectory    = DirectoryReference.Combine(EngineSourceDirectory, Tag.Directory.EngineCode);      // \Engine\Source\Runtime
		public static readonly DirectoryReference EngineSourceDeveloperDirectory  = DirectoryReference.Combine(EngineSourceDirectory, Tag.Directory.EngineAndEditor); // \Engine\Source\Developer
		public static readonly DirectoryReference EngineSourceEditorDirectory     = DirectoryReference.Combine(EngineSourceDirectory, Tag.Directory.EditorOnly);      // \Engine\Source\Editor
		public static readonly DirectoryReference EngineSourceProgramsDirectory   = DirectoryReference.Combine(EngineSourceDirectory, Tag.Directory.ExternalTools);   // \Engine\Source\Programs
		public static readonly DirectoryReference EngineSourceThirdPartyDirectory = DirectoryReference.Combine(EngineSourceDirectory, Tag.Directory.ThirdParty);      // \Engine\Source\ThirdParty

		public static readonly DirectoryReference EnterpriseDirectory             = DirectoryReference.Combine(RootDirectory,       Tag.Directory.Enterprise); // \Enterprise
		public static readonly DirectoryReference EnterpriseSourceDirectory       = DirectoryReference.Combine(EnterpriseDirectory, Tag.Directory.SourceCode); // \Enterprise\Source
		public static readonly DirectoryReference EnterprisePluginsDirectory      = DirectoryReference.Combine(EnterpriseDirectory, Tag.Directory.Plugins);    // \Enterprise\Plugins
		public static readonly DirectoryReference EnterpriseIntermediateDirectory = DirectoryReference.Combine(EnterpriseDirectory, Tag.Directory.Generated);  // \Enterprise\Intermediate

		// The engine programs directory
		public static DirectoryReference EngineProgramSavedDirectory
		{
			get
			{
				if (CachedEngineProgramSavedDirectory == null)
				{
					if (IsEngineInstalled())
					{
						CachedEngineProgramSavedDirectory = Utils.GetUserSettingDirectory()?? DirectoryReference.Combine(EngineDirectory, Tag.Directory.ExternalTools);
					}
					else
					{
						CachedEngineProgramSavedDirectory = DirectoryReference.Combine(EngineDirectory, Tag.Directory.ExternalTools);
					}
				}
				return CachedEngineProgramSavedDirectory;
			}
		}

		public static string GetPlatformGeneratedFolder(BuildTargetPlatform Platform, string Architecture)
		{
			// now that we have the platform, we can set the intermediate path to include the platform/architecture name
			return Path.Combine(Tag.Directory.Generated, Tag.Directory.Build, Platform.ToString(), BuildPlatform.GetBuildPlatform(Platform).GetFolderNameForArchitecture(Architecture));
		}

		// The Remote Ini directory.  This should always be valid when compiling using a remote server.
		static string RemoteIniPath = null;

#region STATIC_FUNCTIONS

		public static DirectoryReference AppendSuffixPlatforms(DirectoryReference ProjectDirectory)
		{
			return DirectoryReference.Combine(ProjectDirectory, Tag.Directory.Platforms);
		}

		public static DirectoryReference ProjectPlatformExtensionsDirectory(FileReference ProjectFile)
		{
			return AppendSuffixPlatforms(ProjectFile.Directory);
		}

		// The main engine directory and all found platform extension engine directories
		public static DirectoryReference[] GetAllEngineDirectories(string Suffix="")
		{
			List<DirectoryReference> EngineDirectories = new List<DirectoryReference>() { DirectoryReference.Combine(EngineDirectory, Suffix) };

			// EnginePlatformExtensionsDirectory = {D:\UERelease\Engine\Platforms}
			// -> EngineDirectories는 D:\UERelease\Engine, D:\UERelease\Engine\Platforms\XXX
			if (DirectoryReference.Exists(EnginePlatformExtensionsDirectory))
			{
				foreach (DirectoryReference PlatformDirectory in DirectoryReference.EnumerateDirectories(EnginePlatformExtensionsDirectory))
				{
					DirectoryReference PlatformEngineDirectory = DirectoryReference.Combine(PlatformDirectory, Suffix);
					if (DirectoryReference.Exists(PlatformEngineDirectory))
					{
						EngineDirectories.Add(PlatformEngineDirectory);
					}
				}
			}

			return EngineDirectories.ToArray();
		}

		// Returns the main project directory and all found platform extension project directories, with
		// an optional subdirectory to look for within each location (ie, "Config" or "Source/Runtime")
		public static DirectoryReference[] GetAllProjectDirectories(DirectoryReference ProjectDirectory, string Suffix = "")
		{
			List<DirectoryReference> ProjectDirectories = new List<DirectoryReference>() { DirectoryReference.Combine(ProjectDirectory, Suffix) };

			if (DirectoryReference.Exists(AppendSuffixPlatforms(ProjectDirectory)))
			{
				foreach (DirectoryReference PlatformDirectory in DirectoryReference.EnumerateDirectories
					(AppendSuffixPlatforms(ProjectDirectory), Tag.PlaceHolder.WildCard, SearchOption.TopDirectoryOnly))
				{
					DirectoryReference PlatformEngineDirectory = DirectoryReference.Combine(PlatformDirectory, Suffix);

					if (DirectoryReference.Exists(PlatformEngineDirectory))
					{
						ProjectDirectories.Add(PlatformEngineDirectory);
					}
				}
			}

			return ProjectDirectories.ToArray();
		}

		public static DirectoryReference[] GetAllProjectDirectories(FileReference ProjectFile, string Suffix = "")
		{
			return GetAllProjectDirectories(ProjectFile.Directory, Suffix);
		}

		// Returns true if BuildTool is running using installed Engine components
		static public bool IsEngineInstalled()
		{
			if (!bIsEngineInstalled.HasValue)
			{
				bIsEngineInstalled = FileReference.Exists(FileReference.Combine(EngineDirectory, Tag.Directory.Build, Tag.TxtFileName.CachedEngineInstalled));
			}
			return bIsEngineInstalled.Value;
		}

		// Returns true if BuildTool is running using installed Enterprise components
		static public bool IsEnterpriseInstalled()
		{
			if (!bIsEnterpriseInstalled.HasValue)
			{
				bIsEnterpriseInstalled = FileReference.Exists(FileReference.Combine(EnterpriseDirectory, Tag.Directory.Build, Tag.TxtFileName.CachedEngineInstalled));
			}
			return bIsEnterpriseInstalled.Value;
		}

		// Returns true if BuildTool is running using an installed project (ie. a mod kit)
		static public bool IsProjectInstalled()
		{
			if (!bIsProjectInstalled.HasValue)
			{
				FileReference InstalledProjectLocationFile = FileReference.Combine(BuildTool.RootDirectory, Tag.Directory.Engine, Tag.Directory.Build, Tag.TxtFileName.CachedEngineInstalled);
				if (FileReference.Exists(InstalledProjectLocationFile))
				{
					InstalledProjectTXTFile = FileReference.Combine(BuildTool.RootDirectory, File.ReadAllText(InstalledProjectLocationFile.FullName).Trim());
					bIsProjectInstalled = true;
				}
				else
				{
					InstalledProjectTXTFile = null;
					bIsProjectInstalled = false;
				}
			}
			return bIsProjectInstalled.Value;
		}

		// Gets the installed project file
		static public FileReference GetInstalledProjectFile()
		{
			if(IsProjectInstalled())
			{
				return InstalledProjectTXTFile;
			}
			else
			{
				return null;
			}
		}

		// Checks whether the given file is under an installed directory, and should not be overridden
		static public bool IsFileInstalled(FileReference File)
		{
			if(IsEngineInstalled() && File.IsUnderDirectory(EngineDirectory))
			{
				return true;
			}
			if(IsEnterpriseInstalled() && File.IsUnderDirectory(EnterpriseDirectory))
			{
				return true;
			}
			if(IsProjectInstalled() && File.IsUnderDirectory(InstalledProjectTXTFile.Directory))
			{
				return true;
			}
			return false;
		}

		// Gets the absolute path to the BuildTool assembly.
		static public FileReference GetBuildToolAssemblyPath()
		{
			return BuildToolAssemblyPath;
		}

		// The Remote tool ini directory.
		// This should be valid if compiling using a remote server
		static public string GetRemoteIniPath()
		{
			return RemoteIniPath;
		}

		static public void SetRemoteIniPath(string Path)
		{
			RemoteIniPath = Path;
		}

#endregion STATIC_FUNCTIONS

		// Global options for UBT (any modes)
		class GlobalOptions
		{
			// The amount of detail to write to the log
			[CommandLine(ReservedCommand = "-Verbose",     Value ="Verbose")]
			[CommandLine(ReservedCommand = "-VeryVerbose", Value ="VeryVerbose")]
			public LogEventType LogOutputLevel = LogEventType.Log;

			// Specifies the path to a log file to write.
			// Note that the default mode (eg. building, generating project files) will create a log file by default if this not specified.
			[CommandLine(ReservedCommand = "-Log")]
			public FileReference LogFileName = null;

			// Whether to include timestamps in the log
			[CommandLine(ReservedCommand = "-Timestamps")]
			public bool bLogTimestamps = false;

			// Whether to format messages in MsBuild format
			[CommandLine(ReservedCommand = "-FromMsBuild")]
			public bool bLogFromMsBuild = false;

			// Whether to write progress markup in a format that can be parsed by other programs
			[CommandLine(ReservedCommand = "-Progress")]
			public bool bWriteProgressMarkup = false;

			// Whether to ignore the mutex
			[CommandLine(ReservedCommand = "-NoMutex")]
			public bool bNoMutex = false;

			// Whether to wait for the mutex rather than aborting immediately
			[CommandLine(ReservedCommand = "-WaitMutex")]
			public bool bWaitMutex = false;

			// Whether to wait for the mutex rather than aborting immediately
			[CommandLine(ReservedCommand = "-RemoteIni")]
			public string RemoteIni = "";

			// The mode to execute
			[CommandLine]
			[CommandLine(ReservedCommand = "-Clean",              Value="Clean")]
			[CommandLine(ReservedCommand = "-ProjectFiles",       Value="GenerateProjectFiles")]
			[CommandLine(ReservedCommand = "-ProjectFileFormat=", Value="GenerateProjectFiles")]
			[CommandLine(ReservedCommand = "-Makefile",           Value="GenerateProjectFiles")]
			[CommandLine(ReservedCommand = "-CMakefile",          Value="GenerateProjectFiles")]
			[CommandLine(ReservedCommand = "-QMakefile",          Value="GenerateProjectFiles")]
			[CommandLine(ReservedCommand = "-KDevelopfile",       Value="GenerateProjectFiles")]
			[CommandLine(ReservedCommand = "-CodeliteFiles",      Value="GenerateProjectFiles")]
			[CommandLine(ReservedCommand = "-XCodeProjectFiles",  Value="GenerateProjectFiles")]
			[CommandLine(ReservedCommand = "-EdditProjectFiles",  Value="GenerateProjectFiles")]
			[CommandLine(ReservedCommand = "-VSCode",             Value="GenerateProjectFiles")]
			[CommandLine(ReservedCommand = "-VSMac",              Value="GenerateProjectFiles")]
			[CommandLine(ReservedCommand = "-CLion",              Value="GenerateProjectFiles")]
			public string Mode = null;
			
			// Initialize the options with the given commnad line arguments
			public GlobalOptions(CommandLineArguments Arguments)
			{
				Arguments.ApplyTo(this);

				if (!string.IsNullOrEmpty(RemoteIni))
				{
					BuildTool.SetRemoteIniPath(RemoteIni);
				}
			}
		} // End class GlobalOptions

		// Only Install SourceCode
		// GenerateProjectFiles Mode Arguments -> (in Batch Command) -ProjectFiles %*(but no Argument) => -ProjectFiles

		// In IncludeTool Mode Argument ->
		// "-Mode=JsonExport {0} {1} {2}{3} -disableunity -xgeexport -nobuilduht -nopch -nodebuginfo -execcodegenactions -outputfile=\"{4}\"", Target, Configuration, Platform, Precompile? " -precompile" : "", TaskListFile.ChangeExtension(".json").FullName), WorkingDir, Log
		// and
		// "{0} {1} {2}{3} -disableunity -xgeexport -nobuilduht -nopch -nodebuginfo", Target, Configuration, Platform, Precompile? " -precompile" : ""
		
		private static int Main(string[] ArgumentsArray) // Main Entry point
		{
			SystemWideSingletonMutex Mutex = null;

			try
			{
				// Parse the command line arguments
				CommandLineArguments Arguments = new CommandLineArguments(ArgumentsArray);

				// Parse the global options
				GlobalOptions Options = new GlobalOptions(Arguments);

				// Configure the log system
				Log.OutputLevel                          = Options.LogOutputLevel;
				Log.IncludeTimestamps                    = Options.bLogTimestamps;
				Log.IncludeProgramNameWithSeverityPrefix = Options.bLogFromMsBuild;
				
				// Configure the progress writer
				ProgressWriter.bWriteMarkup = Options.bWriteProgressMarkup;

				// Add the log writer if requested. When building a target, we'll create the writer for the default log file later.
				if(Options.LogFileName != null)
				{
					Log.AddFileWriter(Tag.ReservedStringID.LogTraceListener, Options.LogFileName);
				}

				// Ensure we can resolve any external assemblies that are not in the same folder as our assembly.
				// For Testing.
				AssemblyUtils.InstallAssemblyResolver(Path.GetDirectoryName(Assembly.GetEntryAssembly().GetOriginalLocation()));

				// Change the working directory to be the Engine/Source folder. We are likely running from Engine/Binaries/DotNET
				// This is critical to be done early so any code that relies on the current directory being Engine/Source will work.
				DirectoryReference.SetCurrentDirectory(BuildTool.EngineSourceDirectory);

				// Get the type of the mode to execute,
				// using a fast-path for the build mode.
				Type ModeType = typeof(BuildMode);

				if(Options.Mode != null)
				{
					// Find all the valid modes
					Dictionary<string, Type> ModeNameToType = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

					foreach(Type Type in Assembly.GetExecutingAssembly().GetTypes())
					{
						if(Type.IsClass && !Type.IsAbstract && Type.IsSubclassOf(typeof(ToolMode)))
						{
							ToolModeAttribute Attribute = Type.GetCustomAttribute<ToolModeAttribute>();
							if(Attribute == null)
							{
								throw new BuildException("Class '{0}' should have a ToolModeAttribute", Type.Name);
							}
							ModeNameToType.Add(Attribute.ToolModeName, Type);
						}
					}

					// Try to get the correct mode
					if(!ModeNameToType.TryGetValue(Options.Mode, out ModeType))
					{
						Log.TraceError("No mode named '{0}'. Available modes are:\n  {1}", Options.Mode, String.Join("\n  ", ModeNameToType.Keys));
						return 1;
					}
				}

				// Get the options for which systems have to be initialized for this mode
				ToolModeOptions ModeOptions = ModeType.GetCustomAttribute<ToolModeAttribute>().Options;

				// Start prefetching the contents of the engine folder
				if((ModeOptions & ToolModeOptions.StartPrefetchingEngine) != 0)
				{
					FileMetadataPrefetch.QueueEngineDirectory();
				}

				// Read the XML configuration files
				if((ModeOptions & ToolModeOptions.XmlConfig) != 0)
				{
                    string XmlConfigMutexName = SystemWideSingletonMutex.GetUniqueMutexForPath(Tag.ReservedStringID.BuildToolMutexXMLConfig, Assembly.GetExecutingAssembly().CodeBase);
                    using (SystemWideSingletonMutex XmlConfigMutex = new SystemWideSingletonMutex(XmlConfigMutexName, true))
                    {
                        FileReference XmlConfigCache = Arguments.GetFileReferenceOrDefault("-XmlConfigCache=", null);
                        XMLConfig.ReadConfigFiles(XmlConfigCache);
                    }

                }

				// Acquire a lock for this branch
				if((ModeOptions & ToolModeOptions.SingleInstance) != 0 && !Options.bNoMutex)
				{
                    string MutexName = SystemWideSingletonMutex.GetUniqueMutexForPath(Tag.ReservedStringID.BuildToolMutex, Assembly.GetExecutingAssembly().CodeBase);
                    Mutex = new SystemWideSingletonMutex(MutexName, Options.bWaitMutex);
                }

                // Register all the build platforms (See console window and Output Window)
                if ((ModeOptions & ToolModeOptions.BuildPlatforms) != 0)
				{
					BuildPlatform.RegisterPlatforms(false, false);
					
				}
				if ((ModeOptions & ToolModeOptions.BuildPlatformsHostOnly) != 0)
				{
					BuildPlatform.RegisterPlatforms(false, true);
					
				}
				if ((ModeOptions & ToolModeOptions.BuildPlatformsForValidation) != 0)
				{
					BuildPlatform.RegisterPlatforms(true, false);
				}

				// Create the appropriate handler
				ToolMode Mode = (ToolMode)Activator.CreateInstance(ModeType);

				int Result = Mode.Execute(Arguments);

				return Result;
			}
			catch (CompilationResultException Ex)
			{
				// Used to return a propagate a specific exit code after an error has occurred. Does not log any message.
				Log.TraceLog(ExceptionUtils.FormatExceptionDetails(Ex));
				return (int)Ex.Result;
			}
			catch (BuildException Ex)
			{
				// BuildExceptions should have nicely formatted messages. We can log these directly.
				Log.TraceError(ExceptionUtils.FormatException(Ex));
				Log.TraceLog(ExceptionUtils.FormatExceptionDetails(Ex));
				return (int)CompilationResult.OtherCompilationError;
			}
			catch (Exception Ex)
			{
				// Unhandled exception. 
				Log.TraceError("Unhandled exception: {0}", ExceptionUtils.FormatException(Ex));
				Log.TraceLog(ExceptionUtils.FormatExceptionDetails(Ex));
				return (int)CompilationResult.OtherCompilationError;
			}
			finally
			{
				// Cancel the prefetcher
				FileMetadataPrefetch.Stop();

				// Make sure we flush the logs however we exit
				Trace.Close();

				// Dispose of the mutex. Must be done last to ensure that another process does not startup and start trying to write to the same log file.
				if(Mutex != null)
				{
					Mutex.Dispose();
				}
			}
		} // End Main() Function
	} // End class BuildTool
} // End namespace BuildTool