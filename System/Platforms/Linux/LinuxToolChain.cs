using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BuildToolUtilities;

namespace BuildTool
{
	// Option flags for the Linux toolchain
	[Flags]
	internal enum LinuxToolChainOptions
	{
		None                             = 0x00, // No custom options
		EnableAddressSanitizer           = 0x01, // Enable address sanitzier
		EnableThreadSanitizer            = 0x02, // Enable thread sanitizer
		EnableUndefinedBehaviorSanitizer = 0x04, // Enable undefined behavior sanitizer
		EnableMemorySanitizer            = 0x08, // Enable memory sanitizer
		EnableThinLTO                    = 0x10, // Enable thin LTO
		EnableSharedSanitizer            = 0x20, // Enable Shared library for the Sanitizers otherwise defaults to Statically linked
	}

	internal sealed class LinuxToolChain : ISPCToolChain
	{
		// Flavor of the current build (target triplet)
		private readonly string Architecture;

		// Cache to avoid making multiple checks for lld availability/usability
		private readonly bool bUseLld = false;

		// Whether the compiler is set up to produce PIE executables by default
		private bool bSuppressPIE = false;

		// Whether or not to preserve the portable symbol file produced by dump_syms
		private readonly bool bPreservePSYM = false;

		// Pass --gdb-index option to linker to generate .gdb_index section.
		private readonly bool bGdbIndexSection = true;

		// Allows you to override the maximum binary size allowed to be passed to objcopy.exe when cross building on Windows.
		// Max value is 2GB, due to bat file limitation
		private readonly ulong MaxBinarySizeOverrideForObjcopy = 0;

		// Platform SDK to use
		private readonly LinuxPlatformSDK PlatformSDK;

		// Toolchain information to print during the build.
		private readonly string ToolchainInfo;

		// Whether to compile with ASan enabled
		private readonly LinuxToolChainOptions Options;

		// cache the location of NDK tools
		private readonly bool bIsCrossCompiling;
		private readonly string BaseLinuxPath;
		private readonly string ClangPath;
		private readonly string GCCPath;
		private readonly string ArPath;
		private readonly string LlvmArPath;
		private readonly string RanlibPath;
		private readonly string StripPath;
		private readonly string ObjcopyPath;
		private readonly string DumpSymsPath;
		private readonly string BreakpadEncoderPath;
		private readonly string MultiArchRoot;

		// Version string of the current compiler, whether clang or gcc or whatever
		private static string CompilerVersionString;
		private static int CompilerVersionMajor = -1;
		private static int CompilerVersionMinor = -1;
		private static int CompilerVersionPatch = -1;

		// Whether to use old, slower way to relink circularly dependent libraries.
		// It makes sense to use it when cross-compiling on Windows due to race conditions between actions reading and modifying the libs.
		private readonly bool bUseFixdeps = false;

		// Track which scripts need to be deleted before appending to
		private bool bHasWipedFixDepsScript = false;

		// Holds all the binaries for a particular target (except maybe the executable itself).
		private static readonly List<FileItem> AllBinaries = new List<FileItem>();

		// Tracks that information about used C++ library is only printed once
		private bool bHasPrintedBuildDetails = false;

		public LinuxToolChain(string InArchitecture, LinuxPlatformSDK InSDK, bool InPreservePSYM = false, LinuxToolChainOptions InOptions = LinuxToolChainOptions.None)
			: this(BuildTargetPlatform.Linux, InArchitecture, InSDK, InPreservePSYM, InOptions)
		{
			MultiArchRoot = PlatformSDK.GetSDKLocation();
			BaseLinuxPath = PlatformSDK.GetBaseLinuxPathForArchitecture(InArchitecture);

			bool bForceUseSystemCompiler = PlatformSDK.ForceUseSystemCompiler();
			bool bHasValidCompiler;

			// these are supplied by the engine and do not change depending on the circumstances
			DumpSymsPath = Path.Combine(BuildTool.EngineDirectory.FullName, "Binaries", "Linux", "dump_syms");
			BreakpadEncoderPath = Path.Combine(BuildTool.EngineDirectory.FullName, "Binaries", "Linux", "BreakpadSymbolEncoder");

			if (bForceUseSystemCompiler)
			{
				// Validate the system toolchain.
				BaseLinuxPath = "";
				MultiArchRoot = "";

				ToolchainInfo = "system toolchain";

				// use native linux toolchain
				ClangPath   = LinuxCommon.WhichClang();
				GCCPath     = LinuxCommon.WhichGcc();
				ArPath      = LinuxCommon.Which("ar");
				LlvmArPath  = LinuxCommon.Which("llvm-ar");
				RanlibPath  = LinuxCommon.Which("ranlib");
				StripPath   = LinuxCommon.Which("strip");
				ObjcopyPath = LinuxCommon.Which("objcopy");

				// if clang is available, zero out gcc (@todo: support runtime switching?)
				if (!String.IsNullOrEmpty(ClangPath))
				{
					GCCPath = null;
				}

				// When compiling on Linux, use a faster way to relink circularly dependent libraries.
				// Race condition between actions linking to the .so and action overwriting it is avoided thanks to inodes
				bUseFixdeps = false;

				bIsCrossCompiling = false;

				bHasValidCompiler = DetermineCompilerVersion();
			}
			else
			{
				if (String.IsNullOrEmpty(BaseLinuxPath))
				{
					throw new BuildException("LINUX_MULTIARCH_ROOT environment variable is not set; cannot instantiate Linux toolchain");
				}
				if (String.IsNullOrEmpty(MultiArchRoot)) 
				{
					MultiArchRoot = BaseLinuxPath;
					Log.TraceInformation("Using LINUX_ROOT (deprecated, consider LINUX_MULTIARCH_ROOT)");
				}

				BaseLinuxPath = BaseLinuxPath.Replace("\"", "").Replace('\\', '/');
				ToolchainInfo = String.Format("toolchain located at '{0}'", BaseLinuxPath);

				// set up the path to our toolchain
				GCCPath = "";
				ClangPath = Path.Combine(BaseLinuxPath, @"bin", "clang++" + GetHostPlatformBinarySuffix());
				ArPath = Path.Combine(Path.Combine(BaseLinuxPath, String.Format("bin/{0}-{1}", Architecture, "ar" + GetHostPlatformBinarySuffix())));
				LlvmArPath = Path.Combine(Path.Combine(BaseLinuxPath, String.Format("bin/{0}", "llvm-ar" + GetHostPlatformBinarySuffix())));
				RanlibPath = Path.Combine(Path.Combine(BaseLinuxPath, String.Format("bin/{0}-{1}", Architecture, "ranlib" + GetHostPlatformBinarySuffix())));
				StripPath = Path.Combine(Path.Combine(BaseLinuxPath, String.Format("bin/{0}-{1}", Architecture, "strip" + GetHostPlatformBinarySuffix())));
				ObjcopyPath = Path.Combine(Path.Combine(BaseLinuxPath, String.Format("bin/{0}-{1}", Architecture, "objcopy" + GetHostPlatformBinarySuffix())));

				// When cross-compiling on Windows, use old FixDeps. It is slow, but it does not have timing issues
				bUseFixdeps = (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Win64 || BuildHostPlatform.Current.Platform == BuildTargetPlatform.Win32);

				if (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Linux)
				{
					Environment.SetEnvironmentVariable("LC_ALL", "C");
				}

				bIsCrossCompiling = true;

				bHasValidCompiler = DetermineCompilerVersion();
			}

			if (!bHasValidCompiler)
			{
				throw new BuildException("Could not determine version of the compiler, not registering Linux toolchain.");
			}

			CheckDefaultCompilerSettings();

			// refuse to use compilers that we know won't work
			// disable that only if you are a dev and you know what you are doing
			if (!UsingClang())
			{
				throw new BuildException("Unable to build: no compatible clang version found. Please run Setup.sh");
			}
			// prevent unknown clangs since the build is likely to fail on too old or too new compilers
			else if ((CompilerVersionMajor * 10 + CompilerVersionMinor) > 90 || (CompilerVersionMajor * 10 + CompilerVersionMinor) < 60)
			{
				throw new BuildException(
					string.Format("This version of the C++ Engine can only be compiled with clang 9.0, 8.0, 7.0 and 6.0. clang {0} may not build it - please use a different version.",
						CompilerVersionString)
					);
			}

			// trust lld only for clang 5.x and above (FIXME: also find if present on the system?)
			// NOTE: with early version you can run into errors like "failed to compute relocation:" and others
			bUseLld = (CompilerVersionMajor >= 5);

			// Add --gdb-index for Clang 9.0 and higher
			bGdbIndexSection = (CompilerVersionMajor >= 9);
		}

		// Simple Default Constructor
#pragma warning disable IDE0060 // Remove unused parameter
		public LinuxToolChain(BuildTargetPlatform Sanitizer, string InArchitecture, LinuxPlatformSDK InSDK, bool InPreservePSYM = false, LinuxToolChainOptions InOptions = LinuxToolChainOptions.None)
#pragma warning restore IDE0060 // Remove unused parameter
#pragma warning restore IDE0079 // Remove unnecessary suppression
			: base()
		{
			Architecture = InArchitecture;
			PlatformSDK = InSDK;
			Options = InOptions;
			bPreservePSYM = InPreservePSYM;
		}

		private string GetHostPlatformBinarySuffix()
		{
			if (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Win64 || BuildHostPlatform.Current.Platform == BuildTargetPlatform.Win32)
			{
				return ".exe";
			}

			return "";
		}

		private bool CrossCompiling() => bIsCrossCompiling;

		private bool UsingClang() => !String.IsNullOrEmpty(ClangPath);

		// Splits compiler version string into numerical components, leaving unchanged if not known		
		private void DetermineCompilerMajMinPatchFromVersionString()
		{
			string[] Parts = CompilerVersionString.Split('.');
			if (1 <= Parts.Length)
			{
				CompilerVersionMajor = Convert.ToInt32(Parts[0]);
			}
			if (2 <= Parts.Length)
			{
				CompilerVersionMinor = Convert.ToInt32(Parts[1]);
			}
			if (3 <= Parts.Length)
			{
				CompilerVersionPatch = Convert.ToInt32(Parts[2]);
			}
		}

