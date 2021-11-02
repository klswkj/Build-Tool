using System;
using System.Collections.Generic;
using System.Linq;
using BuildToolUtilities;

namespace BuildTool
{
	// Class for enumerating plugin metadata
	public static class Plugins
	{
		// Cache of plugins under each directory
		private static readonly Dictionary<DirectoryReference, List<PluginInfo>> PluginInfoCache = new Dictionary<DirectoryReference, List<PluginInfo>>();

		// Cache of plugin filenames under each directory
		private static readonly Dictionary<DirectoryReference, List<FileReference>> PluginFileCache = new Dictionary<DirectoryReference, List<FileReference>>();

		// Filters the list of plugins to ensure that any game plugins override engine plugins with the same name,
		// and otherwise that no two plugins with the same name exist.
		public static IEnumerable<PluginInfo> FilterPlugins(IEnumerable<PluginInfo> PluginsToFilter)
		{
			Dictionary<string, PluginInfo> NameToPluginInfo = new Dictionary<string, PluginInfo>(StringComparer.InvariantCultureIgnoreCase);
			foreach(PluginInfo Plugin in PluginsToFilter)
			{
				if (!NameToPluginInfo.TryGetValue(Plugin.Name, out PluginInfo ExistingPluginInfo))
				{
					NameToPluginInfo.Add(Plugin.Name, Plugin);
				}
				else if (ExistingPluginInfo.Type < Plugin.Type)
				{
					NameToPluginInfo[Plugin.Name] = Plugin;
				}
				else if (Plugin.Type == ExistingPluginInfo.Type)
				{
					throw new BuildException(String.Format("Found '{0}' plugin in two locations ({1} and {2}). Plugin names must be unique.", Plugin.Name, ExistingPluginInfo.File, Plugin.File));
				}
			}

			// Filtered list of plugins in the original order.
			return PluginsToFilter.Where(x => NameToPluginInfo[x.Name] == x);
		}

		// Read all the plugins available to a given project
		public static List<PluginInfo> ReadAvailablePlugins
		(
			DirectoryReference EngineDir, 
			DirectoryReference ProjectDir/*May be null*/, 
			List<DirectoryReference> AdditionalDirectoriesToScan
		)
		{
			List<PluginInfo> Plugins = new List<PluginInfo>();

			// Read all the engine plugins
			Plugins.AddRange(ReadEnginePlugins(EngineDir));

			// Read all the project plugins
			if (ProjectDir != null)
			{
				Plugins.AddRange(ReadProjectPlugins(ProjectDir));
			}

            // Scan for shared plugins in project specified additional directories
			if(AdditionalDirectoriesToScan != null)
			{
				foreach (DirectoryReference AdditionalDirectory in AdditionalDirectoriesToScan)
				{
					Plugins.AddRange(ReadPluginsFromDirectory(AdditionalDirectory, "", PluginType.External));
				}
			}

			return Plugins;
		}

		// Enumerates all the plugin files available to the given project
		public static IEnumerable<FileReference> EnumeratePlugins(FileReference ProjectFile)
		{
			DirectoryReference EnginePluginsDir = DirectoryReference.Combine(BuildTool.EngineDirectory, Tag.Directory.Plugins);
			foreach(FileReference PluginFile in EnumeratePlugins(EnginePluginsDir))
			{
				yield return PluginFile;
			}

			DirectoryReference EnterprisePluginsDir = DirectoryReference.Combine(BuildTool.EnterpriseDirectory, Tag.Directory.Plugins);
			foreach(FileReference PluginFile in EnumeratePlugins(EnterprisePluginsDir))
			{
				yield return PluginFile;
			}

			if(ProjectFile != null)
			{
				DirectoryReference ProjectPluginsDir = DirectoryReference.Combine(ProjectFile.Directory, Tag.Directory.Plugins);
				foreach(FileReference PluginFile in EnumeratePlugins(ProjectPluginsDir))
				{
					yield return PluginFile;
				}

				DirectoryReference ProjectModsDir = DirectoryReference.Combine(ProjectFile.Directory, Tag.Directory.Mods);
				foreach(FileReference PluginFile in EnumeratePlugins(ProjectModsDir))
				{
					yield return PluginFile;
				}
			}
		}

		// Read all the plugin descriptors under the given engine directory
		public static IReadOnlyList<PluginInfo> ReadEnginePlugins(DirectoryReference EngineDirectory)
		{
			return ReadPluginsFromDirectory(EngineDirectory, Tag.Directory.Plugins, PluginType.Engine);
		}

		// Read all the plugin descriptors under the given enterprise directory
		public static IReadOnlyList<PluginInfo> ReadEnterprisePlugins(DirectoryReference EnterpriseDirectory)
		{
			return ReadPluginsFromDirectory(EnterpriseDirectory, Tag.Directory.Plugins, PluginType.Enterprise);
		}

