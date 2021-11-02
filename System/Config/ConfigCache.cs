using System;
using System.Collections.Generic;
using System.Reflection;
using BuildToolUtilities;

namespace BuildTool
{
	// Caches config files and config file hierarchies
	public static class ConfigCache
	{
		// Delegate to add a value to an ICollection in a target object
		// <param name="TargetObject">The object containing the field to be modified</param>
		// <param name="ValueObject">The value to add</param>
		delegate void AddElementDelegate(object TargetObject, object ValueObjectToAdd); 

		// Caches information about a field with a [ConfigFile] attribute in a type
		class ConfigField
		{
			public FieldInfo           FieldInfo;     // The field with the config attribute
			public ConfigFileAttribute Attribute;     // The attribute instance
			public Type                ElementType;   // For fields implementing ICollection, specifies the element type
			public AddElementDelegate  CallbackToAdd; // For fields implementing ICollection, a callback to add an element type.
		}

		// Stores information identifying a unique config hierarchy
		class ConfigHierarchyKey
		{
			public ConfigHierarchyType HierarchyType; // The hierarchy type
			public DirectoryReference ProjectDir; // The project directory to read from
			public BuildTargetPlatform TargetPlatform; // Which platform-specific files to read

			// <param name="Type">The hierarchy type</param>
			// <param name="ProjectDir">The project directory to read from</param>
			// <param name="Platform">Which platform-specific files to read</param>
			public ConfigHierarchyKey(ConfigHierarchyType InHierarchyType, DirectoryReference InProjectDirToRead, BuildTargetPlatform InTargetPlatformToRead)
			{
				HierarchyType  = InHierarchyType;
				ProjectDir     = InProjectDirToRead;
				TargetPlatform = InTargetPlatformToRead;
			}

			// Test whether this key is equal to another object
			public override bool Equals(object Other)
			{
				return (Other is ConfigHierarchyKey OtherKey && 
					OtherKey.HierarchyType == HierarchyType && 
					OtherKey.ProjectDir == ProjectDir       && 
					OtherKey.TargetPlatform == TargetPlatform);
			}

			// Returns a stable hash of this object
			// <returns>Hash value for this object</returns>
			public override int GetHashCode()
			{
				return ((ProjectDir != null) ? ProjectDir.GetHashCode() : 0) + 
					((int)HierarchyType * 123) + 
					(TargetPlatform.GetHashCode() * 345);
			}
		}

		// Cache of individual config files
		private static readonly Dictionary<FileReference, ConfigFile> LocationToConfigFile = new Dictionary<FileReference, ConfigFile>();

		// Cache of config hierarchies by project
		private static readonly Dictionary<ConfigHierarchyKey, ConfigHierarchy> HierarchyKeyToHierarchy = new Dictionary<ConfigHierarchyKey, ConfigHierarchy>();

		// Cache of config fields by type
		private static readonly Dictionary<Type, List<ConfigField>> TypeToConfigFields = new Dictionary<Type, List<ConfigField>>();

		// Attempts to read a config file (or retrieve it from the cache)
		// <param name="Location">Location of the file to read</param>
		// <param name="ConfigFile">On success, receives the parsed config file</param>
		// <returns>True if the file exists and was read, false otherwise</returns>
		internal static bool TryReadFile(FileReference Location, out ConfigFile ConfigFile)
		{
			lock (LocationToConfigFile)
			{
				if (!LocationToConfigFile.TryGetValue(Location, out ConfigFile))
				{
					if (FileReference.Exists(Location))
					{
						ConfigFile = new ConfigFile(Location);
					}

					if (ConfigFile != null)
					{
						LocationToConfigFile.Add(Location, ConfigFile);
					}
				}
			}

			return ConfigFile != null;
		}