		internal string GetDumpEncodeDebugCommand(LinkEnvironment LinkEnvironment, FileItem OutputFile)
		{
			bool bUseCmdExe = BuildHostPlatform.Current.Platform == BuildTargetPlatform.Win64 || BuildHostPlatform.Current.Platform == BuildTargetPlatform.Win32;
			
			string EncodeDebugCommand = "";

			{
				string DumpCommand = bUseCmdExe ? "\"{0}\" \"{1}\" \"{2}\" 2>NUL\n" : "\"{0}\" -c -o \"{2}\" \"{1}\"\n";

				FileItem EncodedBinarySymbolsFile = FileItem.GetItemByPath(Path.Combine(LinkEnvironment.OutputDirectory.FullName, OutputFile.FileDirectory.GetFileNameWithoutExtension() + ".sym"));

				FileItem SymbolsFile = bPreservePSYM ?
					 FileItem.GetItemByPath(Path.Combine(LinkEnvironment.OutputDirectory.FullName, OutputFile.FileDirectory.GetFileNameWithoutExtension() + ".psym")) :
					 FileItem.GetItemByPath(Path.Combine(LinkEnvironment.LocalShadowDirectory.FullName, OutputFile.FileDirectory.GetFileName() + ".psym"));

				// dump_syms
				EncodeDebugCommand += string.Format
				(
					DumpCommand,
					DumpSymsPath,
					OutputFile.AbsolutePath,
					SymbolsFile.AbsolutePath
				);

				// encode breakpad symbols
				EncodeDebugCommand += string.Format
				(
					"\"{0}\" \"{1}\" \"{2}\"\n",
					BreakpadEncoderPath,
					SymbolsFile.AbsolutePath,
					EncodedBinarySymbolsFile.AbsolutePath
				);
			}

			if (LinkEnvironment.bCreateDebugInfo)
			{
				FileItem DebugFile    = FileItem.GetItemByPath(Path.Combine(LinkEnvironment.OutputDirectory.FullName, OutputFile.FileDirectory.GetFileNameWithoutExtension() + ".debug"));
				FileItem StrippedFile = FileItem.GetItemByPath(Path.Combine(LinkEnvironment.LocalShadowDirectory.FullName, OutputFile.FileDirectory.GetFileName() + "_nodebug"));

				if (0 < MaxBinarySizeOverrideForObjcopy && bUseCmdExe)
				{
					// Bad hack where objcopy.exe cannot handle files larger then 2GB. Its fine when building on Linux
					EncodeDebugCommand += string.Format
					(
						"for /F \"tokens=*\" %%F in (\"{0}\") DO set size=%%~zF\n",
						OutputFile.AbsolutePath
					);

					EncodeDebugCommand += string.Format("if %size% LSS {0} (\n", MaxBinarySizeOverrideForObjcopy);
				}

				// objcopy stripped file
				EncodeDebugCommand += string.Format
				(
					"\"{0}\" --strip-all \"{1}\" \"{2}\"\n",
					ObjcopyPath,
					OutputFile.AbsolutePath,
					StrippedFile.AbsolutePath
				);

				// objcopy debug file
				EncodeDebugCommand += string.Format
				(
					"\"{0}\" --only-keep-debug \"{1}\" \"{2}\"\n",
					ObjcopyPath,
					OutputFile.AbsolutePath,
					DebugFile.AbsolutePath
				);

				// objcopy link debug file to final so
				EncodeDebugCommand += string.Format
				(
					"\"{0}\" --add-gnu-debuglink=\"{1}\" \"{2}\" \"{3}.temp\"\n",
					ObjcopyPath,
					DebugFile.AbsolutePath,
					StrippedFile.AbsolutePath,
					OutputFile.AbsolutePath
				);

				if (bUseCmdExe)
				{
					// Only move the temp final elf file once its done being linked by objcopy
					EncodeDebugCommand += string.Format
					(
						"move /Y \"{0}.temp\" \"{1}\"\n",
						OutputFile.AbsolutePath,
						OutputFile.AbsolutePath
					);

					if (MaxBinarySizeOverrideForObjcopy > 0)
					{
						// If we have an override size, then we need to create a dummy file if that size is exceeded
						EncodeDebugCommand += string.Format(") ELSE (\necho DummyDebug >> \"{0}\"\n)\n", DebugFile.AbsolutePath);
					}
				}
				else
				{
					// Only move the temp final elf file once its done being linked by objcopy
					EncodeDebugCommand += string.Format
					(
						"mv \"{0}.temp\" \"{1}\"\n",
						OutputFile.AbsolutePath,
						OutputFile.AbsolutePath
					);

					// Change the debug file to normal permissions. It was taking on the +x rights from the output file
					EncodeDebugCommand += string.Format("chmod 644 \"{0}\"\n", DebugFile.AbsolutePath);
				}
			}

			return EncodeDebugCommand;
		}

		private bool DetermineCompilerVersion()
		{
			CompilerVersionString = null;
			CompilerVersionMajor  = -1;
			CompilerVersionMinor  = -1;
			CompilerVersionPatch  = -1;

			using (Process Proc = new Process())
			{
				Proc.StartInfo.UseShellExecute        = false;
				Proc.StartInfo.CreateNoWindow         = true;
				Proc.StartInfo.RedirectStandardOutput = true;
				Proc.StartInfo.RedirectStandardError  = true;

				if (!String.IsNullOrEmpty(GCCPath))
				{
					Proc.StartInfo.FileName  = GCCPath;
					Proc.StartInfo.Arguments = " -dumpversion";

					Proc.Start();
					Proc.WaitForExit();

					if (Proc.ExitCode == 0)
					{
						// read just the first string
						CompilerVersionString = Proc.StandardOutput.ReadLine();
						DetermineCompilerMajMinPatchFromVersionString();
					}
				}
				else if (!String.IsNullOrEmpty(ClangPath))
				{
					Proc.StartInfo.FileName = ClangPath;
					Proc.StartInfo.Arguments = " --version";

					Proc.Start();
					Proc.WaitForExit();

					if (Proc.ExitCode == 0)
					{
						// read just the first string
						string VersionString = Proc.StandardOutput.ReadLine();

						Regex VersionPattern = new Regex("version \\d+(\\.\\d+)+");
						Match VersionMatch = VersionPattern.Match(VersionString);

						// version match will be like "version 3.3", so remove the "version"
						if (VersionMatch.Value.StartsWith("version "))
						{
							CompilerVersionString = VersionMatch.Value.Replace("version ", "");

							DetermineCompilerMajMinPatchFromVersionString();
						}
					}
				}
				else
				{
					// icl?
				}
			}

			return !String.IsNullOrEmpty(CompilerVersionString);
		}

		// Checks default compiler settings
		private void CheckDefaultCompilerSettings()
		{
			using (Process Proc = new Process())
			{
				Proc.StartInfo.UseShellExecute = false;
				Proc.StartInfo.CreateNoWindow = true;
				Proc.StartInfo.RedirectStandardOutput = true;
				Proc.StartInfo.RedirectStandardError = true;
				Proc.StartInfo.RedirectStandardInput = true;

				if (!String.IsNullOrEmpty(ClangPath) && File.Exists(ClangPath))
				{
					Proc.StartInfo.FileName = ClangPath;
					Proc.StartInfo.Arguments = " -E -dM -";

					Proc.Start();
					Proc.StandardInput.Close();

					for (; ; )
					{
						string CompilerDefine = Proc.StandardOutput.ReadLine();
						if (string.IsNullOrEmpty(CompilerDefine))
						{
							Proc.WaitForExit();
							break;
						}

						if (CompilerDefine.Contains("__PIE__") || CompilerDefine.Contains("__pie__"))
						{
							bSuppressPIE = true;
						}
					}
				}
				else
				{
					// other compilers aren't implemented atm
				}
			}
		}

		// Checks if compiler version matches the requirements
		private static bool CompilerVersionGreaterOrEqual(int Major, int Minor, int Patch)
		{
			return CompilerVersionMajor > Major ||
				(CompilerVersionMajor == Major && CompilerVersionMinor > Minor) ||
				(CompilerVersionMajor == Major && CompilerVersionMinor == Minor && CompilerVersionPatch >= Patch);
		}

		// Architecture-specific compiler switches
		static string ArchitectureSpecificSwitches(string Architecture)
		{
			string Result = "";

			if (Architecture.StartsWith("arm") || Architecture.StartsWith("aarch64"))
			{
				Result += " -fsigned-char";
			}

			return Result;
		}

		private string ArchitectureSpecificDefines(string Architecture)
		{
			string Result = "";

			if (Architecture.StartsWith("x86_64") || Architecture.StartsWith("aarch64"))
			{
				Result += " -D_LINUX64";
			}

			return Result;
		}

		private static bool ShouldUseLibcxx(string Architecture)
		{
			// set LINUX_USE_LIBCXX to either 0 or 1. If unset, defaults to 1.
			string UseLibcxxEnvVarOverride = Environment.GetEnvironmentVariable("LINUX_USE_LIBCXX");
			if (string.IsNullOrEmpty(UseLibcxxEnvVarOverride) || UseLibcxxEnvVarOverride == "1")
			{
				// at the moment ARM32 libc++ remains missing
				return Architecture.StartsWith("x86_64") || Architecture.StartsWith("aarch64") || Architecture.StartsWith("i686");
			}
			return false;
		}

