using System;
using System.Collections.Generic;
using System.Linq;
using BuildToolUtilities;

namespace BuildTool
{
	// Stores information about a Visual C++ installation and compile environment
	class VCEnvironment
	{
		public readonly WindowsCompiler Compiler;
		public readonly DirectoryReference CompilerDir;
		public readonly VersionNumber CompilerVersion;
		public readonly WindowsArchitecture CompilerArchitecture;

		// The underlying toolchain to use.
		// Using Clang/ICL will piggy-back on a Visual Studio toolchain for the CRT, linker, etc...
		public readonly WindowsCompiler ToolChain;
		public readonly DirectoryReference ToolChainDir;
		public readonly VersionNumber ToolChainVersion;
		
		// Root directory containing the Windows Sdk
		public readonly DirectoryReference WindowsSdkDir;

		// Version number of the Windows Sdk
		public readonly VersionNumber WindowsSdkVersion;

		public readonly FileReference CompilerPath; // The path to the linker for linking executables
		public readonly FileReference LinkerPath; // The path to the linker for linking executables
		public readonly FileReference LibraryManagerPath; // The path to the linker for linking libraries
		public readonly FileReference ResourceCompilerPath; // Path to the resource compiler from the Windows SDK

		public readonly List<DirectoryReference> IncludePaths = new List<DirectoryReference>();
		public readonly List<DirectoryReference> LibraryPaths = new List<DirectoryReference>();

		public VCEnvironment
		(
			BuildTargetPlatform Platform,         // The platform to find the compiler for
			WindowsCompiler      Compiler,         // The compiler to use
			DirectoryReference   CompilerDir,      // The compiler directory
			VersionNumber        CompilerVersion,  // The compiler version number
			WindowsArchitecture  Architecture,     // The compiler Architecture
			WindowsCompiler      ToolChain,        // The Base Toolchain Version
			DirectoryReference   ToolChainDir,     // Directory containing the toolchain
			VersionNumber        ToolChainVersion, // Version of the toolchain
			DirectoryReference   WindowsSdkDir,    // Root directory containing the Windows SDK 
			VersionNumber        WindowsSdkVersion // Version of the Windows SDK 
		)
		{
			this.Compiler          = Compiler;
			this.CompilerDir       = CompilerDir;
			this.CompilerVersion   = CompilerVersion;
			this.CompilerArchitecture = Architecture;
			this.ToolChain         = ToolChain;
			this.ToolChainDir      = ToolChainDir;
			this.ToolChainVersion  = ToolChainVersion;
			this.WindowsSdkDir     = WindowsSdkDir;
			this.WindowsSdkVersion = WindowsSdkVersion;

			// Get the standard VC paths
			DirectoryReference VCToolPath = GetVCToolPath(ToolChain, ToolChainDir, Architecture);
			// Regardless of the target, if we're linking on a 64 bit machine,
			// we want to use the 64 bit linker (it's faster than the 32 bit linker and can handle large linking jobs)
			DirectoryReference DefaultLinkerDir = VCToolPath;

			// Compile using 64 bit tools for 64 bit targets, and 32 for 32.
			CompilerPath       = GetCompilerToolPath(Platform, Compiler, Architecture, CompilerDir);
			LinkerPath         = GetLinkerToolPath(Platform, Compiler, DefaultLinkerDir);
			LibraryManagerPath = GetLibraryLinkerToolPath(Platform, Compiler, DefaultLinkerDir);

			// Get the resource compiler path from the Windows SDK
			ResourceCompilerPath = GetResourceCompilerToolPath(Platform, WindowsSdkDir, WindowsSdkVersion);

			// Get all the system include paths
			SetupEnvironment(Platform);
		}