		// Reads a config hierarchy (or retrieve it from the cache)
		public static ConfigHierarchy ReadHierarchy
		(
            ConfigHierarchyType InConfigHierarchyType,
            DirectoryReference ProjectDirToRead, // The project directory to read the hierarchy for
			BuildTargetPlatform PlatformToRead // Which platform to read platform-specific config files for
		)
		{
			// Get the key to use for the cache. It cannot be null, so we use the engine directory if a project directory is not given.
			ConfigHierarchyKey Key = new ConfigHierarchyKey(InConfigHierarchyType, ProjectDirToRead, PlatformToRead);

			// Try to get the cached hierarchy with this key
			ConfigHierarchy Hierarchy;
			lock (HierarchyKeyToHierarchy)
			{
				if (!HierarchyKeyToHierarchy.TryGetValue(Key, out Hierarchy))
				{
					// Find all the input files
					List<ConfigFile> Files = new List<ConfigFile>();
					foreach (FileReference IniFileName in ConfigHierarchy.EnumerateConfigFileLocations(InConfigHierarchyType, ProjectDirToRead, PlatformToRead))
					{
						if (TryReadFile(IniFileName, out ConfigFile File))
						{
							Files.Add(File);
						}
					}

					// Handle command line overrides
					string[] CmdLine = Environment.GetCommandLineArgs();
					string IniConfigArgPrefix = "-ini:" + Enum.GetName(typeof(ConfigHierarchyType), InConfigHierarchyType) + ":";
					foreach (string CmdLineArg in CmdLine)
					{
						if (CmdLineArg.StartsWith(IniConfigArgPrefix))
						{
							ConfigFile OverrideFile = new ConfigFile(CmdLineArg.Substring(IniConfigArgPrefix.Length));
							Files.Add(OverrideFile);
						}
					}

					// Create the hierarchy
					Hierarchy = new ConfigHierarchy(Files);
					HierarchyKeyToHierarchy.Add(Key, Hierarchy);
				}
			}
			return Hierarchy;
		}

