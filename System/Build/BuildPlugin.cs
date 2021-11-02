using System.Collections.Generic;
using BuildToolUtilities;

namespace BuildTool
{
	// Stores information about a plugin that is being built for a target
	internal sealed class BuildPlugin
	{
		public PluginInfo Info; // Information about the plugin
		public List<BuildModuleCPP> Modules = new List<BuildModuleCPP>(); // Modules that this plugin belongs to
		public HashSet<BuildPlugin> Dependencies; // Recursive

		// Whether the descriptor for this plugin is needed at runtime;
		// because it has modules or content which is used, or because it references another module that does.
		public bool bDescriptorNeededAtRuntime;

		// Whether this descriptor is referenced non-optionally by something else; a project file or other plugin.
		// This is recursively applied to the plugin's references.
		public bool bDescriptorReferencedExplicitly;

		public BuildPlugin(PluginInfo StaticPluinInfo) => this.Info = StaticPluinInfo;

		public string Name => Info.Name;
		public FileReference File => Info.File;
		public List<FileReference> ChildFiles => Info.ChildFiles;
		public PluginType Type => Info.Type;
		public DirectoryReference Directory => Info.RootDirectory;
		public PluginDescriptor Descriptor => Info.Descriptor;
		public override string ToString() => Info.Name;
	}
}
