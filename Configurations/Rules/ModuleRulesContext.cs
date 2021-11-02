using BuildToolUtilities;

namespace BuildTool
{
	// Stores information about where a module rules object came from, and how it can be used. 
	class ModuleRulesContext
	{
		public RulesScope Scope;                        // The scope for this module. Used to validate references to other modules.
		public DirectoryReference DefaultOutputBaseDir; // The default directory for output files
		public PluginInfo ModulePluginInfo;             // The plugin that this module belongs to
		public bool bCanHotReload;                      // Whether this module should be included in the default hot reload set
		public bool bCanBuildDebugGame;                 // Whether this module should be compiled with optimization disabled in DebugGame configurations (ie. whether it's a game module).
		public bool bCanUseForSharedPCH;
		public bool bClassifyAsGameModuleForUHT;        // Whether to treat this module as a game module for UHT ordering
		public UHTModuleType? DefaultUHTModuleType;     // Do not use this for inferring other things about the module.

		public ModuleRulesContext(RulesScope Scope, DirectoryReference DefaultOutputBaseDir)
		{
			this.Scope = Scope;
			this.DefaultOutputBaseDir = DefaultOutputBaseDir;
			this.bCanUseForSharedPCH = true;
		}

		public ModuleRulesContext(ModuleRulesContext Other)
		{
			this.Scope = Other.Scope;
			this.DefaultOutputBaseDir = Other.DefaultOutputBaseDir;
			this.ModulePluginInfo = Other.ModulePluginInfo;
			this.bCanHotReload = Other.bCanHotReload;
			this.bCanBuildDebugGame = Other.bCanBuildDebugGame;
			this.bCanUseForSharedPCH = Other.bCanUseForSharedPCH;
			this.bClassifyAsGameModuleForUHT = Other.bClassifyAsGameModuleForUHT;
			this.DefaultUHTModuleType = Other.DefaultUHTModuleType;
		}
	}
}