		private string GetCLArguments_Global(CppCompileEnvironment CompileEnvironment)
		{
			string Result = "";

			// build up the commandline common to C and C++
			Result += " -c";
			Result += " -pipe";

			if (ShouldUseLibcxx(CompileEnvironment.Architecture))
			{
				Result += " -nostdinc++";
				Result += " -I" + "ThirdParty/Linux/LibCxx/include/";
				Result += " -I" + "ThirdParty/Linux/LibCxx/include/c++/v1";
			}

			// ASan
			if (Options.HasFlag(LinuxToolChainOptions.EnableAddressSanitizer))
			{
				// Force using the ANSI allocator if ASan is enabled
				Result += " -fsanitize=address -DFORCE_ANSI_ALLOCATOR=1";
			}

			// TSan
			if (Options.HasFlag(LinuxToolChainOptions.EnableThreadSanitizer))
			{
				// Force using the ANSI allocator if TSan is enabled
				Result += " -fsanitize=thread -DFORCE_ANSI_ALLOCATOR=1";
			}

			// UBSan
			if (Options.HasFlag(LinuxToolChainOptions.EnableUndefinedBehaviorSanitizer))
			{
				Result += " -fsanitize=undefined";
			}

			// MSan
			if (Options.HasFlag(LinuxToolChainOptions.EnableMemorySanitizer))
			{
				// Force using the ANSI allocator if MSan is enabled
				// -fsanitize-memory-track-origins adds a 1.5x-2.5x slow down ontop of MSan normal amount of overhead
				// -fsanitize-memory-track-origins=1 is faster but collects only allocation points but not intermediate stores
				Result += " -fsanitize=memory -fsanitize-memory-track-origins -DFORCE_ANSI_ALLOCATOR=1";
			}

			Result += " -Wall -Werror";

			if (!CompileEnvironment.Architecture.StartsWith("x86_64") && !CompileEnvironment.Architecture.StartsWith("i686"))
			{
				Result += " -funwind-tables";               // generate unwind tables as they are needed for backtrace (on x86(64) they are generated implicitly)
			}

			Result += " -Wsequence-point";              // additional warning not normally included in Wall: warns if order of operations is ambigious
			//Result += " -Wunreachable-code";            // additional warning not normally included in Wall: warns if there is code that will never be executed - not helpful due to bIsGCC and similar
			//Result += " -Wshadow";                      // additional warning not normally included in Wall: warns if there variable/typedef shadows some other variable - not helpful because we have gobs of code that shadows variables
			Result += " -Wdelete-non-virtual-dtor";

			Result += ArchitectureSpecificSwitches(CompileEnvironment.Architecture);

			Result += " -fno-math-errno";               // do not assume that math ops have side effects

			Result += GetRTTIFlag(CompileEnvironment);	// flag for run-time type info

			if (CompileEnvironment.bHideSymbolsByDefault)
			{
				Result += " -fvisibility-ms-compat";
				Result += " -fvisibility-inlines-hidden";
			}

			if (String.IsNullOrEmpty(ClangPath))
			{
				// GCC only option
				Result += " -fno-strict-aliasing";
				Result += " -Wno-sign-compare"; // needed to suppress: comparison between signed and unsigned integer expressions
				Result += " -Wno-enum-compare"; // Stats2.h triggers this (alignof(int64) <= DATA_ALIGN)
				Result += " -Wno-return-type"; // Variant.h triggers this
				Result += " -Wno-unused-local-typedefs";
				Result += " -Wno-multichar";
				Result += " -Wno-unused-but-set-variable";
				Result += " -Wno-strict-overflow"; // Array.h:518
			}
			else
			{
				// Clang only options
				if (CrossCompiling())
				{
					if (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Win64 || BuildHostPlatform.Current.Platform == BuildTargetPlatform.Win32)
					{
						Result += " -fdiagnostics-format=msvc";     // make diagnostics compatible with MSVC when cross-compiling
					}
					else if (Log.ColorConsoleOutput)
					{
						Result += " -fcolor-diagnostics";
					}
				}

				// output full paths to the files when the build fails, required 4.0+ of clang
				if (CompilerVersionGreaterOrEqual(4, 0, 0))
				{
					Result += " -fdiagnostics-absolute-paths";
				}

				Result += " -Wno-unused-private-field";     // MultichannelTcpSocket.h triggers this, possibly more
				// this hides the "warning : comparison of unsigned expression < 0 is always false" type warnings due to constant comparisons, which are possible with template arguments
				Result += " -Wno-tautological-compare";

				// this switch is understood by clang 3.5.0, but not clang-3.5 as packaged by Ubuntu 14.04 atm
				if (CompilerVersionGreaterOrEqual(3, 5, 0))
				{
					Result += " -Wno-undefined-bool-conversion";	// hides checking if 'this' pointer is null
				}

				if (CompilerVersionGreaterOrEqual(3, 6, 0))
				{
					Result += " -Wno-unused-local-typedef";	// clang is being overly strict here? PhysX headers trigger this.
					Result += " -Wno-inconsistent-missing-override";	// these have to be suppressed for C++ engine, should be fixed later.
				}

				if (CompilerVersionGreaterOrEqual(3, 9, 0))
				{
					Result += " -Wno-undefined-var-template"; // not really a good warning to disable
				}

				if (CompilerVersionGreaterOrEqual(5, 0, 0))
				{
					Result += " -Wno-unused-lambda-capture";  // suppressed because capturing of compile-time constants is seemingly inconsistent. And MSVC doesn't do that.
				}
			}

			Result += " -Wno-unused-variable";
			// this will hide the warnings about static functions in headers that aren't used in every single .cpp file
			Result += " -Wno-unused-function";
			// this hides the "enumeration value 'XXXXX' not handled in switch [-Wswitch]" warnings - we should maybe remove this at some point and add UE_LOG(, Fatal, ) to default cases
			Result += " -Wno-switch";
			Result += " -Wno-unknown-pragmas";			// Slate triggers this (with its optimize on/off pragmas)
			// needed to suppress warnings about using offsetof on non-POD types.
			Result += " -Wno-invalid-offsetof";
			// we use this feature to allow static FNames.
			Result += " -Wno-gnu-string-literal-operator-template";

			// Profile Guided Optimization (PGO) and Link Time Optimization (LTO)
			// Whether we actually can enable that is checked in CanUseAdvancedLinkerFeatures() earlier
			if (CompileEnvironment.bPGOOptimize)
			{
				//
				// Clang emits a warning for each compiled function that doesn't have a matching entry in the profile data.
				// This can happen when the profile data is older than the binaries we're compiling.
				//
				// Disable this warning. It's far too verbose.
				//
				Result += " -Wno-backend-plugin";

				Log.TraceInformationOnce("Enabling Profile Guided Optimization (PGO). Linking will take a while.");
				Result += string.Format(" -fprofile-instr-use=\"{0}\"", Path.Combine(CompileEnvironment.PGODirectory, CompileEnvironment.PGOFilenamePrefix));
			}
			else if (CompileEnvironment.bPGOProfile)
			{
				Log.TraceInformationOnce("Enabling Profile Guided Instrumentation (PGI). Linking will take a while.");
				Result += " -fprofile-generate";
			}

			// Unlike on other platforms, allow LTO be specified independently of PGO
			// Whether we actually can enable that is checked in CanUseAdvancedLinkerFeatures() earlier
			if (CompileEnvironment.bAllowLTCG)
			{
				if((Options & LinuxToolChainOptions.EnableThinLTO) != 0)
				{
					Result += " -flto=thin";
				}
				else
				{
					Result += " -flto";
				}
			}

			if (CompileEnvironment.ShadowVariableWarningLevel != WarningLevel.Off)
			{
				Result += " -Wshadow" + ((CompileEnvironment.ShadowVariableWarningLevel == WarningLevel.Error) ? "" : " -Wno-error=shadow");
			}

			if (CompileEnvironment.bEnableUndefinedIdentifierWarnings)
			{
				Result += " -Wundef" + (CompileEnvironment.bUndefinedIdentifierWarningsAsErrors ? "" : " -Wno-error=undef");
			}

			//Result += " -DOPERATOR_NEW_INLINE=FORCENOINLINE";

			// shipping builds will cause this warning with "ensure", so disable only in those case
			if (CompileEnvironment.Configuration == CppConfiguration.Shipping)
			{
				Result += " -Wno-unused-value";
				Result += " -fomit-frame-pointer";
			}
			// switches to help debugging
			else if (CompileEnvironment.Configuration == CppConfiguration.Debug)
			{
				Result += " -fno-inline";                   // disable inlining for better debuggability (e.g. callstacks, "skip file" in gdb)
				Result += " -fno-omit-frame-pointer";       // force not omitting fp
				Result += " -fstack-protector";             // detect stack smashing
				//Result += " -fsanitize=address";            // detect address based errors (support properly and link to libasan)
			}

			// debug info
			// bCreateDebugInfo is normally set for all configurations, including Shipping - this is needed to enable callstack in Shipping builds (proper resolution: UEPLAT-205, separate files with debug info)
			if (CompileEnvironment.bCreateDebugInfo)
			{
				Result += " -gdwarf-4";

				if (bGdbIndexSection)
				{
					// Generate .debug_pubnames and .debug_pubtypes sections in a format suitable for conversion into a
					// GDB index. This option is only useful with a linker that can produce GDB index version 7.
					Result += " -ggnu-pubnames";
				}
			}

			// optimization level
			if (!CompileEnvironment.bOptimizeCode)
			{
				Result += " -O0";
			}
			else
			{
				// Don't over optimise if using Address/MemorySanitizer or you'll get false positive errors due to erroneous optimisation of necessary Address/MemorySanitizer instrumentation.
				if (Options.HasFlag(LinuxToolChainOptions.EnableAddressSanitizer) || Options.HasFlag(LinuxToolChainOptions.EnableMemorySanitizer))
				{
					Result += " -O1 -g -fno-optimize-sibling-calls -fno-omit-frame-pointer";
				}
				else if (Options.HasFlag(LinuxToolChainOptions.EnableThreadSanitizer))
				{
					Result += " -O1 -g";
				}
				else
				{
					Result += " -O2";	// warning: as of now (2014-09-28), clang 3.5.0 miscompiles PlatformerGame with -O3 (bitfields?)
				}
			}

			if (!CompileEnvironment.bUseInlining)
			{
				Result += " -fno-inline-functions";
			}

			if (CompileEnvironment.bIsBuildingDLL)
			{
				Result += " -fPIC";
				// Use local-dynamic TLS model. This generates less efficient runtime code for __thread variables, but avoids problems of running into
				// glibc/ld.so limit (DTV_SURPLUS) for number of dlopen()'ed DSOs with static TLS (see e.g. https://www.cygwin.com/ml/libc-help/2013-11/msg00033.html)
				Result += " -ftls-model=local-dynamic";
			}
			else
			{
				Result += " -ffunction-sections";
				Result += " -fdata-sections";
			}

			if (CompileEnvironment.bEnableExceptions)
			{
				Result += " -fexceptions";
				Result += " -DPLATFORM_EXCEPTIONS_DISABLED=0";
			}
			else
			{
				Result += " -fno-exceptions";               // no exceptions
				Result += " -DPLATFORM_EXCEPTIONS_DISABLED=1";
			}

			if (bSuppressPIE && !CompileEnvironment.bIsBuildingDLL)
			{
				Result += " -fno-PIE";
			}

			if (PlatformSDK.bVerboseCompiler)
			{
				Result += " -v";                            // for better error diagnosis
			}

			Result += ArchitectureSpecificDefines(CompileEnvironment.Architecture);
			if (CrossCompiling())
			{
				if (UsingClang() && !string.IsNullOrEmpty(CompileEnvironment.Architecture))
				{
					Result += String.Format(" -target {0}", CompileEnvironment.Architecture);        // Set target triple
				}
				Result += String.Format(" --sysroot=\"{0}\"", BaseLinuxPath);
			}

			return Result;
		}

		
		// Sanitizes a definition argument if needed.
		
		// <param name="definition">A string in the format "foo=bar".</param>
		// <returns></returns>
		internal static string EscapeArgument(string definition)
		{
			string[] splitData = definition.Split('=');
			string myKey = splitData.ElementAtOrDefault(0);
			string myValue = splitData.ElementAtOrDefault(1);

			if (string.IsNullOrEmpty(myKey)) { return ""; }
			if (!string.IsNullOrEmpty(myValue))
			{
				if (!myValue.StartsWith("\"") && (myValue.Contains(" ") || myValue.Contains("$")))
				{
					myValue = myValue.Trim('\"');		// trim any leading or trailing quotes
					myValue = "\"" + myValue + "\"";	// ensure wrap string with double quotes
				}

				// replace double quotes to escaped double quotes if exists
				myValue = myValue.Replace("\"", "\\\"");
			}

			return myValue == null
				? string.Format("{0}", myKey)
				: string.Format("{0}={1}", myKey, myValue);
		}

		private static string GetCompilerStandardVersion_CPP(CppCompileEnvironment CompileEnvironment)
		{
			if (CompileEnvironment.CppStandard == CppStandardVersion.Cpp14 || CompileEnvironment.CppStandard == CppStandardVersion.Default)
			{
				return " -std=c++14";
			}
			else if (CompileEnvironment.CppStandard == CppStandardVersion.Cpp17)
			{
				return " -std=c++17";
			}
			else if (CompileEnvironment.CppStandard == CppStandardVersion.Latest)
			{
				return " -std=c++17";
			}

			throw new BuildException(
			string.Format("Unknown C++ standard type set: {0}", CompileEnvironment.CppStandard));
		}

		private static string GetCompileArguments_CPP(CppCompileEnvironment CompileEnvironment)
		{
			string Result = "";
			Result += " -x c++";
			Result += GetCompilerStandardVersion_CPP(CompileEnvironment);
			return Result;
		}

		private static string GetCompileArguments_C()
		{
			string Result = "";
			Result += " -x c";
			return Result;
		}

		private static string GetCompileArguments_MM(CppCompileEnvironment CompileEnvironment)
		{
			string Result = "";
			Result += " -x objective-c++";
			Result += " -fobjc-abi-version=2";
			Result += " -fobjc-legacy-dispatch";
			Result += GetCompilerStandardVersion_CPP(CompileEnvironment);
			return Result;
		}

		// Conditionally enable (default disabled) generation of information about every class with virtual functions for use by the C++ runtime type identification features
		// (`dynamic_cast' and `typeid'). If you don't use those parts of the language, you can save some space by using -fno-rtti.
		// Note that exception handling uses the same information, but it will generate it as needed.
		private static string GetRTTIFlag(CppCompileEnvironment CompileEnvironment)
		{
			string Result;

			if (CompileEnvironment.bUseRTTI)
			{
				Result = " -frtti";
			}
			else
			{
				Result = " -fno-rtti";
			}

			return Result;
		}

		private static string GetCompileArguments_M(CppCompileEnvironment CompileEnvironment)
		{
			string Result = "";
			Result += " -x objective-c";
			Result += " -fobjc-abi-version=2";
			Result += " -fobjc-legacy-dispatch";
			Result += GetCompilerStandardVersion_CPP(CompileEnvironment);
			return Result;
		}

