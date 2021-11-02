using System.Collections.Generic;
using System.IO;
using System.Linq;
using BuildToolUtilities;

namespace BuildTool
{
	//  Class to manage looking up data driven platform information (loaded from .ini files instead of in code)
	public class DataDrivenPlatformInfo
	{
		// All data driven information about a platform
		public class ConfigDataDrivenPlatformInfo
		{
			public bool     bIsConfidential;                    // Is the platform a confidential ("console-style") platform
			public string[] AdditionalRestrictedFolders = null; // Additional restricted folders for this platform.
			public string[] IniParentChain = null;              // Entire ini parent chain, ending with this platform
		};

		static Dictionary<string, ConfigDataDrivenPlatformInfo> PlatformInfos = null;

		// Return all data driven infos found
		public static Dictionary<string, ConfigDataDrivenPlatformInfo> GetAllPlatformInfos()
		{
			// need to init?
			if (PlatformInfos == null)
			{
				PlatformInfos = new Dictionary<string, ConfigDataDrivenPlatformInfo>();
				Dictionary<string, string> IniParents = new Dictionary<string, string>();

				foreach (DirectoryReference EngineConfigDir in BuildTool.GetAllEngineDirectories("Config"))
				{
					// look through all config dirs looking for the data driven ini file
					foreach (string FilePath in Directory.EnumerateFiles(EngineConfigDir.FullName, "DataDrivenPlatformInfo.ini", SearchOption.AllDirectories))
					{
						FileReference FileRef = new FileReference(FilePath);

						// get the platform name from the path
						string IniPlatformName;
						if (FileRef.IsUnderDirectory(DirectoryReference.Combine(BuildTool.EngineDirectory, "Config")))
						{
							// Foo/Engine/Config/<Platform>/DataDrivenPlatformInfo.ini
							IniPlatformName = Path.GetFileName(Path.GetDirectoryName(FilePath));
						}
						else
						{
							// Foo/Engine/Platforms/<Platform>/Config/DataDrivenPlatformInfo.ini
							IniPlatformName = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(FilePath)));
						}

						// load the DataDrivenPlatformInfo from the path
						ConfigFile Config = new ConfigFile(FileRef);
						ConfigDataDrivenPlatformInfo NewInfo = new ConfigDataDrivenPlatformInfo();

						// we must have the key section 
						if (Config.TryGetSection("DataDrivenPlatformInfo", out ConfigFileSection Section))
						{
							ConfigHierarchySection ParsedSection = new ConfigHierarchySection(new List<ConfigFileSection>() { Section });

							// get string values
							if (ParsedSection.TryGetValue("IniParent", out string IniParent))
							{
								IniParents[IniPlatformName] = IniParent;
							}

							// slightly nasty bool parsing for bool values
							if (ParsedSection.TryGetValue("bIsConfidential", out string Temp) == false || 
								ConfigHierarchy.TryParse(Temp, out NewInfo.bIsConfidential) == false)
							{
								NewInfo.bIsConfidential = false;
							}

							// get a list of additional restricted folders
							if (ParsedSection.TryGetValues("AdditionalRestrictedFolders", out IReadOnlyList<string> AdditionalRestrictedFolders) && 
								0 < AdditionalRestrictedFolders.Count)
							{
								NewInfo.AdditionalRestrictedFolders = AdditionalRestrictedFolders.Select(x => x.Trim()).Where(x => 0 < x.Length).ToArray();
							}

							// create cache it
							PlatformInfos[IniPlatformName] = NewInfo;
						}
					}
				}

				// now that all are read in, calculate the ini parent chain, starting with parent-most
				foreach (KeyValuePair<string, ConfigDataDrivenPlatformInfo> Pair in PlatformInfos)
				{
					// walk up the chain and build up the ini chain
					List<string> Chain = new List<string>();
					if (IniParents.TryGetValue(Pair.Key, out string CurrentPlatform))
					{
						while (!string.IsNullOrEmpty(CurrentPlatform))
						{
							// insert at 0 to reverse the order
							Chain.Insert(0, CurrentPlatform);
							if (IniParents.TryGetValue(CurrentPlatform, out CurrentPlatform) == false)
							{
								break;
							}
						}
					}

					// bake it into the info
					if (0 < Chain.Count)
					{
						Pair.Value.IniParentChain = Chain.ToArray();
					}
				}
			}

			return PlatformInfos;
		}

		// Return the data driven info for the given platform name
		public static ConfigDataDrivenPlatformInfo GetDataDrivenInfoForPlatform(string PlatformName)
		{
			// lookup the platform name (which is not guaranteed to be there)
			GetAllPlatformInfos().TryGetValue(PlatformName, out ConfigDataDrivenPlatformInfo Info);

			// return what we found of null if nothing
			return Info;
		}
	}
}