		// Updates environment variables needed for running with this toolchain
		public void SetEnvironmentVariables()
		{
			// Add the compiler path and directory as environment variables for the process so they may be used elsewhere.
			Environment.SetEnvironmentVariable("VC_COMPILER_PATH", CompilerPath.FullName, EnvironmentVariableTarget.Process);
			Environment.SetEnvironmentVariable("VC_COMPILER_DIR", CompilerPath.Directory.FullName, EnvironmentVariableTarget.Process);

			// Add both toolchain paths to the PATH environment variable.
			// There are some support DLLs which are only added to one of the paths,
			// but which the toolchain in the other directory needs to run (eg. mspdbcore.dll).
			if (CompilerArchitecture == WindowsArchitecture.x64)
			{
				AddDirectoryToPath(GetVCToolPath(ToolChain, ToolChainDir, WindowsArchitecture.x64));
				AddDirectoryToPath(GetVCToolPath(ToolChain, ToolChainDir, WindowsArchitecture.x86));
			}
			else if (CompilerArchitecture == WindowsArchitecture.x86)
			{
				AddDirectoryToPath(GetVCToolPath(ToolChain, ToolChainDir, WindowsArchitecture.x86));
				AddDirectoryToPath(GetVCToolPath(ToolChain, ToolChainDir, WindowsArchitecture.x64));
			}
			else if (CompilerArchitecture == WindowsArchitecture.ARM64)
			{
				AddDirectoryToPath(GetVCToolPath(ToolChain, ToolChainDir, WindowsArchitecture.ARM64));
				AddDirectoryToPath(GetVCToolPath(ToolChain, ToolChainDir, WindowsArchitecture.x86));
				AddDirectoryToPath(GetVCToolPath(ToolChain, ToolChainDir, WindowsArchitecture.x64));
			}

			// Add the Windows SDK directory to the path too, for mt.exe.
			if (new VersionNumber(10) <= WindowsSdkVersion)
			{
				AddDirectoryToPath(DirectoryReference.Combine(WindowsSdkDir, "bin", WindowsSdkVersion.ToString(), CompilerArchitecture.ToString()));
			}
		}

		// Add a directory to the PATH environment variable
		static void AddDirectoryToPath(DirectoryReference ToolPath)
		{
            string PathEnvironmentVariable = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!PathEnvironmentVariable.Split(';').Any(x => String.Compare(x, ToolPath.FullName, true) == 0))
            {
                PathEnvironmentVariable = ToolPath.FullName + ";" + PathEnvironmentVariable;
                Environment.SetEnvironmentVariable("PATH", PathEnvironmentVariable);
            }
		}

		// Gets the path to the tool binaries.
		private static DirectoryReference GetVCToolPath
		(
			WindowsCompiler     Compiler,
			DirectoryReference  VCToolChainDir,
			WindowsArchitecture Architecture
		)
		{
			if (Compiler >= WindowsCompiler.VisualStudio2017)
			{
				FileReference NativeCompilerPath 
					= FileReference.Combine
					(
						VCToolChainDir,
						"bin",
						"HostX64",
						WindowsExports.GetArchitectureSubpath(Architecture),
						"cl.exe"
					);
				if (FileReference.Exists(NativeCompilerPath))
				{
					return NativeCompilerPath.Directory;
				}

				FileReference CrossCompilerPath 
					= FileReference.Combine
					(
						VCToolChainDir,
						"bin",
						"HostX86",
						WindowsExports.GetArchitectureSubpath(Architecture),
						"cl.exe"
					);

				if (FileReference.Exists(CrossCompilerPath))
				{
					return CrossCompilerPath.Directory;
				}
			}
			else
			{
				if (Architecture == WindowsArchitecture.x86)
				{
					FileReference CompilerPath = FileReference.Combine(VCToolChainDir, "bin", "cl.exe");
					if(FileReference.Exists(CompilerPath))
					{
						return CompilerPath.Directory;
					}
				}
				else if (Architecture == WindowsArchitecture.x64)
				{
					// Use the native 64-bit compiler if present
					FileReference NativeCompilerPath = FileReference.Combine(VCToolChainDir, "bin", "amd64", "cl.exe");
					if (FileReference.Exists(NativeCompilerPath))
					{
						return NativeCompilerPath.Directory;
					}

					// Otherwise use the amd64-on-x86 compiler. VS2012 Express only includes the latter.
					FileReference CrossCompilerPath = FileReference.Combine(VCToolChainDir, "bin", "x86_amd64", "cl.exe");
					if (FileReference.Exists(CrossCompilerPath))
					{
						return CrossCompilerPath.Directory;
					}
				}
				else if (Architecture == WindowsArchitecture.ARM32)
				{
					// Use the native 64-bit compiler if present
					FileReference NativeCompilerPath = FileReference.Combine(VCToolChainDir, "bin", "amd64_arm", "cl.exe");
					if (FileReference.Exists(NativeCompilerPath))
					{
						return NativeCompilerPath.Directory;
					}

					// Otherwise use the amd64-on-x86 compiler. VS2012 Express only includes the latter.
					FileReference CrossCompilerPath = FileReference.Combine(VCToolChainDir, "bin", "x86_arm", "cl.exe");
					if (FileReference.Exists(CrossCompilerPath))
					{
						return CrossCompilerPath.Directory;
					}
				}
			}

			throw new BuildException("No required compiler toolchain found in {0}", VCToolChainDir);
		}