		private static string GetCompileArguments_PCH(CppCompileEnvironment CompileEnvironment)
		{
			string Result = "";
			Result += " -x c++-header";
			Result += GetCompilerStandardVersion_CPP(CompileEnvironment);
			return Result;
		}

		private string GetLinkArguments(LinkEnvironment LinkEnvironment)
		{
			string Result = "";

			if (UsingLld(LinkEnvironment.Architecture) && (!LinkEnvironment.bIsBuildingDLL || (CompilerVersionMajor >= 9)))
			{
				Result += (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Win64) ? " -fuse-ld=lld.exe" : " -fuse-ld=lld";
			}

			// debugging symbols
			// Applying to all configurations @FIXME: temporary hack for FN to enable callstack in Shipping builds (proper resolution: UEPLAT-205)
			Result += " -rdynamic";   // needed for backtrace_symbols()...

			if (LinkEnvironment.bIsBuildingDLL)
			{
				Result += " -shared";
			}
			else
			{
				// ignore unresolved symbols in shared libs
				Result += string.Format(" -Wl,--unresolved-symbols=ignore-in-shared-libs");
			}

			if (Options.HasFlag(LinuxToolChainOptions.EnableAddressSanitizer) ||
				Options.HasFlag(LinuxToolChainOptions.EnableThreadSanitizer) ||
				Options.HasFlag(LinuxToolChainOptions.EnableUndefinedBehaviorSanitizer) ||
				Options.HasFlag(LinuxToolChainOptions.EnableMemorySanitizer))
			{
				Result += " -g";

				if (Options.HasFlag(LinuxToolChainOptions.EnableSharedSanitizer))
				{
					Result += " -shared-libsan";
				}

				if (Options.HasFlag(LinuxToolChainOptions.EnableAddressSanitizer))
				{
					Result += " -fsanitize=address";
				}
				else if (Options.HasFlag(LinuxToolChainOptions.EnableThreadSanitizer))
				{
					Result += " -fsanitize=thread";
				}
				else if (Options.HasFlag(LinuxToolChainOptions.EnableUndefinedBehaviorSanitizer))
				{
					Result += " -fsanitize=undefined";
				}
				else if (Options.HasFlag(LinuxToolChainOptions.EnableMemorySanitizer))
				{
					// -fsanitize-memory-track-origins adds a 1.5x-2.5x slow ontop of MSan normal amount of overhead
					// -fsanitize-memory-track-origins=1 is faster but collects only allocation points but not intermediate stores
					Result += " -fsanitize=memory -fsanitize-memory-track-origins";
				}

				if (CrossCompiling())
				{
					Result += string.Format(" -Wl,-rpath=\"{0}/lib/clang/{1}.{2}.{3}/lib/linux\"",
							BaseLinuxPath, CompilerVersionMajor, CompilerVersionMinor, CompilerVersionPatch);
				}
			}

			if (UsingLld(Architecture) && LinkEnvironment.bCreateDebugInfo && bGdbIndexSection)
			{
				// Generate .gdb_index section. On my machine, this cuts symbol loading time (breaking at main) from 45
				// seconds to 17 seconds (with gdb v8.3.1).
				Result += " -Wl,--gdb-index";
			}

			// RPATH for third party libs
			Result += " -Wl,-rpath=${ORIGIN}";
			Result += " -Wl,-rpath-link=${ORIGIN}";
			Result += " -Wl,-rpath=${ORIGIN}/..";	// for modules that are in sub-folders of the main Engine/Binary/Linux folder
			if (LinkEnvironment.Architecture.StartsWith("x86_64"))
			{
				Result += " -Wl,-rpath=${ORIGIN}/../../../Engine/Binaries/ThirdParty/Qualcomm/Linux";
			}
			else
			{
				// x86_64 is now using updated ICU that doesn't need extra .so
				Result += " -Wl,-rpath=${ORIGIN}/../../../Engine/Binaries/ThirdParty/ICU/icu4c-53_1/Linux/" + LinkEnvironment.Architecture;
			}

			Result += " -Wl,-rpath=${ORIGIN}/../../../Engine/Binaries/ThirdParty/OpenVR/OpenVRv1_5_17/linux64";
			Result += " -Wl,-rpath=${ORIGIN}/../../../Engine/Binaries/ThirdParty/PhysX3/Linux/x86_64-unknown-linux-gnu";

			// Some OS ship ld with new ELF dynamic tags, which use DT_RUNPATH vs DT_RPATH. Since DT_RUNPATH do not propagate to dlopen()ed DSOs,
			// this breaks the editor on such systems. See https://kenai.com/projects/maxine/lists/users/archive/2011-01/message/12 for details
			Result += " -Wl,--disable-new-dtags";

			// This severely improves runtime linker performance. Without using FixDeps the impact on link time is not as big.
			Result += " -Wl,--as-needed";

			// Additionally speeds up editor startup by 1-2s
			Result += " -Wl,--hash-style=gnu";

			// This apparently can help LLDB speed up symbol lookups
			Result += " -Wl,--build-id";
			if (!LinkEnvironment.bIsBuildingDLL)
			{
				Result += " -Wl,--gc-sections";

				if (bSuppressPIE)
				{
					if (CompilerVersionGreaterOrEqual(7, 0, 0))
					{
						Result += " -Wl,-no-pie";
					}
					else
					{
						Result += " -Wl,-nopie";
					}
				}
			}

			// Profile Guided Optimization (PGO) and Link Time Optimization (LTO)
			// Whether we actually can enable that is checked in CanUseAdvancedLinkerFeatures() earlier
			if (LinkEnvironment.bPGOOptimize)
			{
				//
				// Clang emits a warning for each compiled function that doesn't have a matching entry in the profile data.
				// This can happen when the profile data is older than the binaries we're compiling.
				//
				// Disable this warning. It's far too verbose.
				//
				Result += " -Wno-backend-plugin";

				Log.TraceInformationOnce("Enabling Profile Guided Optimization (PGO). Linking will take a while.");
				Result += string.Format(" -fprofile-instr-use=\"{0}\"", Path.Combine(LinkEnvironment.PGODirectory, LinkEnvironment.PGOFilenamePrefix));
			}
			else if (LinkEnvironment.bPGOProfile)
			{
				Log.TraceInformationOnce("Enabling Profile Guided Instrumentation (PGI). Linking will take a while.");
				Result += " -fprofile-generate";
			}

			// whether we actually can do that is checked in CanUseAdvancedLinkerFeatures() earlier
			if (LinkEnvironment.bAllowLTCG)
			{
				if((Options & LinuxToolChainOptions.EnableThinLTO) != 0)
				{
					Result += String.Format(" -flto=thin -Wl,--thinlto-jobs={0}", Utils.GetPhysicalProcessorCount());
				}
				else
				{
					Result += " -flto";
				}
			}

			if (CrossCompiling())
			{
				if (UsingClang())
				{
					Result += String.Format(" -target {0}", LinkEnvironment.Architecture);        // Set target triple
				}
				string SysRootPath = BaseLinuxPath.TrimEnd(new char[] { '\\', '/' });
				Result += String.Format(" \"--sysroot={0}\"", SysRootPath);

				// Linking with the toolchain on linux appears to not search usr/
				if (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Linux)
				{
					Result += String.Format(" -B\"{0}/usr/lib/\"", SysRootPath);
					Result += String.Format(" -B\"{0}/usr/lib64/\"", SysRootPath);
					Result += String.Format(" -L\"{0}/usr/lib/\"", SysRootPath);
					Result += String.Format(" -L\"{0}/usr/lib64/\"", SysRootPath);
				}
			}

			return Result;
		}

		private string GetArchiveArguments() => "rcs";

		// Checks if we actually can use LTO/PGO with this set of tools
		private bool CanUseAdvancedLinkerFeatures(string Architecture)
		{
			return UsingLld(Architecture) && !String.IsNullOrEmpty(LlvmArPath);
		}

		// Returns a helpful string for the user
		private string ExplainWhyCannotUseAdvancedLinkerFeatures(string Architecture)
		{
			string Explanation = "Cannot use LTO/PGO on this toolchain:";
			int NumProblems = 0;
			if (!UsingLld(Architecture))
			{
				Explanation += " not using lld";
				++NumProblems;
			}
			if (String.IsNullOrEmpty(LlvmArPath))
			{
				if (NumProblems > 0)
				{
					Explanation += " and";
				}
				Explanation += " llvm-ar was not found";
			}
			return Explanation;
		}

		private void PrintBuildDetails(CppCompileEnvironment CompileEnvironment)
		{
			Log.TraceInformation("------- Build details --------");
			Log.TraceInformation("Using {0}.", ToolchainInfo);
			Log.TraceInformation("Using {0} ({1}) version '{2}' (string), {3} (major), {4} (minor), {5} (patch)",
				String.IsNullOrEmpty(ClangPath) ? "gcc" : "clang",
				String.IsNullOrEmpty(ClangPath) ? GCCPath : ClangPath,
				CompilerVersionString, CompilerVersionMajor, CompilerVersionMinor, CompilerVersionPatch);

			if (UsingClang())
			{
				// inform the user which C++ library the engine is going to be compiled against - important for compatibility with third party code that uses STL
				Log.TraceInformation("Using {0} standard C++ library.", ShouldUseLibcxx(CompileEnvironment.Architecture) ? "bundled libc++" : "compiler default (most likely libstdc++)");
				Log.TraceInformation("Using {0}", UsingLld(CompileEnvironment.Architecture) ? "lld linker" : "default linker (ld)");
				Log.TraceInformation("Using {0}", !String.IsNullOrEmpty(LlvmArPath) ? String.Format("llvm-ar : {0}", LlvmArPath) : String.Format("ar and ranlib: {0}, {1}", ArPath, RanlibPath));
			}

			if (Options.HasFlag(LinuxToolChainOptions.EnableAddressSanitizer)           ||
				Options.HasFlag(LinuxToolChainOptions.EnableThreadSanitizer)            ||
				Options.HasFlag(LinuxToolChainOptions.EnableUndefinedBehaviorSanitizer) ||
				Options.HasFlag(LinuxToolChainOptions.EnableMemorySanitizer))
			{
				string SanitizerInfo = "Building with:";
				string StaticOrShared = Options.HasFlag(LinuxToolChainOptions.EnableSharedSanitizer) ? " dynamically" : " statically";

				SanitizerInfo += Options.HasFlag(LinuxToolChainOptions.EnableAddressSanitizer) ? StaticOrShared + " linked AddressSanitizer" : "";
				SanitizerInfo += Options.HasFlag(LinuxToolChainOptions.EnableThreadSanitizer) ? StaticOrShared + " linked ThreadSanitizer" : "";
				SanitizerInfo += Options.HasFlag(LinuxToolChainOptions.EnableUndefinedBehaviorSanitizer) ? StaticOrShared + " linked UndefinedBehaviorSanitizer" : "";
				SanitizerInfo += Options.HasFlag(LinuxToolChainOptions.EnableMemorySanitizer) ? StaticOrShared + " linked MemorySanitizer" : "";

				Log.TraceInformation(SanitizerInfo);
			}

			// Also print other once-per-build information
			if (bUseFixdeps)
			{
				Log.TraceInformation("Using old way to relink circularly dependent libraries (with a FixDeps step).");
			}
			else
			{
				Log.TraceInformation("Using fast way to relink  circularly dependent libraries (no FixDeps).");
			}

			if (CompileEnvironment.bPGOOptimize)
			{
				Log.TraceInformation("Using PGO (profile guided optimization).");
				Log.TraceInformation("  Directory for PGO data files='{0}'", CompileEnvironment.PGODirectory);
				Log.TraceInformation("  Prefix for PGO data files='{0}'", CompileEnvironment.PGOFilenamePrefix);
			}

			if (CompileEnvironment.bPGOProfile)
			{
				Log.TraceInformation("Using PGI (profile guided instrumentation).");
			}

			if (CompileEnvironment.bAllowLTCG)
			{
				Log.TraceInformation("Using LTO (link-time optimization).");
			}

			if (bSuppressPIE)
			{
				Log.TraceInformation("Compiler is set up to generate position independent executables by default, but we're suppressing it.");
			}
			Log.TraceInformation("------------------------------");
		}

