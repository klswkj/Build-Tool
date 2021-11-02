using BuildToolUtilities;

namespace BuildTool
{
	// Public Mac functions exposed to Automation tool.
	public class MacExports
	{
		// Strips symbols from a file
		public static void StripSymbols(FileReference SourceFile, FileReference TargetFile)
		{
			MacToolChain ToolChain = new MacToolChain(null, MacToolChainOptions.None);
			ToolChain.StripSymbols(SourceFile, TargetFile);
		}
	}
}