		// Gets the path to the compiler.
		static FileReference GetCompilerToolPath
		(
			BuildTargetPlatform Platform,
			WindowsCompiler Compiler,
			WindowsArchitecture Architecture,
			DirectoryReference CompilerDir
		)
		{
			if (Compiler == WindowsCompiler.Clang)
			{
				return FileReference.Combine(CompilerDir, "bin", "clang-cl.exe");
			}
			else if(Compiler == WindowsCompiler.Intel)
			{
				if(Platform == BuildTargetPlatform.Win32)
				{
					return FileReference.Combine(CompilerDir, "bin", "ia32", "icl.exe");
				}
				else
				{
					return FileReference.Combine(CompilerDir, "bin", "intel64", "icl.exe");
				}
			}
			else
			{
				return FileReference.Combine(GetVCToolPath(Compiler, CompilerDir, Architecture), "cl.exe");
			}
		}

		// Gets the path to the linker.
		private static FileReference GetLinkerToolPath
		(
			BuildTargetPlatform Platform,
			WindowsCompiler Compiler,
			DirectoryReference DefaultLinkerDir
		)
		{
			// If we were asked to use Clang, then we'll redirect the path to the compiler to the LLVM installation directory
			if (Compiler == WindowsCompiler.Clang && WindowsPlatform.bAllowClangLinker)
			{
				FileReference LinkerPath 
					= FileReference.Combine
					(
						DirectoryReference.GetSpecialFolder(Environment.SpecialFolder.ProgramFiles),
						"LLVM",
						"bin",
						"lld-link.exe"
					);
				if (FileReference.Exists(LinkerPath))
				{
					return LinkerPath;
				}

				FileReference LinkerPathX86 
					= FileReference.Combine
					(
						DirectoryReference.GetSpecialFolder(Environment.SpecialFolder.ProgramFilesX86),
						"LLVM",
						"bin",
						"lld-link.exe"
					);

				if (FileReference.Exists(LinkerPathX86))
				{
					return LinkerPathX86;
				}

				throw new BuildException("Clang was selected as the Windows compiler, but {0} and {1} were not found.", LinkerPath, LinkerPathX86);
			}
			else if(Compiler == WindowsCompiler.Intel && WindowsPlatform.bAllowICLLinker)
			{
				FileReference LinkerPath = FileReference.Combine(DirectoryReference.GetSpecialFolder(Environment.SpecialFolder.ProgramFilesX86), "IntelSWTools", "compilers_and_libraries", "windows", "bin", (Platform == BuildTargetPlatform.Win32)? "ia32" : "intel64", "xilink.exe");
				if (FileReference.Exists(LinkerPath))
				{
					return LinkerPath;
				}

				throw new BuildException("ICL was selected as the Windows compiler, but {0} was not found.", LinkerPath);
			}
			else
			{
				return FileReference.Combine(DefaultLinkerDir, "link.exe");
			}
		}