		// Read all the plugin descriptors under the given project directory
		public static IReadOnlyList<PluginInfo> ReadProjectPlugins(DirectoryReference ProjectDirectory)
		{
			List<PluginInfo> Plugins = new List<PluginInfo>();
			Plugins.AddRange(ReadPluginsFromDirectory(ProjectDirectory, Tag.Directory.Plugins, PluginType.Project));
			Plugins.AddRange(ReadPluginsFromDirectory(ProjectDirectory, Tag.Directory.Mods, PluginType.Mod));
			return Plugins.AsReadOnly();
		}

		// Read all of the plugins found in the project specified additional plugin directories
		public static IReadOnlyList<PluginInfo> ReadAdditionalPlugins(DirectoryReference AdditionalDirectoryToScan)
		{
			return ReadPluginsFromDirectory(AdditionalDirectoryToScan, "", PluginType.External);
		}

		// Determines whether the given suffix is valid for a child plugin
		private static bool IsValidChildPluginSuffix(string Suffix)
		{
			foreach (BuildPlatformGroup Group in BuildPlatformGroup.GetValidGroups())
			{
				if (Group.ToString().Equals(Suffix, StringComparison.InvariantCultureIgnoreCase))
				{
					return true;
				}
			}

			foreach (BuildTargetPlatform Platform in BuildTargetPlatform.GetValidPlatforms())
			{
				if (Platform.ToString().Equals(Suffix, StringComparison.InvariantCultureIgnoreCase))
				{
					return true;
				}
			}

			return false;
		}

		//  Attempt to merge a child plugin up into a parent plugin (via file naming scheme).
		//  Very little merging happens but it does allow for platform extensions to extend a plugin with module files
		private static void TryMergeWithParent(PluginInfo ChildPluginToMerge, FileReference ChildPluginFilename)
		{
			// find the parent
			PluginInfo Parent = null;

			string[] Tokens = ChildPluginFilename.GetFileNameWithoutAnyExtensions().Split("_".ToCharArray());
			if (Tokens.Length == 2)
			{
				string ParentPluginName = Tokens[0];
				foreach (KeyValuePair<DirectoryReference, List<PluginInfo>> Pair in PluginInfoCache)
				{
					Parent = Pair.Value.FirstOrDefault(x => x.Name.Equals(ParentPluginName, StringComparison.InvariantCultureIgnoreCase));
					if (Parent != null)
					{
						break;
					}
				}
			}

			// did we find a parent plugin?
			if (Parent == null)
			{
				throw new BuildException("Child plugin {0} was not named properly. It should be in the form <ParentPlugin>_<Platform>.uplugin", ChildPluginFilename);
			}

			// validate child plugin file name
			string PlatformName = Tokens[1];
			if (!IsValidChildPluginSuffix(PlatformName))
			{
				Log.TraceWarning("Ignoring child plugin: {0} - Unknown suffix \"{1}\". Expected valid platform or group", ChildPluginToMerge.File.GetFileName(), PlatformName);
				return;
			}

			// add our uplugin file to the existing plugin to be used to search for modules later
			Parent.ChildFiles.Add(ChildPluginToMerge.File);

			// merge the supported platforms
			if (ChildPluginToMerge.Descriptor.SupportedTargetPlatforms != null)
			{
				if (Parent.Descriptor.SupportedTargetPlatforms == null)
				{
					Parent.Descriptor.SupportedTargetPlatforms = ChildPluginToMerge.Descriptor.SupportedTargetPlatforms;
				}
				else
				{
					Parent.Descriptor.SupportedTargetPlatforms = Parent.Descriptor.SupportedTargetPlatforms.Union(ChildPluginToMerge.Descriptor.SupportedTargetPlatforms).ToList();
				}
			}

			// make sure we are whitelisted for any modules we list
			if (ChildPluginToMerge.Descriptor.Modules != null)
			{
				if (Parent.Descriptor.Modules == null)
				{
					Parent.Descriptor.Modules = ChildPluginToMerge.Descriptor.Modules;
				}
				else
				{
					foreach (ModuleDescriptor ChildModule in ChildPluginToMerge.Descriptor.Modules)
					{
						ModuleDescriptor ParentModule = Parent.Descriptor.Modules.FirstOrDefault(x => x.ModuleName.Equals(ChildModule.ModuleName) && x.Type == ChildModule.Type);
						if (ParentModule != null)
						{
							// merge white/blacklists (if the parent had a list, and child didn't specify a list, just add the child platform to the parent list - for white and black!)
							if (ChildModule.WhitelistPlatforms != null)
							{
								if (ParentModule.WhitelistPlatforms == null)
								{
									ParentModule.WhitelistPlatforms = ChildModule.WhitelistPlatforms;
								}
								else
								{
									ParentModule.WhitelistPlatforms = ParentModule.WhitelistPlatforms.Union(ChildModule.WhitelistPlatforms).ToList();
								}
							}
							if (ChildModule.BlacklistPlatforms != null)
							{
								if (ParentModule.BlacklistPlatforms == null)
								{
									ParentModule.BlacklistPlatforms = ChildModule.BlacklistPlatforms;
								}
								else
								{
									ParentModule.BlacklistPlatforms = ParentModule.BlacklistPlatforms.Union(ChildModule.BlacklistPlatforms).ToList();
								}
							}
						}
						else
						{
							Parent.Descriptor.Modules.Add(ChildModule);
						}
					}
				}
			}

			// make sure we are whitelisted for any plugins we list
			if (ChildPluginToMerge.Descriptor.Plugins != null)
			{
				if (Parent.Descriptor.Plugins == null)
				{
					Parent.Descriptor.Plugins = ChildPluginToMerge.Descriptor.Plugins;
				}
				else
				{ 
					foreach (PluginReferenceDescriptor ChildPluginReference in ChildPluginToMerge.Descriptor.Plugins)
					{
						PluginReferenceDescriptor ParentPluginReference = Parent.Descriptor.Plugins.FirstOrDefault(x => x.Name.Equals(ChildPluginReference.Name));
						if (ParentPluginReference != null)
						{
							// we only need to whitelist the platform if the parent had a whitelist (otherwise, we could mistakenly remove all other platforms)
							if (ParentPluginReference.WhitelistPlatforms != null)
							{
								if (ChildPluginReference.WhitelistPlatforms != null)
								{
									ParentPluginReference.WhitelistPlatforms = ParentPluginReference.WhitelistPlatforms.Union(ChildPluginReference.WhitelistPlatforms).ToList();
								}
							}

							// if we want to blacklist a platform, add it even if the parent didn't have a blacklist. this won't cause problems with other platforms
							if (ChildPluginReference.BlacklistPlatforms != null)
							{
								if (ParentPluginReference.BlacklistPlatforms == null)
								{
									ParentPluginReference.BlacklistPlatforms = ChildPluginReference.BlacklistPlatforms;
								}
								else
								{
									ParentPluginReference.BlacklistPlatforms = ParentPluginReference.BlacklistPlatforms.Union(ChildPluginReference.BlacklistPlatforms).ToList();
								}
							}
						}
						else
						{
							Parent.Descriptor.Plugins.Add(ChildPluginReference);
						}
					}
				}
			}
			// @todo platplug: what else do we want to support merging?!?
		}