		private bool CheckSDKVersionFromFile(string VersionPath, out string ErrorMessage)
		{
			if (File.Exists(VersionPath))
			{
				StreamReader SDKVersionFile = new StreamReader(VersionPath);
				string SDKVersionString = SDKVersionFile.ReadLine();
				SDKVersionFile.Close();

				if (SDKVersionString != null)
				{
					return PlatformSDK.CheckSDKCompatible(SDKVersionString, out ErrorMessage);
				}
			}

			ErrorMessage = "Cannot use an old toolchain (missing " + PlatformSDK.SDKVersionFileName() + " file, assuming version earlier than v11)";
			return false;
		}

		public override CPPOutput CompileCPPFiles(CppCompileEnvironment CompileEnvironment, List<FileItem> InputFiles, DirectoryReference OutputDir, string ModuleName, IActionGraphBuilder Graph)
		{
			string Arguments = GetCLArguments_Global(CompileEnvironment);
			string PCHArguments = "";

			//var BuildPlatform = UEBuildPlatform.GetBuildPlatform(CompileEnvironment.Platform);

			if (!bHasPrintedBuildDetails)
			{
				PrintBuildDetails(CompileEnvironment);

				string LinuxDependenciesPath = Path.Combine(BuildTool.EngineSourceThirdPartyDirectory.FullName, "Linux", PlatformSDK.HaveLinuxDependenciesFile());
				if (!File.Exists(LinuxDependenciesPath))
				{
					throw new BuildException("Please make sure that Engine/Source/ThirdParty/Linux is complete (re - run Setup script if using a github build)");
				}

				if (MultiArchRoot.HasValue())
				{
					if (!CheckSDKVersionFromFile(Path.Combine(MultiArchRoot, PlatformSDK.SDKVersionFileName()), out string ErrorMessage))
					{
						throw new BuildException(ErrorMessage);
					}
				}

				bHasPrintedBuildDetails = true;
			}

			if ((CompileEnvironment.bAllowLTCG || CompileEnvironment.bPGOOptimize || CompileEnvironment.bPGOProfile) && !CanUseAdvancedLinkerFeatures(CompileEnvironment.Architecture))
			{
				throw new BuildException(ExplainWhyCannotUseAdvancedLinkerFeatures(CompileEnvironment.Architecture));
			}

			if (CompileEnvironment.PCHAction == PCHAction.Include)
			{
				PCHArguments += string.Format(" -include \"{0}\"", CompileEnvironment.PCHIncludeFilename.FullName.Replace('\\', '/'));
			}

			// Add include paths to the argument list.
			foreach (DirectoryReference IncludePath in CompileEnvironment.UserIncludePaths)
			{
				string IncludePathString;
				if (IncludePath.IsUnderDirectory(BuildTool.RootDirectory))
				{
					IncludePathString = IncludePath.MakeRelativeTo(BuildTool.EngineSourceDirectory);
				}
				else
				{
					IncludePathString = IncludePath.FullName;
				}
				Arguments += string.Format(" -I\"{0}\"", IncludePathString.Replace('\\', '/'));
			}
			foreach (DirectoryReference IncludePath in CompileEnvironment.SystemIncludePaths)
			{
				Arguments += string.Format(" -I\"{0}\"", IncludePath.FullName.Replace('\\', '/'));
			}

			// Add preprocessor definitions to the argument list.
			foreach (string Definition in CompileEnvironment.Definitions)
			{
				Arguments += string.Format(" -D \"{0}\"", EscapeArgument(Definition));
			}

			// Create a compile action for each source file.
			CPPOutput Result = new CPPOutput();
			foreach (FileItem SourceFile in InputFiles)
			{
				Action CompileAction = Graph.CreateAction(ActionType.Compile);
				CompileAction.PrerequisiteItems.AddRange(CompileEnvironment.ForceIncludeFiles);
				CompileAction.PrerequisiteItems.AddRange(CompileEnvironment.AdditionalPrerequisites);

				string FileArguments = "";
				string Extension = Path.GetExtension(SourceFile.AbsolutePath).ToUpperInvariant();

				// Add C or C++ specific compiler arguments.
				if (CompileEnvironment.PCHAction == PCHAction.Create)
				{
					FileArguments += GetCompileArguments_PCH(CompileEnvironment);
				}
				else if (Extension == ".C")
				{
					// Compile the file as C code.
					FileArguments += GetCompileArguments_C();
				}
				else if (Extension == ".MM")
				{
					// Compile the file as Objective-C++ code.
					FileArguments += GetCompileArguments_MM(CompileEnvironment);
					FileArguments += GetRTTIFlag(CompileEnvironment);
				}
				else if (Extension == ".M")
				{
					// Compile the file as Objective-C code.
					FileArguments += GetCompileArguments_M(CompileEnvironment);
				}
				else
				{
					FileArguments += GetCompileArguments_CPP(CompileEnvironment);

					// only use PCH for .cpp files
					FileArguments += PCHArguments;
				}

				foreach (FileItem ForceIncludeFile in CompileEnvironment.ForceIncludeFiles)
				{
					FileArguments += String.Format(" -include \"{0}\"", ForceIncludeFile.FileDirectory.FullName.Replace('\\', '/'));
				}

				// Add the C++ source file and its included files to the prerequisite item list.
				CompileAction.PrerequisiteItems.Add(SourceFile);

				if (CompileEnvironment.PCHAction == PCHAction.Create)
				{
					// Add the precompiled header file to the produced item list.
					FileItem PrecompiledHeaderFile = FileItem.GetItemByFileReference(FileReference.Combine(OutputDir, Path.GetFileName(SourceFile.AbsolutePath) + ".gch"));

					CompileAction.ProducedItems.Add(PrecompiledHeaderFile);
					Result.PCHFile = PrecompiledHeaderFile;

					// Add the parameters needed to compile the precompiled header file to the command-line.
					FileArguments += string.Format(" -o \"{0}\"", PrecompiledHeaderFile.AbsolutePath.Replace('\\', '/'));
				}
				else
				{
					if (CompileEnvironment.PCHAction == PCHAction.Include)
					{
						CompileAction.PrerequisiteItems.Add(CompileEnvironment.PrecompiledHeaderFile);
					}

					// Add the object file to the produced item list.
					FileItem ObjectFile = FileItem.GetItemByFileReference(FileReference.Combine(OutputDir, Path.GetFileName(SourceFile.AbsolutePath) + ".o"));
					CompileAction.ProducedItems.Add(ObjectFile);
					Result.ObjectFiles.Add(ObjectFile);

					FileArguments += string.Format(" -o \"{0}\"", ObjectFile.AbsolutePath.Replace('\\', '/'));
				}

				// Add the source file path to the command-line.
				FileArguments += string.Format(" \"{0}\"", SourceFile.AbsolutePath.Replace('\\', '/'));

				// Generate the included header dependency list
				if(CompileEnvironment.bGenerateDependenciesFile)
				{
					FileItem DependencyListFile = FileItem.GetItemByFileReference(FileReference.Combine(OutputDir, Path.GetFileName(SourceFile.AbsolutePath) + ".d"));
					FileArguments += string.Format(" -MD -MF\"{0}\"", DependencyListFile.AbsolutePath.Replace('\\', '/'));
					CompileAction.DependencyListFile = DependencyListFile;
					CompileAction.ProducedItems.Add(DependencyListFile);
				}

				CompileAction.WorkingDirectory = BuildTool.EngineSourceDirectory;
				if (!UsingClang())
				{
					CompileAction.CommandPath = new FileReference(GCCPath);
				}
				else
				{
					CompileAction.CommandPath = new FileReference(ClangPath);
				}

				string AllArguments = (Arguments + FileArguments + CompileEnvironment.AdditionalArguments);
				// all response lines should have / instead of \, but we cannot just bulk-replace it here since some \ are used to escape quotes, e.g. Definitions.Add("FOO=TEXT(\"Bar\")");

				Debug.Assert(CompileAction.ProducedItems.Count > 0);

				FileReference CompilerResponseFileName = CompileAction.ProducedItems[0].FileDirectory + ".rsp";
				FileItem CompilerResponseFileItem = Graph.CreateIntermediateTextFile(CompilerResponseFileName, AllArguments);

				CompileAction.CommandArguments = string.Format(" @\"{0}\"", CompilerResponseFileName);
				CompileAction.PrerequisiteItems.Add(CompilerResponseFileItem);
				CompileAction.CommandDescription = "Compile";
				CompileAction.StatusDescription = Path.GetFileName(SourceFile.AbsolutePath);
				CompileAction.bIsGCCCompiler = true;

				// Don't farm out creation of pre-compiled headers as it is the critical path task.
				CompileAction.bCanExecuteRemotely =
					CompileEnvironment.PCHAction != PCHAction.Create ||
					CompileEnvironment.bAllowRemotelyCompiledPCHs;
			}

			return Result;
		}

		bool UsingLld(string Architecture)
		{
			return bUseLld && (Architecture.StartsWith("x86_64") || (9 <= CompilerVersionMajor));
		}

		public override CPPOutput CompileRCFiles(CppCompileEnvironment Environment, List<FileItem> InputFiles, DirectoryReference OutputDir, IActionGraphBuilder Graph)
		{
			return null;
		}
		
		// Creates an action to archive all the .o files into single .a file
		public FileItem CreateArchiveAndIndex(LinkEnvironment LinkEnvironment, IActionGraphBuilder Graph)
		{
			// Create an archive action
			Action ArchiveAction = Graph.CreateAction(ActionType.Link);
			ArchiveAction.WorkingDirectory = BuildTool.EngineSourceDirectory;
			ArchiveAction.CommandPath = BuildHostPlatform.Current.ShellPath;

			if (BuildHostPlatform.Current.ShellType == ShellType.Shell)
			{
				ArchiveAction.CommandArguments = "-c '";
			}
			else
			{
				ArchiveAction.CommandArguments = "/c \"";
			}

			// this will produce a final library
			ArchiveAction.bProducesImportLibrary = true;

			// Add the output file as a production of the link action.
			FileItem OutputFile = FileItem.GetItemByFileReference(LinkEnvironment.OutputFilePath);
			ArchiveAction.ProducedItems.Add(OutputFile);
			ArchiveAction.CommandDescription = "Archive";
			ArchiveAction.StatusDescription = Path.GetFileName(OutputFile.AbsolutePath);
			ArchiveAction.CommandArguments += string.Format("\"{0}\" {1} \"{2}\"", ArPath, GetArchiveArguments(), OutputFile.AbsolutePath);

			// Add the input files to a response file, and pass the response file on the command-line.
			List<string> InputFileNames = new List<string>();
			foreach (FileItem InputFile in LinkEnvironment.InputFiles)
			{
				string InputAbsolutePath = InputFile.AbsolutePath.Replace("\\", "/");
				InputFileNames.Add(string.Format("\"{0}\"", InputAbsolutePath));
				ArchiveAction.PrerequisiteItems.Add(InputFile);
			}

			// this won't stomp linker's response (which is not used when compiling static libraries)
			FileReference ResponsePath = GetResponseFileName(LinkEnvironment, OutputFile);
			if (!ProjectFileGenerator.bGenerateProjectFiles)
			{
				FileItem ResponseFileItem = Graph.CreateIntermediateTextFile(ResponsePath, InputFileNames);
				ArchiveAction.PrerequisiteItems.Add(ResponseFileItem);
			}
			ArchiveAction.CommandArguments += string.Format(" @\"{0}\"", ResponsePath.FullName);

			// add ranlib if not using llvm-ar
			if (String.IsNullOrEmpty(LlvmArPath))
			{
				ArchiveAction.CommandArguments += string.Format(" && \"{0}\" \"{1}\"", RanlibPath, OutputFile.AbsolutePath);
			}

			// Add the additional arguments specified by the environment.
			ArchiveAction.CommandArguments += LinkEnvironment.AdditionalArguments;
			ArchiveAction.CommandArguments = ArchiveAction.CommandArguments.Replace("\\", "/");

			if (BuildHostPlatform.Current.ShellType == ShellType.Shell)
			{
				ArchiveAction.CommandArguments += "'";
			}
			else
			{
				ArchiveAction.CommandArguments += "\"";
			}

			// Only execute linking on the local PC.
			ArchiveAction.bCanExecuteRemotely = false;

			return OutputFile;
		}

