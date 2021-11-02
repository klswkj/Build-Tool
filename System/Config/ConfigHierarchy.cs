using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BuildToolUtilities;

namespace BuildTool
{
	// Types of config file hierarchy
	public enum ConfigHierarchyType
	{
		Game,                         // BaseGame.ini, DefaultGame.ini, etc...
		Engine,                       // BaseEngine.ini, DefaultEngine.ini, etc...
		EditorPerProjectUserSettings, // BaseEditorPerProjectUserSettings.ini, DefaultEditorPerProjectUserSettings.ini, etc..
		Encryption,                   // BaseEncryption.ini, DefaultEncryption.ini, etc..
		Crypto,                       // BaseCrypto.ini, DefaultCrypto.ini, etc..
		EditorSettings,               // BaseEditorSettings.ini, DefaultEditorSettings.ini, etc...
		InstallBundle,                // BaseInstallBundle.ini, DefaultInstallBundle.ini, etc...
	}

	// Stores a set of merged key/value pairs for a config section
	public class ConfigHierarchySection
	{
		// Map of key names to their values
		private readonly Dictionary<string, List<string>> KeyToValue = new Dictionary<string, List<string>>(StringComparer.InvariantCultureIgnoreCase);

		// Construct a merged config section from the given per-file config sections
		// <param name="FileSections">Config sections from individual files</param>
		internal ConfigHierarchySection(IEnumerable<ConfigFileSection> FileSections)
		{
			foreach(ConfigFileSection FileSection in FileSections)
			{
				foreach(ConfigLine Line in FileSection.ConfigLines)
				{
                    if (Line.ActionToTakeWhenMerging == ConfigLineAction.RemoveKey)
                    {
                        KeyToValue.Remove(Line.Key);
                        continue;
                    }

					// Find or create the values for this key
					if (KeyToValue.TryGetValue(Line.Key, out List<string> Values))
					{
						// Update the existing list
						if (Line.ActionToTakeWhenMerging == ConfigLineAction.Set)
						{
							Values.Clear();
							Values.Add(Line.Value);
						}
						else if (Line.ActionToTakeWhenMerging == ConfigLineAction.Add)
						{
							Values.Add(Line.Value);
						}
						else if (Line.ActionToTakeWhenMerging == ConfigLineAction.RemoveKeyValue)
						{
							Values.RemoveAll(x => x.Equals(Line.Value, StringComparison.InvariantCultureIgnoreCase));
						}
					}
					else
					{
						// If it's a set or add action, create and add a new list
						if (Line.ActionToTakeWhenMerging == ConfigLineAction.Set || 
							Line.ActionToTakeWhenMerging == ConfigLineAction.Add)
						{
							KeyToValue.Add(Line.Key, new List<string> { Line.Value });
						}
					}
				}
			}
		}

		public IEnumerable<string> KeyNames => KeyToValue.Keys;

		public bool TryGetValue(string KeyNameToSearch, out string OutValue)
		{
			if (KeyToValue.TryGetValue(KeyNameToSearch, out List<string> ValuesList) && 
				0 < ValuesList.Count)
			{
				OutValue = ValuesList[0];
				return true;
			}
			else
			{
				OutValue = null;
				return false;
			}
		}

		public bool TryGetValues(string KeyNameToSearch, out IReadOnlyList<string> OutValues)
		{
			if (KeyToValue.TryGetValue(KeyNameToSearch, out List<string> ValuesList))
			{
				OutValues = ValuesList;
				return true;
			}
			else
			{
				OutValues = null;
				return false;
			}
		}
	}

	// Encapsulates a hierarchy of config files, merging sections from them together on request 
	public class ConfigHierarchy
	{
		private readonly ConfigFile[] Files;

		private readonly Dictionary<string, ConfigHierarchySection> NameToConfigSection 
			= new Dictionary<string, ConfigHierarchySection>(StringComparer.InvariantCultureIgnoreCase);

        private readonly System.Threading.ReaderWriterLockSlim SRWLock = new System.Threading.ReaderWriterLockSlim();

        public ConfigHierarchy(IEnumerable<ConfigFile> FilesToInclude) => this.Files = FilesToInclude.ToArray();

        public HashSet<string> SectionNames
		{
			get
			{
				HashSet<string> Result = new HashSet<string>();
				foreach (ConfigFile File in Files)
				{
					foreach (string SectionName in File.SectionNames)
					{
						if ( !Result.Contains(SectionName) )
						{
							Result.Add(SectionName);
						}
					}
				}
				return Result;
			}
		}

