using System;
using System.Collections.Generic;
using System.Linq;
using BuildToolUtilities;

namespace BuildTool
{
	// Public Linux functions exposed to UAT
	public static class WindowsExports
	{
		// Tries to get the directory for an installed Visual Studio version
		public static bool TryGetVSInstallDir(WindowsCompiler Compiler, out DirectoryReference InstallDir)
		{
			return WindowsPlatform.TryGetVSInstallDir(Compiler, out InstallDir);
		}

		// Gets the path to MSBuild.exe
		public static string GetMSBuildEXEPath() => WindowsPlatform.GetMsBuildEXEPath().FullName;

		// Returns the common name of the current architecture
		public static string GetArchitectureSubpath(WindowsArchitecture InWindowsArchitecture) 
			=> WindowsPlatform.GetArchitectureSubpath(InWindowsArchitecture);


		// Tries to get the directory for an installed Windows SDK
		public static bool TryGetWindowsSdkDir(string DesiredVersion, out Version OutSdkVersion, out DirectoryReference OutSdkDir)
		{
			if (WindowsPlatform.TryGetWindowsSdkDir(DesiredVersion, out VersionNumber NumVersion, out OutSdkDir))
			{
				OutSdkVersion = new Version(NumVersion.ToString());
				return true;
			}
			else
			{
				OutSdkVersion = null;
				return false;
			}
		}

		// Gets a list of Windows Sdk installation directories, ordered by preference
		public static List<KeyValuePair<string, DirectoryReference>> GetWindowsSdkDirs()
		{
			List<KeyValuePair<string, DirectoryReference>> OutWindowsSdkDirs = new List<KeyValuePair<string, DirectoryReference>>();

			// Add the default directory first
			if (WindowsPlatform.TryGetWindowsSdkDir(null, out VersionNumber Version, out DirectoryReference DefaultWindowsSdkDir))
			{
				OutWindowsSdkDirs.Add(new KeyValuePair<string, DirectoryReference>(Version.ToString(), DefaultWindowsSdkDir));
			}

			// Add all the other directories sorted in reverse order
			IReadOnlyDictionary<VersionNumber, DirectoryReference> WindowsSdkDirPairs = WindowsPlatform.FindWindowsSdkDirs();
			foreach(KeyValuePair<VersionNumber, DirectoryReference> Pair in WindowsSdkDirPairs.OrderByDescending(x => x.Key))
			{
				if(!OutWindowsSdkDirs.Any(x => x.Value == Pair.Value))
				{
					OutWindowsSdkDirs.Add(new KeyValuePair<string, DirectoryReference>(Pair.Key.ToString(), Pair.Value));
				}
			}

			return OutWindowsSdkDirs;
		}
	}
}