		// Read all the plugin descriptors under the given directory
		public static IReadOnlyList<PluginInfo> ReadPluginsFromDirectory(DirectoryReference RootDirectory, string Subdirectory, PluginType Type)
		{
			// look for directories in RootDirectory and and Platform directories under RootDirectory
			List<DirectoryReference> RootDirectories = new List<DirectoryReference>() { DirectoryReference.Combine(RootDirectory, Subdirectory) };

			// now look for platform subdirectories with the Subdirectory
			DirectoryReference PlatformDirectory = DirectoryReference.Combine(RootDirectory, Tag.Directory.Platforms);
			if (DirectoryReference.Exists(PlatformDirectory))
			{
				foreach (DirectoryReference Dir in DirectoryReference.EnumerateDirectories(PlatformDirectory))
				{
					RootDirectories.Add(DirectoryReference.Combine(Dir, Subdirectory));
				}
			}

			Dictionary<PluginInfo, FileReference> ChildPlugins = new Dictionary<PluginInfo, FileReference>();
			List<PluginInfo> OutAllParentPlugins = new List<PluginInfo>();

			foreach (DirectoryReference Dir in RootDirectories)
			{
				if (!DirectoryReference.Exists(Dir))
				{
					continue;
				}

				if (!PluginInfoCache.TryGetValue(Dir, out List<PluginInfo> Plugins))
				{
					Plugins = new List<PluginInfo>();
					foreach (FileReference PluginFileName in EnumeratePlugins(Dir))
					{
						PluginInfo Plugin = new PluginInfo(PluginFileName, Type);

						// is there a parent to merge up into?
						if (Plugin.Descriptor.bIsPluginExtension)
						{
							ChildPlugins.Add(Plugin, PluginFileName);
						}
						else
						{
							Plugins.Add(Plugin);
						}
					}
					PluginInfoCache.Add(Dir, Plugins);
				}

				// gather all of the plugins into one list
				OutAllParentPlugins.AddRange(Plugins);
			}

			// now that all parent plugins are read in, we can let the children look up the parents
			foreach (KeyValuePair<PluginInfo, FileReference> Pair in ChildPlugins)
			{
				TryMergeWithParent(Pair.Key, Pair.Value);
			}

			return OutAllParentPlugins;
		}