		public ConfigHierarchySection FindSection(string SectionNameToFind)
		{
            ConfigHierarchySection Section;
            try
            {
                // Acquire a read lock and do a quick check for the config section
                SRWLock.EnterUpgradeableReadLock();
                if (!NameToConfigSection.TryGetValue(SectionNameToFind, out Section))
                {
                    try
                    {
                        // Acquire a write lock and add the config section if another thread didn't just complete it
                        SRWLock.EnterWriteLock();
                        if (!NameToConfigSection.TryGetValue(SectionNameToFind, out Section))
                        {
                            // Find all the raw sections from the file hierarchy
                            List<ConfigFileSection> RawSections = new List<ConfigFileSection>();
                            foreach (ConfigFile File in Files)
                            {
								if (File.TryGetSection(SectionNameToFind, out ConfigFileSection RawSection))
								{
									RawSections.Add(RawSection);
								}
							}

                            // Merge them together and add it to the cache
                            Section = new ConfigHierarchySection(RawSections);
                            NameToConfigSection.Add(SectionNameToFind, Section);
                        }                        
                    }
                    finally
                    {
                        SRWLock.ExitWriteLock();
                    }
                }
            }
            finally
            {
                SRWLock.ExitUpgradeableReadLock();
            }
            return Section;
        }

#region GETTER
		public bool GetBool(string SectionName, string KeyName, out bool Value)
		{
			return TryGetValue(SectionName, KeyName, out Value);
		}

		public bool GetArray(string SectionName, string KeyName, out List<string> Values)
		{
			if (TryGetValues(SectionName, KeyName, out IReadOnlyList<string> ValuesEnumerable))
			{
				Values = ValuesEnumerable.ToList();
				return true;
			}
			else
			{
				Values = null;
				return false;
			}
		}

		public bool GetString(string SectionName, string KeyName, out string Value)
		{
			if(TryGetValue(SectionName, KeyName, out Value))
			{
				return true;
			}
			else
			{
				Value = "";
				return false;
			}
		}

		public bool GetInt32(string SectionName, string KeyName, out int Value)
		{
			return TryGetValue(SectionName, KeyName, out Value);
		}

		public bool TryGetValue(string SectionName, string KeyName, out string Value)
		{
			return FindSection(SectionName).TryGetValue(KeyName, out Value);
		}

		public bool TryGetValue(string SectionName, string KeyName, out bool Value)
		{
			if (!TryGetValue(SectionName, KeyName, out string Text))
			{
				Value = false;
				return false;
			}
			return TryParse(Text, out Value);
		}

		public bool TryGetValue(string SectionName, string KeyName, out int Value)
		{
			if (!TryGetValue(SectionName, KeyName, out string Text))
			{
				Value = 0;
				return false;
			}
			return TryParse(Text, out Value);
		}

		public bool TryGetValue(string SectionName, string KeyName, out Guid Value)
		{
			if (!TryGetValue(SectionName, KeyName, out string Text))
			{
				Value = Guid.Empty;
				return false;
			}
			return TryParse(Text, out Value);
		}

		public bool TryGetValue(string SectionName, string KeyName, out float Value)
		{
			if (!TryGetValue(SectionName, KeyName, out string Text))
			{
				Value = 0;
				return false;
			}
			return TryParse(Text, out Value);
		}

		public bool TryGetValue(string SectionName, string KeyName, out double Value)
		{
			if (!TryGetValue(SectionName, KeyName, out string Text))
			{
				Value = 0;
				return false;
			}
			return TryParse(Text, out Value);
		}

		public bool TryGetValues(string SectionName, string KeyName, out IReadOnlyList<string> Values)
		{
			return FindSection(SectionName).TryGetValues(KeyName, out Values);
		}

		public static bool TryParse(string Text, out bool Value)
		{
			// C# Boolean type expects "False" or "True" but since we're not case sensitive, we need to suppor that manually
			if (Text == "1" || Text.Equals("true", StringComparison.InvariantCultureIgnoreCase))
			{
				Value = true;
				return true;
			}
			else if (Text == "0" || Text.Equals("false", StringComparison.InvariantCultureIgnoreCase))
			{
				Value = false;
				return true;
			}
			else
			{
				Value = false;
				return false;
			}
		}

