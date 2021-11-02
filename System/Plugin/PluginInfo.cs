using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using BuildToolUtilities;

namespace BuildTool
{
	public enum PluginLoadedFrom
	{
		Engine, // Plugin is built-in to the engine
		Project // Project-specific plugin, stored within a game project directory
	}

	// Where a plugin was loaded from. 
	// The order of this enum is important; in the case of name collisions, larger-valued types will take precedence. 
	// Plugins of the same type may not be duplicated.
	public enum PluginType
	{
		Engine,     // Plugin is built-in to the engine
		Enterprise, // Enterprise plugin
		Project,    // Project-specific plugin, stored within a game project directory

		// Plugin found in an external directory
		// (found in an AdditionalPluginDirectory listed in the project file, or referenced on the command line)
		External,
		Mod, // Project-specific mod plugin
	}

	// Information about a single plugin
	[DebuggerDisplay("\\{{File}\\}")]
	public class PluginInfo
	{
		public readonly string             Name; // Plugin name
		public readonly FileReference      File; // Path to the plugin
		public readonly DirectoryReference RootDirectory; // Path to the plugin's root directory
		public List<FileReference>         ChildFiles = new List<FileReference>(); // These can be added to this plugin (platform extensions)
		public PluginDescriptor            Descriptor; // The plugin descriptor
		public PluginType                  Type;

		public PluginInfo(FileReference InFile, PluginType InPluginType)
		{
			Name = Path.GetFileNameWithoutExtension(InFile.FullName);
			File = InFile;
			RootDirectory = File.Directory;
			Descriptor = PluginDescriptor.FromFile(File);
			Type = InPluginType;
		}

		// Determines whether the plugin should be enabled by default
		public bool IsEnabledByDefault(bool bAllowEnginePluginsEnabledByDefault)
		{
			if (Descriptor.bEnabledByDefault.HasValue)
			{
				if (Descriptor.bEnabledByDefault.Value)
				{
					return ((LoadedFrom == PluginLoadedFrom.Project) || bAllowEnginePluginsEnabledByDefault);
				}
				else
				{
					return false;
				}
			}
			else
			{
				return (LoadedFrom == PluginLoadedFrom.Project);
			}
		}

		// Determines where the plugin was loaded from
		public PluginLoadedFrom LoadedFrom
		{
			get
			{
				switch (Type)
				{
					case PluginType.Engine:
					case PluginType.Enterprise:
						return PluginLoadedFrom.Engine;
					default:
						return PluginLoadedFrom.Project;
				}
			}
		}
	}
}
