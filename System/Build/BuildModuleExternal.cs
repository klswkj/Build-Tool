using BuildToolUtilities;

namespace BuildTool
{
	// A module that is never compiled by us, and is only used to group include paths and libraries into a dependency unit.
	internal sealed class BuildModuleExternal : BuildModule
	{
		public BuildModuleExternal(ModuleRules Rules, DirectoryReference IntermediateDirectory)
			: base(Rules, IntermediateDirectory)
		{
		}
	}
}