		// Gets the path to the library linker.
		private static FileReference GetLibraryLinkerToolPath(BuildTargetPlatform Platform, WindowsCompiler Compiler, DirectoryReference DefaultLinkerDir)
		{
			// Regardless of the target, if we're linking on a 64 bit machine, we want to use the 64 bit linker (it's faster than the 32 bit linker)
			if (Compiler == WindowsCompiler.Intel && WindowsPlatform.bAllowICLLinker)
			{
				FileReference LibPath = FileReference.Combine(DirectoryReference.GetSpecialFolder(Environment.SpecialFolder.ProgramFilesX86), "IntelSWTools", "compilers_and_libraries", "windows", "bin", Platform == BuildTargetPlatform.Win32 ? "ia32" : "intel64", "xilib.exe");
				if (FileReference.Exists(LibPath))
				{
					return LibPath;
				}

				throw new BuildException("ICL was selected as the Windows compiler, but does not appear to be installed.  Could not find: " + LibPath);
			}
			else
			{
				return FileReference.Combine(DefaultLinkerDir, "lib.exe");
			}
		}

		// Gets the path to the resource compiler's rc.exe for the specified platform.
		virtual protected FileReference GetResourceCompilerToolPath(BuildTargetPlatform Platform, DirectoryReference WindowsSdkDir, VersionNumber WindowsSdkVersion)
		{
			// 64 bit -- we can use the 32 bit version to target 64 bit on 32 bit OS.
			if (Platform != BuildTargetPlatform.Win32)
			{
				FileReference ResourceCompilerPath = FileReference.Combine(WindowsSdkDir, "bin", WindowsSdkVersion.ToString(), "x64", "rc.exe");
				if(FileReference.Exists(ResourceCompilerPath))
				{
					return ResourceCompilerPath;
				}

				ResourceCompilerPath = FileReference.Combine(WindowsSdkDir, "bin", "x64", "rc.exe");
				if(FileReference.Exists(ResourceCompilerPath))
				{
					return ResourceCompilerPath;
				}
			}
			else
			{
				FileReference ResourceCompilerPath = FileReference.Combine(WindowsSdkDir, "bin", WindowsSdkVersion.ToString(), "x86", "rc.exe");
				if(FileReference.Exists(ResourceCompilerPath))
				{
					return ResourceCompilerPath;
				}

				ResourceCompilerPath = FileReference.Combine(WindowsSdkDir, "bin", "x86", "rc.exe");
				if(FileReference.Exists(ResourceCompilerPath))
				{
					return ResourceCompilerPath;
				}
			}
			throw new BuildException("Unable to find path to the Windows resource compiler under {0} (version {1})", WindowsSdkDir, WindowsSdkVersion);
		}