		static public bool TryParse(string Text, out int Value)
		{
			return Int32.TryParse(Text, out Value);
		}

		public static bool TryParse(string Text, out Guid Value)
		{
			if (Text.Contains("A=") && Text.Contains("B=") && Text.Contains("C=") && Text.Contains("D="))
			{
				char[] Separators = new char[] { '(', ')', '=', ',', ' ', 'A', 'B', 'C', 'D' };
				string[] ComponentValues = Text.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
				if (ComponentValues.Length == 4)
				{
					StringBuilder HexString = new StringBuilder();
					for (int ComponentIndex = 0; ComponentIndex < 4; ++ComponentIndex)
					{
						if (!Int32.TryParse(ComponentValues[ComponentIndex], out int IntegerValue))
						{
							Value = Guid.Empty;
							return false;
						}
						HexString.Append(IntegerValue.ToString("X8"));
					}
					Text = HexString.ToString();
				}
			}
			return Guid.TryParseExact(Text, "N", out Value);
		}

		public static bool TryParse(string Text, out float Value)
		{
			if(Text.EndsWith("f") || Text.EndsWith("d"))
			{
				return Single.TryParse(Text.Substring(0, Text.Length - 1), out Value);
			}
			else
			{
				return Single.TryParse(Text, out Value);
			}
		}

		public static bool TryParse(string Text, out double Value)
		{
			if(Text.EndsWith("f") || Text.EndsWith("d"))
			{
				return Double.TryParse(Text.Substring(0, Text.Length - 1), out Value);
			}
			else
			{
				return Double.TryParse(Text, out Value);
			}
		}

		// Attempts to parse the given line as a config object (eg. (Name="Foo",Number=1234)).
		public static bool TryParse(string Line, out Dictionary<string, string> Properties)
		{
			// Convert the string to a zero-terminated array, to make parsing easier.
			char[] Chars = new char[Line.Length + 1];
			Line.CopyTo(0, Chars, 0, Line.Length);

			// Get the opening paren
			int Idx = 0;
			while(Char.IsWhiteSpace(Chars[Idx]))
            {
                ++Idx;
            }
			if(Chars[Idx] != '(')
			{
				Properties = null;
				return false;
			}

			// Read to the next token
			++Idx;

			while(Char.IsWhiteSpace(Chars[Idx]))
            {
                ++Idx;
            }

			// Create the dictionary to receive the new properties
			Dictionary<string, string> NewProperties = new Dictionary<string, string>();

			// Read a sequence of key/value pairs
			StringBuilder Value = new StringBuilder();
			if(Chars[Idx] != ')')
			{
				for (;;)
				{
					// Find the end of the name
					int NameIdx = Idx;
					while(Char.IsLetterOrDigit(Chars[Idx]) || Chars[Idx] == '_')
					{
						++Idx;
					}
					if(Idx == NameIdx)
					{
						Properties = null;
						return false;
					}

					// Extract the key string, and make sure it hasn't already been added
					string Key = new string(Chars, NameIdx, Idx - NameIdx);
					if(NewProperties.ContainsKey(Key))
					{
						Properties = null;
						return false;
					}

					// Consume the equals character
					while(Char.IsWhiteSpace(Chars[Idx]))
                    {
                        ++Idx;
                    }
					if(Chars[Idx] != '=')
					{
						Properties = null;
						return false;
					}

					// Move to the value
					Idx++;
					while (Char.IsWhiteSpace(Chars[Idx]))
                    {
                        ++Idx;
                    }

					// Parse the value
					Value.Clear();
					if (Char.IsLetterOrDigit(Chars[Idx]) || Chars[Idx] == '_')
					{
						while (Char.IsLetterOrDigit(Chars[Idx]) || Chars[Idx] == '_' || Chars[Idx] == '.')
						{
							Value.Append(Chars[Idx++]);
						}
					}
					else if (Chars[Idx] == '\"')
					{
						++Idx;
						for(; Chars[Idx] != '\"'; ++Idx)
						{
							if (Chars[Idx] == '\0')
							{
								Properties = null;
								return false;
							}
							else
							{
								Value.Append(Chars[Idx]);
							}
						}
						++Idx;
					}
					else if (Chars[Idx] == '(')
					{
						Value.Append(Chars[Idx++]);

						bool bInQuotes = false;
						for (int Nesting = 1; 0 < Nesting; ++Idx)
						{
							if (Chars[Idx] == '\0')
							{
								Properties = null;
								return false;
							}
							else if (Chars[Idx] == '(' && !bInQuotes)
							{
								++Nesting;
							}
							else if (Chars[Idx] == ')' && !bInQuotes)
							{
								--Nesting;
							}
							else if (Chars[Idx] == '\"' || Chars[Idx] == '\'')
							{
								bInQuotes ^= true;
							}
							Value.Append(Chars[Idx]);
						}
					}
					else
					{
						Properties = null;
						return false;
					}

					// Extract the value string
					NewProperties[Key] = Value.ToString();

					// Move to the separator
					while(Char.IsWhiteSpace(Chars[Idx]))
					{
						++Idx;
					}
					if(Chars[Idx] == ')')
					{
						break;
					}
					if(Chars[Idx] != ',')
					{
						Properties = null;
						return false;
					}

					// Move to the next field
					++Idx;
					while (Char.IsWhiteSpace(Chars[Idx]))
                    {
                        ++Idx;
                    }
				}
			}

			// Make sure we're at the end of the string
			++Idx;
			while(Char.IsWhiteSpace(Chars[Idx]))
			{
				++Idx;
			}
			if(Chars[Idx] != '\0')
			{
				Properties = null;
				return false;
			}

			Properties = NewProperties;
			return true;
		}

