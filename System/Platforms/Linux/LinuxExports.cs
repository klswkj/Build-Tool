using BuildToolUtilities;

namespace BuildTool
{
	// Public Linux functions exposed to Automation tool.	
	public class LinuxExports
	{
		public static void StripSymbols(FileReference SourceFile, FileReference TargetFile)
		{
			LinuxToolChain ToolChain = new LinuxToolChain(LinuxPlatform.DefaultHostArchitecture, new LinuxPlatformSDK());
			ToolChain.StripSymbols(SourceFile, TargetFile);
		}
	}
}