        // Gets a list of ConfigFields for the given type>
        private static List<ConfigField> FindConfigFieldsForType(Type ConfigurableFieldsForType)
		{
			List<ConfigField> Fields;
			lock(TypeToConfigFields)
			{
				if (!TypeToConfigFields.TryGetValue(ConfigurableFieldsForType, out Fields))
				{
					Fields = new List<ConfigField>();
					if(ConfigurableFieldsForType.BaseType != null)
					{
						Fields.AddRange(FindConfigFieldsForType(ConfigurableFieldsForType.BaseType));
					}

					foreach (FieldInfo FieldInfo in ConfigurableFieldsForType.GetFields(BindingFlags.Instance | BindingFlags.GetField | BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
					{
						IEnumerable<ConfigFileAttribute> Attributes = FieldInfo.GetCustomAttributes<ConfigFileAttribute>();
						foreach (ConfigFileAttribute Attribute in Attributes)
						{
							// Copy the field 
							ConfigField Setter = new ConfigField { FieldInfo = FieldInfo, Attribute = Attribute };

							// Check if the field type implements ICollection<>. If so, we can take multiple values.
							foreach (Type InterfaceType in FieldInfo.FieldType.GetInterfaces())
							{
								if (InterfaceType.IsGenericType && InterfaceType.GetGenericTypeDefinition() == typeof(ICollection<>))
								{
									MethodInfo MethodInfo = InterfaceType.GetRuntimeMethod("Add", new Type[] { InterfaceType.GenericTypeArguments[0] });
									Setter.CallbackToAdd = (Target, Value) => { MethodInfo.Invoke(Setter.FieldInfo.GetValue(Target), new object[] { Value }); };
									Setter.ElementType = InterfaceType.GenericTypeArguments[0];
									break;
								}
							}

							// Add it to the output list
							Fields.Add(Setter);
						}
					}
					TypeToConfigFields.Add(ConfigurableFieldsForType, Fields);
				}
			}
			return Fields;
		}
		
		// Read config settings for the given object
		public static void ReadSettings(DirectoryReference ProjectDir, BuildTargetPlatform PlatformBeingBuilt, object TargetObjectToReceive)
		{
			ReadSettings(ProjectDir, PlatformBeingBuilt, TargetObjectToReceive, null);
		}

		// Read config settings for the given object
		internal static void ReadSettings
		(
            DirectoryReference   ProjectDir,
            BuildTargetPlatform PlatformBeingBuilt,
            object               TargetObjectToReceive,
            ConfigValueTracker   RecevingConfigTracker = null
		)
		{
			List<ConfigField> Fields = FindConfigFieldsForType(TargetObjectToReceive.GetType());
			foreach(ConfigField Field in Fields)
			{
				// Read the hierarchy listed
				ConfigHierarchy Hierarchy = ReadHierarchy(Field.Attribute.ConfigType, ProjectDir, PlatformBeingBuilt);

				// Get the key name
				string KeyName = Field.Attribute.KeyName ?? Field.FieldInfo.Name;

				// Get the value(s) associated with this key
				Hierarchy.TryGetValues(Field.Attribute.SectionName, KeyName, out IReadOnlyList<string> Values);

				// Parse the values from the config files and update the target object
				if (Field.CallbackToAdd == null)
				{
					if(Values != null && 
					   Values.Count == 1)
					{
						if (TryParseValue(Values[0], Field.FieldInfo.FieldType, out object Value))
						{
							Field.FieldInfo.SetValue(TargetObjectToReceive, Value);
						}
					}
				}
				else
				{
					if(Values != null)
					{
						foreach(string Item in Values)
						{
							if (TryParseValue(Item, Field.ElementType, out object Value))
							{
								Field.CallbackToAdd(TargetObjectToReceive, Value);
							}
						}
					}
				}

				// Save the dependency
				if (RecevingConfigTracker != null)
				{
					RecevingConfigTracker.Add(Field.Attribute.ConfigType, ProjectDir, PlatformBeingBuilt, Field.Attribute.SectionName, KeyName, Values);
				}
			}
		}

		// Attempts to parse the given text into an object which matches a specific field type
		public static bool TryParseValue(string TextToParse, Type FieldTypeToParse, out object OutValue)
		{
			if(FieldTypeToParse == typeof(string))
			{
				OutValue = TextToParse;
				return true;
			}
			else if(FieldTypeToParse == typeof(bool))
			{
				if (ConfigHierarchy.TryParse(TextToParse, out bool BoolValue))
				{
					OutValue = BoolValue;
					return true;
				}
				else
				{
					OutValue = null;
					return false;
				}
			}
			else if(FieldTypeToParse == typeof(int))
			{
				if (ConfigHierarchy.TryParse(TextToParse, out int IntValue))
				{
					OutValue = IntValue;
					return true;
				}
				else
				{
					OutValue = null;
					return false;
				}
			}
			else if(FieldTypeToParse == typeof(float))
			{
				if (ConfigHierarchy.TryParse(TextToParse, out float FloatValue))
				{
					OutValue = FloatValue;
					return true;
				}
				else
				{
					OutValue = null;
					return false;
				}
			}
			else if(FieldTypeToParse == typeof(double))
			{
				if (ConfigHierarchy.TryParse(TextToParse, out double DoubleValue))
                {
                    OutValue = DoubleValue;
                    return true;
                }
                else
                {
                    OutValue = null;
                    return false;
                }
            }
			else if(FieldTypeToParse == typeof(Guid))
			{
				if (ConfigHierarchy.TryParse(TextToParse, out Guid GuidValue))
				{
					OutValue = GuidValue;
					return true;
				}
				else
				{
					OutValue = null;
					return false;
				}
			}
			else if(FieldTypeToParse.IsEnum)
			{
				try
				{
					OutValue = Enum.Parse(FieldTypeToParse, TextToParse);
					return true;
				}
				catch
				{
					OutValue = null;
					return false;
				}
			}
			else if(FieldTypeToParse.GetGenericTypeDefinition() == typeof(Nullable<>))
			{
				return TryParseValue(TextToParse, FieldTypeToParse.GetGenericArguments()[0], out OutValue);
			}
			else
			{
				throw new BuildException("Unsupported type for [ConfigFile] attribute");
			}
		}
	}
}
