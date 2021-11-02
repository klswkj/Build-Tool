using System;
using System.Collections.Generic;
using BuildToolUtilities;

namespace BuildTool
{
	// Public HoloLensDeploy wrapper exposed to UAT
	public class HoloLensExports
	{
		private readonly HoloLensDeploy InnerDeploy;

		public HoloLensExports()
		{
			InnerDeploy = new HoloLensDeploy();
		}

		// Collect all the WinMD references
		public void AddWinMDReferencesFromReceipt(TargetReceipt Receipt, DirectoryReference SourceProjectDir, string DestPackageRoot)
		{
			InnerDeploy.AddWinMDReferencesFromReceipt(Receipt, SourceProjectDir, DestPackageRoot);
		}

		public static FileReference GetWindowsSdkToolPath(string ToolName)
		{
			return HoloLensToolChain.GetWindowsSdkToolPath(ToolName);
		}

		public static bool InitWindowsSdkToolPath(string SdkVersion)
		{
			return HoloLensToolChain.InitWindowsSdkToolPath(SdkVersion);
		}

		public static void CreateManifestForDLC(FileReference DLCFile, DirectoryReference OutputDirectory)
		{
			string IntermediateDirectory = DirectoryReference.Combine(DLCFile.Directory, "Intermediate", "Deploy").FullName;
			new HoloLensManifestGenerator().CreateManifest(BuildTargetPlatform.HoloLens, WindowsArchitecture.ARM64, OutputDirectory.FullName, IntermediateDirectory, DLCFile, DLCFile.Directory.FullName, new List<TargetConfiguration>(), new List<string>(), null);
		}

		public static Version GetCurrentWindowsSdkVersion()
		{
			return HoloLensToolChain.GetCurrentWindowsSdkVersion();
		}
	}
}