		// Sets up the standard compile environment for the toolchain
		private void SetupEnvironment(BuildTargetPlatform Platform)
		{
			// Add the standard Visual C++ include paths
			IncludePaths.Add(DirectoryReference.Combine(ToolChainDir, "INCLUDE"));
			string ArchFolder = WindowsExports.GetArchitectureSubpath(CompilerArchitecture);

			// Add the standard Visual C++ library paths
			if (ToolChain >= WindowsCompiler.VisualStudio2017)
			{
				if (Platform == BuildTargetPlatform.HoloLens)
				{
					LibraryPaths.Add(DirectoryReference.Combine(ToolChainDir, "lib", ArchFolder, "store"));
				}
				else
				{
					LibraryPaths.Add(DirectoryReference.Combine(ToolChainDir, "lib", ArchFolder));
				}
			}
			else
			{
				DirectoryReference LibsPath = DirectoryReference.Combine(ToolChainDir, "LIB");
				if (Platform == BuildTargetPlatform.HoloLens)
				{
					LibsPath = DirectoryReference.Combine(LibsPath, "store");
				}

				if (CompilerArchitecture == WindowsArchitecture.x64)
				{
					LibsPath = DirectoryReference.Combine(LibsPath, "amd64");
				}
				else if (CompilerArchitecture == WindowsArchitecture.ARM32)
				{
					LibsPath = DirectoryReference.Combine(LibsPath, "arm");
				}

				LibraryPaths.Add(LibsPath);
			}

			// If we're on Visual Studio 2015 and using pre-Windows 10 SDK, we need to find a Windows 10 SDK and add the UCRT include paths
			if (WindowsCompiler.VisualStudio2015_DEPRECATED <= ToolChain && 
				WindowsSdkVersion < new VersionNumber(10))
			{
				KeyValuePair<VersionNumber, DirectoryReference> Pair 
					= WindowsPlatform.FindUniversalCrtDirs().OrderByDescending(x => x.Key).FirstOrDefault();

				if(Pair.Key == null || Pair.Key < new VersionNumber(10))
				{
					throw new BuildException("{0} requires the Universal CRT to be installed.", WindowsPlatform.GetCompilerName(ToolChain));
				}

				DirectoryReference IncludeRootDir = DirectoryReference.Combine(Pair.Value, "include", Pair.Key.ToString());
				IncludePaths.Add(DirectoryReference.Combine(IncludeRootDir, "ucrt"));

				DirectoryReference LibraryRootDir = DirectoryReference.Combine(Pair.Value, "lib", Pair.Key.ToString());
				LibraryPaths.Add(DirectoryReference.Combine(LibraryRootDir, "ucrt", ArchFolder));
			}

			// Add the NETFXSDK include path. We need this for SwarmInterface.
			if (WindowsPlatform.TryGetNetFxSdkInstallDir(out DirectoryReference NetFxSdkDir))
			{
				IncludePaths.Add(DirectoryReference.Combine(NetFxSdkDir, "include", "um"));
				LibraryPaths.Add(DirectoryReference.Combine(NetFxSdkDir, "lib", "um", ArchFolder));
			}
			else
			{
				throw new BuildException("Could not find NetFxSDK install dir; this will prevent SwarmInterface from installing.  Install a version of .NET Framework SDK at 4.6.0 or higher.");
			}

			// Add the Windows SDK paths
			if (WindowsSdkVersion >= new VersionNumber(10))
			{
				DirectoryReference IncludeRootDir = DirectoryReference.Combine(WindowsSdkDir, "include", WindowsSdkVersion.ToString());
				IncludePaths.Add(DirectoryReference.Combine(IncludeRootDir, "ucrt"));
				IncludePaths.Add(DirectoryReference.Combine(IncludeRootDir, "shared"));
				IncludePaths.Add(DirectoryReference.Combine(IncludeRootDir, "um"));
				IncludePaths.Add(DirectoryReference.Combine(IncludeRootDir, "winrt"));

				DirectoryReference LibraryRootDir = DirectoryReference.Combine(WindowsSdkDir, "lib", WindowsSdkVersion.ToString());
				LibraryPaths.Add(DirectoryReference.Combine(LibraryRootDir, "ucrt", ArchFolder));
				LibraryPaths.Add(DirectoryReference.Combine(LibraryRootDir, "um", ArchFolder));
			}
			else
			{
				DirectoryReference IncludeRootDir = DirectoryReference.Combine(WindowsSdkDir, "include");
				IncludePaths.Add(DirectoryReference.Combine(IncludeRootDir, "shared"));
				IncludePaths.Add(DirectoryReference.Combine(IncludeRootDir, "um"));
				IncludePaths.Add(DirectoryReference.Combine(IncludeRootDir, "winrt"));

				DirectoryReference LibraryRootDir = DirectoryReference.Combine(WindowsSdkDir, "lib", "winv6.3");
				LibraryPaths.Add(DirectoryReference.Combine(LibraryRootDir, "um", ArchFolder));
			}
		}

		
		// Creates an environment with the given settings
		