		#endregion GETTER

		enum EConfigFlag
		{
			None,
			// Required,                  // not needed in C# land
			// AllowCommandLineOverride,  // not needed in C# land
			// DedicatedServerOnly,       // not needed in C# land
			// GenerateCacheKey,          // not needed in C# land
		};

		class ConfigLayer
		{
			// Used by the editor to display in the ini-editor
			// string EditorName; // don't need editor name in C# land
			// Path to the ini file (with variables)
			public string Path;
			// Special flag
			// public EConfigFlag Flag = EConfigFlag.None;

			public string ExtEnginePath = null;
			public string ExtProjectPath = null;
		}

		struct ConfigLayerExpansion
		{
			// The subdirectory for this expansion (ie "NoRedist")
			public string DirectoryPrefix;
			// The filename prefix for this expansion (ie "Shippable")
			public string FilePrefix;
			// Optional flags 
			// public EConfigFlag Flag;
		};

		private static readonly ConfigLayer[] ConfigLayers =
		{
			// Engine/Base.ini
			new ConfigLayer { Path = Tag.ConfigHierarchy.Engine + "/" + Tag.ConfigHierarchy.Base + Tag.Ext.Ini }, //, Flag = EConfigFlag.Required },
			// Engine/Base*.ini
 			new ConfigLayer { Path = Tag.ConfigHierarchy.Engine + "/" + Tag.ConfigHierarchy.ED + Tag.ConfigHierarchy.EF + Tag.ConfigHierarchy.Base + Tag.ConfigHierarchy.Type + Tag.Ext.Ini },
			// Engine/Platform/BasePlatform*.ini
			new ConfigLayer { Path = Tag.ConfigHierarchy.Engine + "/" + Tag.ConfigHierarchy.ED + Tag.ConfigHierarchy.Platform + "/" + Tag.ConfigHierarchy.EF + Tag.ConfigHierarchy.Base + Tag.ConfigHierarchy.Platform + Tag.ConfigHierarchy.Type + Tag.Ext.Ini, 
				ExtEnginePath = Tag.ConfigHierarchy.ExtEngine + "/" + Tag.ConfigHierarchy.ED + Tag.ConfigHierarchy.EF + Tag.ConfigHierarchy.Base + Tag.ConfigHierarchy.Platform + Tag.ConfigHierarchy.Type + Tag.Ext.Ini },
			// Project/Default*.ini
			new ConfigLayer { Path = Tag.ConfigHierarchy.Project + "/" + Tag.ConfigHierarchy.ED + Tag.ConfigHierarchy.EF + Tag.ConfigHierarchy.Default + Tag.ConfigHierarchy.Type + Tag.Ext.Ini }, //, Flag = EConfigFlag.AllowCommandLineOverride },
			// Engine/Platform/Platform*.ini
			new ConfigLayer { Path = Tag.ConfigHierarchy.Engine +"/" + Tag.ConfigHierarchy.ED + Tag.ConfigHierarchy.Platform + "/" + Tag.ConfigHierarchy.EF + Tag.ConfigHierarchy.Platform + Tag.ConfigHierarchy.Type + Tag.Ext.Ini, 
				ExtEnginePath = Tag.ConfigHierarchy.ExtEngine + "/" + Tag.ConfigHierarchy.ED + Tag.ConfigHierarchy.EF + Tag.ConfigHierarchy.Platform + Tag.ConfigHierarchy.Type + Tag.Ext.Ini },
			// Project/Platform/Platform*.ini
			new ConfigLayer { Path = Tag.ConfigHierarchy.Project + "/" + Tag.ConfigHierarchy.ED + Tag.ConfigHierarchy.Platform + "/" + Tag.ConfigHierarchy.EF + Tag.ConfigHierarchy.Platform + Tag.ConfigHierarchy.Type + Tag.Ext.Ini, 
				ExtProjectPath = Tag.ConfigHierarchy.ExtProject + "/" + Tag.ConfigHierarchy.ED + Tag.ConfigHierarchy.EF + Tag.ConfigHierarchy.Platform + Tag.ConfigHierarchy.Type + Tag.Ext.Ini },

			// UserSettings/.../User*.ini
			new ConfigLayer { Path = Tag.ConfigHierarchy.UserSettings + "/" + Tag.Directory.EngineName + "/" + Tag.Directory.Engine + "/" + Tag.Directory.Config + "/" + Tag.Directory.User + Tag.ConfigHierarchy.Type + Tag.Ext.Ini },
			// UserDir/.../User*.ini
			new ConfigLayer { Path = Tag.ConfigHierarchy.User + "/" + Tag.Directory.EngineName + "/" + Tag.Directory.Engine + "/" + Tag.Directory.Config + "/" + Tag.ConfigHierarchy.User + Tag.ConfigHierarchy.Type + Tag.Ext.Ini },
			// Project/User*.ini
			new ConfigLayer { Path = Tag.ConfigHierarchy.Project + "/" + Tag.Directory.User + Tag.ConfigHierarchy.Type + Tag.Ext.Ini },
		};