		public FileItem FixDependencies(LinkEnvironment LinkEnvironment, FileItem Executable, IActionGraphBuilder Graph)
		{
			if (bUseFixdeps)
			{
				if (!LinkEnvironment.bIsCrossReferenced)
				{
					return null;
				}

				Log.TraceVerbose("Adding postlink step");

				bool bUseCmdExe = BuildHostPlatform.Current.ShellType == ShellType.Cmd;
				FileReference ShellBinary = BuildHostPlatform.Current.ShellPath;
				string ExecuteSwitch = bUseCmdExe ? " /C" : ""; // avoid -c so scripts don't need +x
				string ScriptName = bUseCmdExe ? "FixDependencies.bat" : "FixDependencies.sh";

				FileItem FixDepsScript = FileItem.GetItemByFileReference(FileReference.Combine(LinkEnvironment.LocalShadowDirectory, ScriptName));

				Action PostLinkAction = Graph.CreateAction(ActionType.Link);
				PostLinkAction.WorkingDirectory = BuildTool.EngineSourceDirectory;
				PostLinkAction.CommandPath = ShellBinary;
				PostLinkAction.StatusDescription = string.Format("{0}", Path.GetFileName(Executable.AbsolutePath));
				PostLinkAction.CommandDescription = "FixDeps";
				PostLinkAction.bCanExecuteRemotely = false;
				PostLinkAction.CommandArguments = ExecuteSwitch;

				PostLinkAction.CommandArguments += bUseCmdExe ? " \"" : " -c '";

				FileItem OutputFile = FileItem.GetItemByFileReference(FileReference.Combine(LinkEnvironment.LocalShadowDirectory, Path.GetFileNameWithoutExtension(Executable.AbsolutePath) + ".link"));

				// Make sure we don't run this script until the all executables and shared libraries
				// have been built.
				PostLinkAction.PrerequisiteItems.Add(Executable);
				foreach (FileItem Dependency in AllBinaries)
				{
					PostLinkAction.PrerequisiteItems.Add(Dependency);
				}

				PostLinkAction.CommandArguments += ShellBinary + ExecuteSwitch + " \"" + FixDepsScript.AbsolutePath + "\" && ";

				// output file should not be empty or it will be rebuilt next time
				string Touch = bUseCmdExe ? "echo \"Dummy\" >> \"{0}\" && copy /b \"{0}\" +,," : "echo \"Dummy\" >> \"{0}\"";

				PostLinkAction.CommandArguments += String.Format(Touch, OutputFile.AbsolutePath);
				PostLinkAction.CommandArguments += bUseCmdExe ? "\"" : "'";

				System.Console.WriteLine("{0} {1}", PostLinkAction.CommandPath, PostLinkAction.CommandArguments);

				PostLinkAction.ProducedItems.Add(OutputFile);
				return OutputFile;
			}
			else
			{
				return null;
			}
		}

		// allow sub-platforms to modify the name of the output file
		private FileItem GetLinkOutputFile(LinkEnvironment LinkEnvironment)
		{
			return FileItem.GetItemByFileReference(LinkEnvironment.OutputFilePath);
		}

