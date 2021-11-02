using BuildToolUtilities;

namespace BuildTool
{
	// Represents a Mac/IOS framework
	internal sealed class BuildFramework
	{
		public readonly string             FrameworkName;      // The name of this framework
		public readonly FileReference      ZipFile;            // Path to a zip file containing the framework. May be null.
		public readonly DirectoryReference OutputDirectory;    // Path to the extracted framework directory.
		public readonly string             CopyBundledAssets;
#if IOS
		public FileItem                    ExtractedTokenFile; // For IOSToolChain. File created after the framework has been extracted. Used to add dependencies into the action graph.
#endif
		public BuildFramework(string Name, string CopyBundledAssets = null)
		{
			this.FrameworkName     = Name;
			this.CopyBundledAssets = CopyBundledAssets;
		}

		public BuildFramework(string Name, FileReference ZipFile, DirectoryReference OutputDirectoryForExtractedZipFile, string CopyBundledAssets)
		{
			this.FrameworkName     = Name;
			this.ZipFile           = ZipFile;
			this.OutputDirectory   = OutputDirectoryForExtractedZipFile;
			this.CopyBundledAssets = CopyBundledAssets;
		}
	}
}