		private static readonly ConfigLayerExpansion[] ConfigLayerExpansions =
		{
			// The base expansion (ie, no expansion)
			new ConfigLayerExpansion { DirectoryPrefix = "", FilePrefix = "" }, 

			// When running a dedicated server, not used in UBT
			// new ConfigLayerExpansion { DirectoryPrefix = "", FilePrefix = "DedicatedServer" }, //  Flag_DedicatedServerOnly },

			// This file is remapped in UAT from inside NFL or NoRedist, because those directories are stripped while packaging
			new ConfigLayerExpansion { DirectoryPrefix = "", FilePrefix = Tag.ConfigHierarchy.Shippable },
			// Hidden directory from licensees
			new ConfigLayerExpansion { DirectoryPrefix = Tag.ConfigHierarchy.NFL + "/", FilePrefix = "" },
			// Settings that need to be hidden from licensees, but are needed for shipping
			new ConfigLayerExpansion { DirectoryPrefix = Tag.ConfigHierarchy.NFL + "/", FilePrefix = Tag.ConfigHierarchy.Shippable },
			// Hidden directory from non-Epic
			new ConfigLayerExpansion { DirectoryPrefix = Tag.ConfigHierarchy.NR + "/", FilePrefix = "" },
			// Settings that need to be hidden from non-Epic, but are needed for shipping
			new ConfigLayerExpansion { DirectoryPrefix = Tag.ConfigHierarchy.NR + "/", FilePrefix = Tag.ConfigHierarchy.Shippable },
		};

		// In EngineCode
		// Match FPlatformProcess::UserDir()
		private static string GetUserDir()
		{
			// Some user accounts (eg. SYSTEM on Windows) don't have a home directory. Ignore them if Environment.GetFolderPath() returns an empty string.
			string PersonalFolder = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
			string PersonalConfigFolder = null;
			if (PersonalFolder.HasValue())
			{
				PersonalConfigFolder = PersonalFolder;
				if (BuildHostPlatform.Current.Platform == BuildTargetPlatform.Mac ||
					Environment.OSVersion.Platform == PlatformID.Unix)
				{
					PersonalConfigFolder = System.IO.Path.Combine(PersonalConfigFolder, Tag.Directory.Documents);
				}
			}

			return PersonalConfigFolder;
		}