		public override FileItem LinkFiles(LinkEnvironment LinkEnvironment, bool bBuildImportLibraryOnly, IActionGraphBuilder Graph)
		{
			Debug.Assert(!bBuildImportLibraryOnly);

			if ((LinkEnvironment.bAllowLTCG  || 
				LinkEnvironment.bPGOOptimize || 
				LinkEnvironment.bPGOProfile) && 
				!CanUseAdvancedLinkerFeatures(LinkEnvironment.Architecture))
			{
				throw new BuildException(ExplainWhyCannotUseAdvancedLinkerFeatures(LinkEnvironment.Architecture));
			}

			List<string> RPaths = new List<string>();

			if (LinkEnvironment.bIsBuildingLibrary || bBuildImportLibraryOnly)
			{
				return CreateArchiveAndIndex(LinkEnvironment, Graph);
			}

			// Create an action that invokes the linker.
			Action LinkAction = Graph.CreateAction(ActionType.Link);
			LinkAction.WorkingDirectory = BuildTool.EngineSourceDirectory;

			string LinkCommandString;
			if (String.IsNullOrEmpty(ClangPath))
			{
				LinkCommandString = "\"" + GCCPath + "\"";
			}
			else
			{
				LinkCommandString = "\"" + ClangPath + "\"";
			}

			// Get link arguments.
			LinkCommandString += GetLinkArguments(LinkEnvironment);

			// Tell the action that we're building an import library here and it should conditionally be
			// ignored as a prerequisite for other actions
			LinkAction.bProducesImportLibrary = LinkEnvironment.bIsBuildingDLL;

			// Add the output file as a production of the link action.
			FileItem LinkOutputFile = GetLinkOutputFile(LinkEnvironment);
			LinkAction.ProducedItems.Add(LinkOutputFile);

			// LTO/PGO can take a lot of time, make it clear for the user
			if (LinkEnvironment.bPGOProfile)
			{
				LinkAction.CommandDescription = "Link-PGI";
			}
			else if (LinkEnvironment.bPGOOptimize)
			{
				LinkAction.CommandDescription = "Link-PGO";
			}
			else if (LinkEnvironment.bAllowLTCG)
			{
				LinkAction.CommandDescription = "Link-LTO";
			}
			else
			{
				LinkAction.CommandDescription = "Link";
			}
			// because the logic choosing between lld and ld is somewhat messy atm (lld fails to link .DSO due to bugs), make the name of the linker clear
			LinkAction.CommandDescription += (LinkCommandString.Contains("-fuse-ld=lld")) ? " (lld)" : " (ld)";
			LinkAction.StatusDescription = Path.GetFileName(LinkOutputFile.AbsolutePath);

			// Add the output file to the command-line.
			LinkCommandString += string.Format(" -o \"{0}\"", LinkOutputFile.AbsolutePath);

			// Add the input files to a response file, and pass the response file on the command-line.
			List<string> ResponseLines = new List<string>();
			foreach (FileItem InputFile in LinkEnvironment.InputFiles)
			{
				ResponseLines.Add(string.Format("\"{0}\"", InputFile.AbsolutePath.Replace("\\", "/")));
				LinkAction.PrerequisiteItems.Add(InputFile);
			}

			if (LinkEnvironment.bIsBuildingDLL)
			{
				ResponseLines.Add(string.Format(" -soname=\"{0}\"", LinkOutputFile.FileDirectory.GetFileName()));
			}

			// Start with the configured LibraryPaths and also add paths to any libraries that
			// we depend on (libraries that we've build ourselves).
			List<DirectoryReference> AllLibraryPaths = LinkEnvironment.LibraryPaths;
			foreach (string AdditionalLibrary in LinkEnvironment.AdditionalLibraries)
			{
				string PathToLib = Path.GetDirectoryName(AdditionalLibrary);
				if (!String.IsNullOrEmpty(PathToLib))
				{
					// make path absolute, because FixDependencies script may be executed in a different directory
					DirectoryReference AbsolutePathToLib = new DirectoryReference(PathToLib);
					if (!AllLibraryPaths.Contains(AbsolutePathToLib))
					{
						AllLibraryPaths.Add(AbsolutePathToLib);
					}
				}

				if ((AdditionalLibrary.Contains("Plugins") || AdditionalLibrary.Contains("Binaries/ThirdParty") || AdditionalLibrary.Contains("Binaries\\ThirdParty")) && Path.GetDirectoryName(AdditionalLibrary) != Path.GetDirectoryName(LinkOutputFile.AbsolutePath))
				{
					string RelativePath = new FileReference(AdditionalLibrary).Directory.MakeRelativeTo(LinkOutputFile.FileDirectory.Directory);

					if (LinkEnvironment.bIsBuildingDLL)
					{
						// Remove the root uildTool.RootDirectory from the RuntimeLibaryPath
						string AdditionalLibraryRootPath = new FileReference(AdditionalLibrary).Directory.MakeRelativeTo(BuildTool.RootDirectory);

						// Figure out how many dirs we need to go back
						string RelativeRootPath = BuildTool.RootDirectory.MakeRelativeTo(LinkOutputFile.FileDirectory.Directory);

						// Combine the two together ie. number of ../ + the path after the root
						RelativePath = Path.Combine(RelativeRootPath, AdditionalLibraryRootPath);
					}

					// On Windows, MakeRelativeTo can silently fail if the engine and the project are located on different drives
					if (CrossCompiling() && RelativePath.StartsWith(BuildTool.RootDirectory.FullName))
					{
						// do not replace directly, but take care to avoid potential double slashes or missed slashes
						string PathFromRootDir = RelativePath.Replace(BuildTool.RootDirectory.FullName, "");
						// Path.Combine doesn't combine these properly
						RelativePath = ((PathFromRootDir.StartsWith("\\") || PathFromRootDir.StartsWith("/")) ? "..\\..\\.." : "..\\..\\..\\") + PathFromRootDir;
					}

					if (!RPaths.Contains(RelativePath))
					{
						RPaths.Add(RelativePath);
						ResponseLines.Add(string.Format(" -rpath=\"${{ORIGIN}}/{0}\"", RelativePath.Replace('\\', '/')));
					}
				}
			}

			foreach(string RuntimeLibaryPath in LinkEnvironment.RuntimeLibraryPaths)
			{
				string RelativePath = RuntimeLibaryPath;

				if(!RelativePath.StartsWith("$"))
				{
					if (LinkEnvironment.bIsBuildingDLL)
					{
						// Remove the root BuildTool.RootDirectory from the RuntimeLibaryPath
						string RuntimeLibraryRootPath = new DirectoryReference(RuntimeLibaryPath).MakeRelativeTo(BuildTool.RootDirectory);

						// Figure out how many dirs we need to go back
						string RelativeRootPath = BuildTool.RootDirectory.MakeRelativeTo(LinkOutputFile.FileDirectory.Directory);

						// Combine the two together ie. number of ../ + the path after the root
						RelativePath = Path.Combine(RelativeRootPath, RuntimeLibraryRootPath);
					}
					else
					{
						string RelativeRootPath = new DirectoryReference(RuntimeLibaryPath).MakeRelativeTo(BuildTool.RootDirectory);

						// We're assuming that the binary will be placed according to our ProjectName/Binaries/Platform scheme
						RelativePath = Path.Combine("..", "..", "..", RelativeRootPath);
					}
				}

				// On Windows, MakeRelativeTo can silently fail if the engine and the project are located on different drives
				if (CrossCompiling() && RelativePath.StartsWith(BuildTool.RootDirectory.FullName))
				{
					// do not replace directly, but take care to avoid potential double slashes or missed slashes
					string PathFromRootDir = RelativePath.Replace(BuildTool.RootDirectory.FullName, "");
					// Path.Combine doesn't combine these properly
					RelativePath = ((PathFromRootDir.StartsWith("\\") || PathFromRootDir.StartsWith("/")) ? "..\\..\\.." : "..\\..\\..\\") + PathFromRootDir;
				}

				if (!RPaths.Contains(RelativePath))
				{
					RPaths.Add(RelativePath);
					ResponseLines.Add(string.Format(" -rpath=\"${{ORIGIN}}/{0}\"", RelativePath.Replace('\\', '/')));
				}
			}

			ResponseLines.Add(string.Format(" -rpath-link=\"{0}\"", Path.GetDirectoryName(LinkOutputFile.AbsolutePath)));

			// Add the library paths to the argument list.
			foreach (DirectoryReference LibraryPath in AllLibraryPaths)
			{
				// use absolute paths because of FixDependencies script again
				ResponseLines.Add(string.Format(" -L\"{0}\"", LibraryPath.FullName.Replace('\\', '/')));
			}

			List<string> EngineAndGameLibrariesLinkFlags = new List<string>();
			List<FileItem> EngineAndGameLibrariesFiles = new List<FileItem>();

			string ExternalLibraries = "";

			// add libraries in a library group
			ResponseLines.Add(string.Format(" --start-group"));

			foreach (string AdditionalLibrary in LinkEnvironment.AdditionalLibraries)
			{
				if (String.IsNullOrEmpty(Path.GetDirectoryName(AdditionalLibrary)))
				{
					// library was passed just like "jemalloc", turn it into -ljemalloc
					ExternalLibraries += string.Format(" -l{0}", AdditionalLibrary);
				}
				else if (Path.GetExtension(AdditionalLibrary) == ".a")
				{
					// static library passed in, pass it along but make path absolute, because FixDependencies script may be executed in a different directory
					string AbsoluteAdditionalLibrary = Path.GetFullPath(AdditionalLibrary);
					if (AbsoluteAdditionalLibrary.Contains(" "))
					{
						AbsoluteAdditionalLibrary = string.Format("\"{0}\"", AbsoluteAdditionalLibrary);
					}
					AbsoluteAdditionalLibrary = AbsoluteAdditionalLibrary.Replace('\\', '/');

					// libcrypto/libssl contain number of functions that are being used in different DSOs. FIXME: generalize?
					if (LinkEnvironment.bIsBuildingDLL && (AbsoluteAdditionalLibrary.Contains("libcrypto") || AbsoluteAdditionalLibrary.Contains("libssl")))
					{
						ResponseLines.Add(" --whole-archive " + AbsoluteAdditionalLibrary + " --no-whole-archive");
					}
					else
					{
						ResponseLines.Add(" " + AbsoluteAdditionalLibrary);
					}

					LinkAction.PrerequisiteItems.Add(FileItem.GetItemByPath(AdditionalLibrary));
				}
				else
				{
					// Skip over full-pathed library dependencies when building DLLs to avoid circular
					// dependencies.
					FileItem LibraryDependency = FileItem.GetItemByPath(AdditionalLibrary);

					string LibName = Path.GetFileNameWithoutExtension(AdditionalLibrary);
					if (LibName.StartsWith("lib"))
					{
						// Remove lib prefix
						LibName = LibName.Remove(0, 3);
					}
					string LibLinkFlag = string.Format(" -l{0}", LibName);

					if (LinkEnvironment.bIsBuildingDLL && LinkEnvironment.bIsCrossReferenced)
					{
						// We are building a cross referenced DLL so we can't actually include
						// dependencies at this point. Instead we add it to the list of
						// libraries to be used in the FixDependencies step.
						EngineAndGameLibrariesLinkFlags.Add(LibLinkFlag);
						EngineAndGameLibrariesFiles.Add(LibraryDependency);
						// it is important to add this exactly to the same place where the missing libraries would have been, it will be replaced later
						if (!ExternalLibraries.Contains("--allow-shlib-undefined"))
						{
							ExternalLibraries += string.Format(" -Wl,--allow-shlib-undefined");
						}
					}
					else
					{
						LinkAction.PrerequisiteItems.Add(LibraryDependency);
						ExternalLibraries += LibLinkFlag;
					}
				}
			}
			ResponseLines.Add(" --end-group");

			FileReference ResponseFileName = GetResponseFileName(LinkEnvironment, LinkOutputFile);
			FileItem ResponseFileItem = Graph.CreateIntermediateTextFile(ResponseFileName, ResponseLines);

			LinkCommandString += string.Format(" -Wl,@\"{0}\"", ResponseFileName);
			LinkAction.PrerequisiteItems.Add(ResponseFileItem);

			LinkCommandString += " -Wl,--start-group";
			LinkCommandString += ExternalLibraries;

			// make unresolved symbols an error, unless a) building a cross-referenced DSO  b) we opted out
			if ((!LinkEnvironment.bIsBuildingDLL || !LinkEnvironment.bIsCrossReferenced) && !LinkEnvironment.bIgnoreUnresolvedSymbols)
			{
				// This will make the linker report undefined symbols the current module, but ignore in the dependent DSOs.
				// It is tempting, but may not be possible to change that report-all - due to circular dependencies between our libs.
				LinkCommandString += " -Wl,--unresolved-symbols=ignore-in-shared-libs";
			}
			LinkCommandString += " -Wl,--end-group";

			LinkCommandString += " -lrt"; // needed for clock_gettime()
			LinkCommandString += " -lm"; // math

			if (ShouldUseLibcxx(LinkEnvironment.Architecture))
			{
				// libc++ and its abi lib
				LinkCommandString += " -nodefaultlibs";
				LinkCommandString += " -L" + "ThirdParty/Linux/LibCxx/lib/Linux/" + LinkEnvironment.Architecture + "/";
				LinkCommandString += " " + "ThirdParty/Linux/LibCxx/lib/Linux/" + LinkEnvironment.Architecture + "/libc++.a";
				LinkCommandString += " " + "ThirdParty/Linux/LibCxx/lib/Linux/" + LinkEnvironment.Architecture + "/libc++abi.a";
				LinkCommandString += " -lm";
				LinkCommandString += " -lc";
				LinkCommandString += " -lpthread"; // pthread_mutex_trylock is missing from libc stubs
				LinkCommandString += " -lgcc_s";
				LinkCommandString += " -lgcc";
			}

			// these can be helpful for understanding the order of libraries or library search directories
			if (PlatformSDK.bVerboseLinker)
			{
				LinkCommandString += " -Wl,--verbose";
				LinkCommandString += " -Wl,--trace";
				LinkCommandString += " -v";
			}

			// Add the additional arguments specified by the environment.
			LinkCommandString += LinkEnvironment.AdditionalArguments;
			LinkCommandString = LinkCommandString.Replace("\\\\", "/");
			LinkCommandString = LinkCommandString.Replace("\\", "/");

			bool bUseCmdExe = BuildHostPlatform.Current.ShellType == ShellType.Cmd;
			FileReference ShellBinary = BuildHostPlatform.Current.ShellPath;
			string ExecuteSwitch = bUseCmdExe ? " /C" : ""; // avoid -c so scripts don't need +x

			// Linux has issues with scripts and parameter expansion from curely brakets
			if (!bUseCmdExe)
			{
				LinkCommandString = LinkCommandString.Replace("{", "'{");
				LinkCommandString = LinkCommandString.Replace("}", "}'");
				LinkCommandString = LinkCommandString.Replace("$'{", "'${");	// fixing $'{ORIGIN}' to be '${ORIGIN}'
			}

			string LinkScriptName = string.Format((bUseCmdExe ? "Link-{0}.link.bat" : "Link-{0}.link.sh"), LinkOutputFile.FileDirectory.GetFileName());
			string LinkScriptFullPath = Path.Combine(LinkEnvironment.LocalShadowDirectory.FullName, LinkScriptName);
			Log.TraceVerbose("Creating link script: {0}", LinkScriptFullPath);
			Directory.CreateDirectory(Path.GetDirectoryName(LinkScriptFullPath));
			using (StreamWriter LinkWriter = File.CreateText(LinkScriptFullPath))
			{
				if (bUseCmdExe)
				{
					LinkWriter.Write("@echo off\n");
					LinkWriter.Write("rem Automatically generated by BuildTool\n");
					LinkWriter.Write("rem *DO NOT EDIT*\n\n");
					LinkWriter.Write("set Retries=0\n");
					LinkWriter.Write(":linkloop\n");
					LinkWriter.Write("if %Retries% GEQ 10 goto failedtorelink\n");
					LinkWriter.Write(LinkCommandString + "\n");
					LinkWriter.Write("if %errorlevel% neq 0 goto sleepandretry\n");
					LinkWriter.Write(GetDumpEncodeDebugCommand(LinkEnvironment, LinkOutputFile) + "\n");
					LinkWriter.Write("exit 0\n");
					LinkWriter.Write(":sleepandretry\n");
					LinkWriter.Write("ping 127.0.0.1 -n 1 -w 5000 >NUL 2>NUL\n");     // timeout complains about lack of redirection
					LinkWriter.Write("set /a Retries+=1\n");
					LinkWriter.Write("goto linkloop\n");
					LinkWriter.Write(":failedtorelink\n");
					LinkWriter.Write("echo Failed to link {0} after %Retries% retries\n", LinkOutputFile.AbsolutePath);
					LinkWriter.Write("exit 1\n");
				}
				else
				{
					LinkWriter.Write("#!/bin/sh\n");
					LinkWriter.Write("# Automatically generated by BuildTool\n");
					LinkWriter.Write("# *DO NOT EDIT*\n\n");
					LinkWriter.Write("set -o errexit\n");
					LinkWriter.Write(LinkCommandString + "\n");
					LinkWriter.Write(GetDumpEncodeDebugCommand(LinkEnvironment, LinkOutputFile) + "\n");
				}
			};

			LinkAction.CommandPath = ShellBinary;

			// This must maintain the quotes around the LinkScriptFullPath
			LinkAction.CommandArguments = ExecuteSwitch + " \"" + LinkScriptFullPath + "\"";

			// prepare a linker script
			FileReference LinkerScriptPath = FileReference.Combine(LinkEnvironment.LocalShadowDirectory, "remove-sym.ldscript");
			if (!DirectoryReference.Exists(LinkEnvironment.LocalShadowDirectory))
			{
				DirectoryReference.CreateDirectory(LinkEnvironment.LocalShadowDirectory);
			}
			if (FileReference.Exists(LinkerScriptPath))
			{
				FileReference.Delete(LinkerScriptPath);
			}

			// Only execute linking on the local PC.
			LinkAction.bCanExecuteRemotely = false;

			// Prepare a script that will run later, once all shared libraries and the executable
			// are created. This script will be called by action created in FixDependencies()
			if (LinkEnvironment.bIsCrossReferenced && LinkEnvironment.bIsBuildingDLL)
			{
				if (bUseFixdeps)
				{
					string ScriptName = bUseCmdExe ? "FixDependencies.bat" : "FixDependencies.sh";

					string FixDepsScriptPath = Path.Combine(LinkEnvironment.LocalShadowDirectory.FullName, ScriptName);
					if (!bHasWipedFixDepsScript)
					{
						bHasWipedFixDepsScript = true;
						Log.TraceVerbose("Creating script: {0}", FixDepsScriptPath);
						Directory.CreateDirectory(Path.GetDirectoryName(FixDepsScriptPath));
						using (StreamWriter Writer = File.CreateText(FixDepsScriptPath))
						{
						if (bUseCmdExe)
						{
							Writer.Write("@echo off\n");
							Writer.Write("rem Automatically generated by BuildTool\n");
							Writer.Write("rem *DO NOT EDIT*\n\n");
						}
						else
						{
							Writer.Write("#!/bin/sh\n");
							Writer.Write("# Automatically generated by BuildTool\n");
							Writer.Write("# *DO NOT EDIT*\n\n");
							Writer.Write("set -o errexit\n");
						}
						}
					}

					StreamWriter FixDepsScript = File.AppendText(FixDepsScriptPath);

					string EngineAndGameLibrariesString = "";
					foreach (string Library in EngineAndGameLibrariesLinkFlags)
					{
						EngineAndGameLibrariesString += Library;
					}

					FixDepsScript.Write(string.Format("echo Fixing {0}\n", Path.GetFileName(LinkOutputFile.AbsolutePath)));
					if (!bUseCmdExe)
					{
						FixDepsScript.Write(string.Format("TIMESTAMP=`stat --format %y \"{0}\"`\n", LinkOutputFile.AbsolutePath));
					}
					string FixDepsLine = LinkCommandString;
					string Replace = "-Wl,--allow-shlib-undefined";

					FixDepsLine = FixDepsLine.Replace(Replace, EngineAndGameLibrariesString);
					string OutputFileForwardSlashes = LinkOutputFile.AbsolutePath.Replace("\\", "/");
					FixDepsLine = FixDepsLine.Replace(OutputFileForwardSlashes, OutputFileForwardSlashes + ".fixed");
					FixDepsLine = FixDepsLine.Replace("$", "\\$");
					FixDepsScript.Write(FixDepsLine + "\n");
					if (bUseCmdExe)
					{
						FixDepsScript.Write(string.Format("move /Y \"{0}.fixed\" \"{0}\"\n", LinkOutputFile.AbsolutePath));
					}
					else
					{
						FixDepsScript.Write(string.Format("mv \"{0}.fixed\" \"{0}\"\n", LinkOutputFile.AbsolutePath));
						FixDepsScript.Write(string.Format("touch -d \"$TIMESTAMP\" \"{0}\"\n\n", LinkOutputFile.AbsolutePath));
					}
					FixDepsScript.Close();
				}
				else
				{
					// Create the action to relink the library. This actions does not overwrite the source file so it can be executed in parallel
					Action RelinkAction = Graph.CreateAction(ActionType.Link);
					RelinkAction.WorkingDirectory    = LinkAction.WorkingDirectory;
					RelinkAction.StatusDescription   = LinkAction.StatusDescription;
					RelinkAction.CommandDescription  = "Relink";
					RelinkAction.bCanExecuteRemotely = false;
					RelinkAction.ProducedItems.Clear();
					RelinkAction.PrerequisiteItems = new List<FileItem>(LinkAction.PrerequisiteItems);

					foreach (FileItem Dependency in EngineAndGameLibrariesFiles)
					{
						RelinkAction.PrerequisiteItems.Add(Dependency);
					}

					RelinkAction.PrerequisiteItems.Add(LinkOutputFile); // also depend on the first link action's output

					// cannot use the real product because we need to maintain the timestamp on it
					FileReference RelinkActionDummyProductRef = FileReference.Combine(LinkEnvironment.LocalShadowDirectory, LinkEnvironment.OutputFilePath.GetFileNameWithoutExtension() + ".relinked_action_ran");
					RelinkAction.ProducedItems.Add(FileItem.GetItemByFileReference(RelinkActionDummyProductRef));

					string EngineAndGameLibrariesString = "";
					foreach (string Library in EngineAndGameLibrariesLinkFlags)
					{
						EngineAndGameLibrariesString += Library;
					}

					// create the relinking step
					string RelinkScriptName     = string.Format((bUseCmdExe ? "Relink-{0}.bat" : "Relink-{0}.sh"), LinkOutputFile.FileDirectory.GetFileName());
					string RelinkScriptFullPath = Path.Combine(LinkEnvironment.LocalShadowDirectory.FullName, RelinkScriptName);

					Log.TraceVerbose("Creating script: {0}", RelinkScriptFullPath);
					Directory.CreateDirectory(Path.GetDirectoryName(RelinkScriptFullPath));

					using (StreamWriter RelinkWriter = File.CreateText(RelinkScriptFullPath))
					{
                        string RelinkInvocation = LinkCommandString;

                        RelinkInvocation = RelinkInvocation.Replace("-Wl,--allow-shlib-undefined", EngineAndGameLibrariesString);

                        string RelinkedFileForwardSlashes = Path.Combine(LinkEnvironment.LocalShadowDirectory.FullName, LinkOutputFile.FileDirectory.GetFileName()) + ".relinked";

                        {
                            string LinkOutputFileForwardSlashes = LinkOutputFile.AbsolutePath.Replace("\\", "/");
                            // should be the same as RelinkedFileRef
                            RelinkInvocation = RelinkInvocation.Replace(LinkOutputFileForwardSlashes, RelinkedFileForwardSlashes);
                            RelinkInvocation = RelinkInvocation.Replace("$", "\\$");
                        }

                        if (bUseCmdExe)
                        {
                            RelinkWriter.Write("@echo off\n");
                            RelinkWriter.Write("rem Automatically generated by BuildTool\n");
                            RelinkWriter.Write("rem *DO NOT EDIT*\n\n");
                            RelinkWriter.Write("set Retries=0\n");
                            RelinkWriter.Write(":relinkloop\n");
                            RelinkWriter.Write("if %Retries% GEQ 10 goto failedtorelink\n");
                            RelinkWriter.Write(RelinkInvocation + "\n");
                            RelinkWriter.Write("if %errorlevel% neq 0 goto sleepandretry\n");
                            RelinkWriter.Write("copy /B \"{0}\" \"{1}.temp\" >NUL 2>NUL\n", RelinkedFileForwardSlashes, LinkOutputFile.AbsolutePath);
                            RelinkWriter.Write("if %errorlevel% neq 0 goto sleepandretry\n");
                            RelinkWriter.Write("move /Y \"{0}.temp\" \"{1}\" >NUL 2>NUL\n", LinkOutputFile.AbsolutePath, LinkOutputFile.AbsolutePath);
                            RelinkWriter.Write("if %errorlevel% neq 0 goto sleepandretry\n");
                            RelinkWriter.Write(GetDumpEncodeDebugCommand(LinkEnvironment, LinkOutputFile) + "\n");
                            RelinkWriter.Write(string.Format("echo \"Dummy\" >> \"{0}\" && copy /b \"{0}\" +,,\n", RelinkActionDummyProductRef.FullName));
                            RelinkWriter.Write("echo Relinked {0} successfully after %Retries% retries\n", LinkOutputFile.AbsolutePath);
                            RelinkWriter.Write("exit 0\n");
                            RelinkWriter.Write(":sleepandretry\n");
                            RelinkWriter.Write("ping 127.0.0.1 -n 1 -w 5000 >NUL 2>NUL\n");     // timeout complains about lack of redirection
                            RelinkWriter.Write("set /a Retries+=1\n");
                            RelinkWriter.Write("goto relinkloop\n");
                            RelinkWriter.Write(":failedtorelink\n");
                            RelinkWriter.Write("echo Failed to relink {0} after %Retries% retries\n", LinkOutputFile.AbsolutePath);
                            RelinkWriter.Write("exit 1\n");
                        }
                        else
                        {
                            RelinkWriter.Write("#!/bin/sh\n");
                            RelinkWriter.Write("# Automatically generated by BuildTool\n");
                            RelinkWriter.Write("# *DO NOT EDIT*\n\n");
                            RelinkWriter.Write("set -o errexit\n");
                            RelinkWriter.Write(RelinkInvocation + "\n");
                            RelinkWriter.Write(string.Format("TIMESTAMP=`stat --format %y \"{0}\"`\n", LinkOutputFile.AbsolutePath));
                            RelinkWriter.Write("cp \"{0}\" \"{1}.temp\"\n", RelinkedFileForwardSlashes, LinkOutputFile.AbsolutePath);
                            RelinkWriter.Write("mv \"{0}.temp\" \"{1}\"\n", LinkOutputFile.AbsolutePath, LinkOutputFile.AbsolutePath);
                            RelinkWriter.Write(GetDumpEncodeDebugCommand(LinkEnvironment, LinkOutputFile) + "\n");
                            RelinkWriter.Write(string.Format("touch -d \"$TIMESTAMP\" \"{0}\"\n\n", LinkOutputFile.AbsolutePath));
                            RelinkWriter.Write(string.Format("echo \"Dummy\" >> \"{0}\"", RelinkActionDummyProductRef.FullName));
                        }
                    }

                    RelinkAction.CommandPath      = ShellBinary;
                    RelinkAction.CommandArguments = ExecuteSwitch + " \"" + RelinkScriptFullPath + "\"";
                }
            }

            return LinkOutputFile;
        }