		// <param name="Compiler">The compiler version to use</param>
		// <param name="Platform">The platform to target</param>
		// <param name="Architecture">The Architecture to target</param>
		// <param name="CompilerVersion">The specific toolchain version to use</param>
		// <param name="WindowsSdkVersion">Version of the Windows SDK to use</param>
		// <param name="SuppliedSdkDirectoryForVersion">If specified, this is the SDK directory to use, otherwise, attempt to look up via registry. If specified, the WindowsSdkVersion is used directly</param>
		// <returns>New environment object with paths for the given settings</returns>
		public static VCEnvironment Create
		(
			WindowsCompiler Compiler,
			BuildTargetPlatform Platform,
			WindowsArchitecture Architecture,
			string CompilerVersion,
			string WindowsSdkVersion,
			string SuppliedSdkDirectoryForVersion
		)
		{
            // Get the compiler version info
            if (!WindowsPlatform.TryGetToolChainDir(Compiler, CompilerVersion, out VersionNumber SelectedCompilerVersion, out DirectoryReference SelectedCompilerDir))
            {
                throw new BuildException("{0}{1} must be installed in order to build this target.", WindowsPlatform.GetCompilerName(Compiler), String.IsNullOrEmpty(CompilerVersion) ? "" : String.Format(" ({0})", CompilerVersion));
            }

            // Get the toolchain info
            WindowsCompiler    ToolChain;
			VersionNumber      SelectedToolChainVersion;
			DirectoryReference SelectedToolChainDir;

			if(Compiler == WindowsCompiler.Clang || 
				Compiler == WindowsCompiler.Intel)
			{
				if (WindowsPlatform.TryGetToolChainDir(WindowsCompiler.VisualStudio2019, null, out SelectedToolChainVersion, out SelectedToolChainDir))
				{
					ToolChain = WindowsCompiler.VisualStudio2019;
				}
				else if (WindowsPlatform.TryGetToolChainDir(WindowsCompiler.VisualStudio2017, null, out SelectedToolChainVersion, out SelectedToolChainDir))
				{
					ToolChain = WindowsCompiler.VisualStudio2017;
				}
				else
				{
					throw new BuildException("{0} or {1} must be installed in order to build this target.", WindowsPlatform.GetCompilerName(WindowsCompiler.VisualStudio2019), WindowsPlatform.GetCompilerName(WindowsCompiler.VisualStudio2017));
				}
			}
			else
			{
				ToolChain = Compiler;
				SelectedToolChainVersion = SelectedCompilerVersion;
				SelectedToolChainDir = SelectedCompilerDir;
			}

			// Get the actual Windows SDK directory
			VersionNumber SelectedWindowsSdkVersion;
			DirectoryReference SelectedWindowsSdkDir;
			if (SuppliedSdkDirectoryForVersion != null)
			{
				SelectedWindowsSdkDir = new DirectoryReference(SuppliedSdkDirectoryForVersion);
				SelectedWindowsSdkVersion = VersionNumber.Parse(WindowsSdkVersion);

				if (!DirectoryReference.Exists(SelectedWindowsSdkDir))
				{
					throw new BuildException("Windows SDK{0} must be installed at {1}.", String.IsNullOrEmpty(WindowsSdkVersion) ? "" : String.Format(" ({0})", WindowsSdkVersion), SuppliedSdkDirectoryForVersion);
				}
			}
			else
			{
				if (!WindowsPlatform.TryGetWindowsSdkDir(WindowsSdkVersion, out SelectedWindowsSdkVersion, out SelectedWindowsSdkDir))
				{
					throw new BuildException("Windows SDK{0} must be installed in order to build this target.", String.IsNullOrEmpty(WindowsSdkVersion) ? "" : String.Format(" ({0})", WindowsSdkVersion));
				}
			}

			return new VCEnvironment(Platform, Compiler, SelectedCompilerDir, SelectedCompilerVersion, Architecture, ToolChain, SelectedToolChainDir, SelectedToolChainVersion, SelectedWindowsSdkDir, SelectedWindowsSdkVersion);
		}
	}
}