		private static string GetLayerPath
		(
			ConfigLayer Layer,
			string PlatformExtensionName,
			string IniPlatformName,
			DirectoryReference ProjectDir,
			string BaseIniName,
			out bool bHasPlatformTag,
			out bool bHasProjectTag,
			out bool bHasExpansionTag
		)
		{
			// cache some platform extension information that can be used inside the loops
			string PlatformExtensionEngineConfigDir = DirectoryReference.Combine(BuildTool.EnginePlatformExtensionsDirectory, PlatformExtensionName, Tag.Directory.Config).FullName;
			bool bHasPlatformExtensionEngineConfigDir = Directory.Exists(PlatformExtensionEngineConfigDir);

			string PlatformExtensionProjectConfigDir = ProjectDir != null ? DirectoryReference.Combine(BuildTool.AppendSuffixPlatforms(ProjectDir), PlatformExtensionName, Tag.Directory.Config).FullName : null;
			bool bHasPlatformExtensionProjectConfigDir = PlatformExtensionProjectConfigDir != null && Directory.Exists(PlatformExtensionProjectConfigDir);

			bHasPlatformTag = Layer.Path.Contains(Tag.ConfigHierarchy.Platform);
			bHasProjectTag = Layer.Path.Contains(Tag.ConfigHierarchy.Project);
			bHasExpansionTag = Layer.Path.Contains(Tag.ConfigHierarchy.ED) || Layer.Path.Contains(Tag.ConfigHierarchy.EF);
			bool bHasUserTag = Layer.Path.Contains(Tag.ConfigHierarchy.User);

			// skip platform layers if we are "platform-less", or user layers without a user dir
			if ((bHasPlatformTag && IniPlatformName == Tag.ConfigHierarchy.None)
			 || (bHasProjectTag && ProjectDir == null)
			 || (bHasUserTag && GetUserDir() == null))
			{
				return null;
			}

			// basic replacements
			string LayerPath;
			// you can only have PROJECT or ENGINE, not both
			if (bHasProjectTag)
			{
				if (bHasPlatformTag && bHasPlatformExtensionProjectConfigDir)
				{
					LayerPath = Layer.ExtProjectPath.Replace(Tag.ConfigHierarchy.ExtEngine, PlatformExtensionProjectConfigDir);
				}
				else
				{
					LayerPath = Layer.Path.Replace(Tag.ConfigHierarchy.Project, Path.Combine(ProjectDir.FullName, Tag.Directory.Config));
				}
			}
			else
			{
				if (bHasPlatformTag && bHasPlatformExtensionEngineConfigDir)
				{
					LayerPath = Layer.ExtEnginePath.Replace(Tag.ConfigHierarchy.ExtEngine, PlatformExtensionEngineConfigDir);
				}
				else
				{
					LayerPath = Layer.Path.Replace(Tag.ConfigHierarchy.Engine, Path.Combine(BuildTool.EngineDirectory.FullName, Tag.Directory.Config));
				}
			}
			LayerPath = LayerPath.Replace(Tag.ConfigHierarchy.Type, BaseIniName);
			LayerPath = LayerPath.Replace(Tag.ConfigHierarchy.UserSettings, Utils.GetUserSettingDirectory().FullName);
			LayerPath = LayerPath.Replace(Tag.ConfigHierarchy.User, GetUserDir());

			return LayerPath;
		}

		private static string GetExpansionPath(ConfigLayerExpansion Expansion, string LayerPath)
		{
			string ExpansionPath = LayerPath.Replace(Tag.ConfigHierarchy.ED, Expansion.DirectoryPrefix);
			ExpansionPath = ExpansionPath.Replace(Tag.ConfigHierarchy.EF, Expansion.FilePrefix);

			return ExpansionPath;
		}