		// Find paths to all the plugins under a given parent directory (recursively)
		public static IEnumerable<FileReference> EnumeratePlugins(DirectoryReference ParentDirectory)
		{
			if (!PluginFileCache.TryGetValue(ParentDirectory, out List<FileReference> FileNames))
			{
				FileNames = new List<FileReference>();

				DirectoryItem ParentDirectoryItem = DirectoryItem.GetItemByDirectoryReference(ParentDirectory);
				if (ParentDirectoryItem.Exists)
				{
					using (ThreadPoolWorkQueue Queue = new ThreadPoolWorkQueue())
					{
						RecursivelyEnumeratePluginsInternal(ParentDirectoryItem, FileNames, Queue);
					}
				}

				// Sort the filenames to ensure that the plugin order is deterministic; otherwise response files will change with each build.
				FileNames = FileNames.OrderBy(x => x.FullName, StringComparer.OrdinalIgnoreCase).ToList();

				PluginFileCache.Add(ParentDirectory, FileNames);
			}

			return FileNames;
		}

		// Find paths to all the plugins under a given parent directory (recursively)
		static void RecursivelyEnumeratePluginsInternal(DirectoryItem ParentDirectory, List<FileReference> FileNames, ThreadPoolWorkQueue Queue)
		{
			foreach (DirectoryItem ChildDirectory in ParentDirectory.EnumerateSubDirectories())
			{
				bool bSearchSubDirectories = true;
				foreach (FileItem PluginFile in ChildDirectory.EnumerateAllCachedFiles())
				{
					if(PluginFile.HasExtension(Tag.Ext.Plugin))
					{
						lock(FileNames)
						{
							FileNames.Add(PluginFile.FileDirectory);
						}
						bSearchSubDirectories = false;
					}
				}

				if (bSearchSubDirectories)
				{
					Queue.Enqueue(() => RecursivelyEnumeratePluginsInternal(ChildDirectory, FileNames, Queue));
				}
			}
		}

		// Determine if a plugin is enabled for a given project
		public static bool IsPluginEnabledForTarget
		(
            PluginInfo InPlugInfo, 
            UProjectDescriptor ProjectDescriptorToCheck, /*Maybe null*/
			BuildTargetPlatform InTargetPlatform,
            TargetConfiguration InTargetConfiguration,
            TargetType InTargetType
		)
		{
			if (!InPlugInfo.Descriptor.SupportsTargetPlatform(InTargetPlatform))
			{
				return false;
			}

			bool bAllowEnginePluginsEnabledByDefault = ((ProjectDescriptorToCheck == null) || !ProjectDescriptorToCheck.DisableEnginePluginsByDefault);
			bool bEnabled = InPlugInfo.IsEnabledByDefault(bAllowEnginePluginsEnabledByDefault);
			if (ProjectDescriptorToCheck != null && 
				ProjectDescriptorToCheck.Plugins != null)
			{
				foreach (PluginReferenceDescriptor PluginReference in ProjectDescriptorToCheck.Plugins)
				{
					if (String.Compare(PluginReference.Name, InPlugInfo.Name, true) == 0 && !PluginReference.bOptional)
					{
						bEnabled = PluginReference.IsEnabledForPlatform(InTargetPlatform) && PluginReference.IsEnabledForTargetConfiguration(InTargetConfiguration) && PluginReference.IsEnabledForTarget(InTargetType);
					}
				}
			}
			return bEnabled;
		}
		
		// Determine if a plugin is enabled for a given project
		public static bool IsPluginCompiledForTarget
		(
            PluginInfo InPluginInfo,
            UProjectDescriptor ProjectDescriptorToCheck, // Maybe null.
            BuildTargetPlatform InTargetPlatform,
            TargetConfiguration InTargetConfiguration,
            TargetType InTargetType,
            bool bRequiresCookedData
		)
		{
			bool bCompiledForTarget = false;
			if (IsPluginEnabledForTarget(InPluginInfo, ProjectDescriptorToCheck, InTargetPlatform, InTargetConfiguration, InTargetType) 
				&& InPluginInfo.Descriptor.Modules != null)
			{
				bool bBuildDeveloperTools =
                    InTargetType == TargetType.Editor
					|| InTargetType == TargetType.Program
					|| (InTargetConfiguration != TargetConfiguration.Test && InTargetConfiguration != TargetConfiguration.Shipping);

                foreach (ModuleDescriptor Module in InPluginInfo.Descriptor.Modules)
				{
					if (Module.IsCompiledInConfiguration(InTargetPlatform, InTargetConfiguration, "", InTargetType, bBuildDeveloperTools, bRequiresCookedData))
					{
						bCompiledForTarget = true;
						break;
					}
				}
			}
			return bCompiledForTarget;
		}
	}
}