        public override void SetupBundleDependencies(List<BuildBinary> Binaries, string GameName)
		{
			if (bUseFixdeps)
			{
				foreach (BuildBinary Binary in Binaries)
				{
					AllBinaries.Add(FileItem.GetItemByFileReference(Binary.OutputFilePath));
				}
			}
		}

		public override ICollection<FileItem> PostBuild(FileItem Executable, LinkEnvironment BinaryLinkEnvironment, IActionGraphBuilder Graph)
		{
			ICollection<FileItem> OutputFiles = base.PostBuild(Executable, BinaryLinkEnvironment, Graph);

			if (bUseFixdeps)
			{
				if (BinaryLinkEnvironment.bIsBuildingDLL || 
					BinaryLinkEnvironment.bIsBuildingLibrary)
				{
					return OutputFiles;
				}

				FileItem FixDepsOutputFile = FixDependencies(BinaryLinkEnvironment, Executable, Graph);
				if (FixDepsOutputFile != null)
				{
					OutputFiles.Add(FixDepsOutputFile);
				}
			}
			else
			{
				// make build product of cross-referenced DSOs to be *.relinked_action_ran, so the relinking steps are executed
				if (BinaryLinkEnvironment.bIsBuildingDLL && BinaryLinkEnvironment.bIsCrossReferenced)
				{
					FileReference RelinkedMapRef = FileReference.Combine(BinaryLinkEnvironment.LocalShadowDirectory, BinaryLinkEnvironment.OutputFilePath.GetFileNameWithoutExtension() + ".relinked_action_ran");
					OutputFiles.Add(FileItem.GetItemByFileReference(RelinkedMapRef));
				}
			}
			return OutputFiles;
		}

		public void StripSymbols(FileReference SourceFile, FileReference TargetFile)
		{
			if (SourceFile != TargetFile)
			{
				// Strip command only works in place so we need to copy original if target is different
				File.Copy(SourceFile.FullName, TargetFile.FullName, true);
			}

			ProcessStartInfo StartInfo = new ProcessStartInfo
			{
				FileName = StripPath,
				Arguments = "--strip-debug \"" + TargetFile.FullName + "\"",
				UseShellExecute = false,
				CreateNoWindow = true
			};

			StringUtils.RunLocalProcessAndLogOutput(StartInfo);
		}
	}
}