		// Returns a list of INI filenames for the given project
		public static IEnumerable<FileReference> EnumerateConfigFileLocations(ConfigHierarchyType Type, DirectoryReference ProjectDir, BuildTargetPlatform Platform)
		{
			string BaseIniName = Enum.GetName(typeof(ConfigHierarchyType), Type);
			string PlatformName = GetIniPlatformName(Platform);

			foreach (ConfigLayer Layer in ConfigLayers)
			{
				string LayerPath = GetLayerPath(Layer, Platform.ToString(), PlatformName, ProjectDir, BaseIniName, out bool bHasPlatformTag, out bool bHasProjectTag, out bool bHasExpansionTag);

				// skip the layer if we aren't going to use it
				if (LayerPath == null)
				{
					continue;
				}

				// handle expansion (and platform - the C++ code will validate that only expansion layers have platforms)
				if (bHasExpansionTag)
				{
					foreach (ConfigLayerExpansion Expansion in ConfigLayerExpansions)
					{
						// expansion replacements
						string ExpansionPath = GetExpansionPath(Expansion, LayerPath);

						// now go up the ini parent chain
						if (bHasPlatformTag)
						{
							DataDrivenPlatformInfo.ConfigDataDrivenPlatformInfo Info = DataDrivenPlatformInfo.GetDataDrivenInfoForPlatform(PlatformName);
							if (Info != null && Info.IniParentChain != null)
							{
								// the IniParentChain
								foreach (string ParentPlatform in Info.IniParentChain)
								{
									string LocalLayerPath = GetLayerPath(Layer, ParentPlatform, ParentPlatform, ProjectDir, BaseIniName, out bHasPlatformTag, out bHasProjectTag, out bHasExpansionTag);
									string LocalExpansionPath = GetExpansionPath(Expansion, LocalLayerPath);
									yield return new FileReference(LocalExpansionPath.Replace(Tag.ConfigHierarchy.Platform, ParentPlatform));
								}
							}
							// always yield the active platform last 
							yield return new FileReference(ExpansionPath.Replace(Tag.ConfigHierarchy.Platform, PlatformName));
						}
						else
						{
							yield return new FileReference(ExpansionPath);
						}
					}
				}
				else
				{
					yield return new FileReference(LayerPath);
				}
			}

			// Find all the generated config files
			foreach (FileReference GeneratedConfigFile in EnumerateGeneratedConfigFileLocations(Type, ProjectDir, Platform))
			{
				yield return GeneratedConfigFile;
			}
		}

		// Returns a list of INI filenames for the given project
		public static IEnumerable<FileReference> EnumerateGeneratedConfigFileLocations(ConfigHierarchyType Type, DirectoryReference ProjectDir, BuildTargetPlatform Platform)
		{
			string BaseIniName = Enum.GetName(typeof(ConfigHierarchyType), Type);
			string PlatformName = GetIniPlatformName(Platform);

			// Get the generated config file too. EditorSettings overrides this from 
			if (Type == ConfigHierarchyType.EditorSettings)
			{
				yield return FileReference.Combine(GetGameAgnosticSavedDir(), Tag.Directory.Config, PlatformName, BaseIniName + Tag.Ext.Ini);
			}
			else
			{
				yield return FileReference.Combine(GetGeneratedConfigDir(ProjectDir), PlatformName, BaseIniName + Tag.Ext.Ini);
			}
		}

		// Determines the path to the generated config directory (same as FPaths::GeneratedConfigDir())
		public static DirectoryReference GetGeneratedConfigDir(DirectoryReference ProjectDir)
		{
			return DirectoryReference.Combine(ProjectDir ?? BuildTool.EngineDirectory, Tag.Directory.Saved, Tag.Directory.Config);

		}

		// Determes the path to the game-agnostic saved directory (same as FPaths::GameAgnosticSavedDir())
		public static DirectoryReference GetGameAgnosticSavedDir()
		{
			if (BuildTool.IsEngineInstalled())
			{
				return DirectoryReference.Combine
				(
                    Utils.GetUserSettingDirectory(),
                    Tag.Directory.EngineName,
                    String.Format("{0}.{1}", ReadOnlyBuildVersion.Current.MajorVersion, ReadOnlyBuildVersion.Current.MinorVersion),
                    Tag.Directory.Saved
				);
			}
			else
			{
				return DirectoryReference.Combine(BuildTool.EngineDirectory, Tag.Directory.Saved);
			}
		}

		// Returns the platform name to use as part of platform-specific config files
		public static string GetIniPlatformName(BuildTargetPlatform TargetPlatform)
		{
			if (TargetPlatform == BuildTargetPlatform.Win32 ||
				TargetPlatform == BuildTargetPlatform.Win64)
			{
				return Tag.PlatformGroup.Windows;
			}
			else if (TargetPlatform == BuildTargetPlatform.HoloLens)
			{
				return Tag.Platform.HoloLens;
			}
			else
			{
				return TargetPlatform.ToString();
			}
		}
	}
}