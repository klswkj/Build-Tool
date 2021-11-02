using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Serialization;
using BuildToolUtilities;

namespace BuildTool
{
    // Functions for manipulating the XML config cache
    internal static class XMLConfig
	{
		// An input config file	
		public class InputFile
		{
			public FileReference Location;   // Location of the file
			public string        FolderName; // Which folder to display the config file under in the generated project files
		}

		public  static FileReference CacheFile;              // The cache file that is being used
		private static XMLConfigData ConfigurationValues;    // Parsed config values
		private static XmlSerializer CachedSchemaSerializer; // Cached serializer for the XML schema

		private static readonly string XMLDeclaration     = "<?xml version=\"1.0\" encoding=\"utf-8\"?>";
		private static readonly string XMLSchemaNamespace = "http://www.w3.org/2001/XMLSchema";

		// Initialize the config system with the given types
		// <param name="OverrideCacheFile">Force use of the cached XML config without checking if it's valid (useful for remote builds)</param>
		public static void ReadConfigFiles(FileReference OverrideCacheFile)
		{
			// Find all the configurable types
			List<Type> ConfigTypes = FindConfigurableTypes();

			// Update the cache if necessary
			if(OverrideCacheFile != null)
			{
				// Set the cache file to the overriden value
				CacheFile = OverrideCacheFile;

				// Never rebuild the cache; just try to load it.
				if(!XMLConfigData.TryRead(CacheFile, ConfigTypes, out ConfigurationValues))
				{
					throw new BuildException("Unable to load XML config cache ({0})", CacheFile);
				}
			}
			else
			{
				if(BuildTool.IsEngineInstalled())
				{
					DirectoryReference UserSettingsDir = Utils.GetUserSettingDirectory();
					if(UserSettingsDir != null)
					{
						CacheFile = FileReference.Combine
						(
							UserSettingsDir, 
							Tag.Directory.EngineName, 
							String.Format("XmlConfigCache-{0}.bin", BuildTool.RootDirectory.FullName.Replace(":", "").Replace(Path.DirectorySeparatorChar, '+'))
						);
					}
				}
				else
				{
					// Get the default BuildTool configuration cache file
					CacheFile = FileReference.Combine(BuildTool.EngineDirectory, Tag.Directory.Generated, Tag.Directory.Build, "XmlConfigCache.bin");
				}

				// Find all the input files
				FileReference[] InputFiles = FindInputFiles().Select(x => x.Location).ToArray();

				// Get the path to the schema
				FileReference SchemaFile = GetSchemaLocation();

				// Try to read the existing BuildTool configuration cache from disk
				if (IsCacheUpToDate(CacheFile, InputFiles) && FileReference.Exists(SchemaFile))
				{
					if (XMLConfigData.TryRead(CacheFile, ConfigTypes, out XMLConfigData CachedValues) && 
						Enumerable.SequenceEqual(InputFiles, CachedValues.InputFiles))
					{
						ConfigurationValues = CachedValues;
					}
				}

				// If that failed, regenerate BuildTool configuration.
				if (ConfigurationValues == null)
				{
					// Find all the configurable fields from the given types
					Dictionary<string, Dictionary<string, FieldInfo>> CategoryToFields = new Dictionary<string, Dictionary<string, FieldInfo>>();
					FindConfigurableFields(ConfigTypes, CategoryToFields);

					// Create a schema for the config files
					XmlSchema Schema = CreateSchema(CategoryToFields);
					if(!BuildTool.IsEngineInstalled())
					{
						WriteSchema(Schema, SchemaFile);
					}

					// Read all the XML BuildTool Configuration files and validate them against the schema
					Dictionary<Type, Dictionary<FieldInfo, object>> TypeToValues = new Dictionary<Type, Dictionary<FieldInfo, object>>();

					foreach(FileReference ItrInputFile in InputFiles)
					{
						if(!TryReadFile(ItrInputFile, CategoryToFields, TypeToValues, Schema))
						{
							throw new BuildException("Failed to properly read XML file : {0}", ItrInputFile.FullName);
						}
					}

					// Make sure the cache directory exists
					DirectoryReference.CreateDirectory(CacheFile.Directory);

					// Create the new cache
					ConfigurationValues = new XMLConfigData(InputFiles, TypeToValues.ToDictionary(x => x.Key, x => x.Value.ToArray()));
					ConfigurationValues.Write(CacheFile);
				}
			}

			// Apply all the static field values
			foreach(KeyValuePair<Type, KeyValuePair<FieldInfo, object>[]> TypeValuesPair in ConfigurationValues.TypeToFieldAndValues)
			{
				foreach(KeyValuePair<FieldInfo, object> FieldValuePair in TypeValuesPair.Value)
				{
					if(FieldValuePair.Key.IsStatic)
					{
						object Value = InstanceValue(FieldValuePair.Value, FieldValuePair.Key.FieldType);
						FieldValuePair.Key.SetValue(null, Value);
					}
				}
			}
		}

		// Find all the configurable types in the current assembly
		private static List<Type> FindConfigurableTypes()
		{
			List<Type> ConfigTypes = new List<Type>();
			foreach(Type ConfigType in Assembly.GetExecutingAssembly().GetTypes())
			{
				if(HasXmlConfigFileAttribute(ConfigType))
				{
					ConfigTypes.Add(ConfigType);
				}
			}
			return ConfigTypes;
		}

		// Determines whether the given type has a field with an XmlConfigFile attribute
		static bool HasXmlConfigFileAttribute(Type Type)
		{
			foreach(FieldInfo Field in Type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
			{
				foreach(CustomAttributeData CustomAttribute in Field.CustomAttributes)
				{
					if(CustomAttribute.AttributeType == typeof(XMLConfigFileAttribute))
					{
						return true;
					}
				}
			}
			return false;
		}

		// Find the location of the XML config schema
		public static FileReference GetSchemaLocation()
		{
			return FileReference.Combine(BuildTool.EngineDirectory, Tag.Directory.Saved, Tag.Directory.EngineName, Tag.Binary.BuildConfigurationSchema);
		}

		// Initialize the list of input files
		public static List<InputFile> FindInputFiles()
		{
			// Find all the input file locations
			List<InputFile> InputFiles = new List<InputFile>();

			// Skip all the config files under the Engine folder if it's an installed build
			if(!BuildTool.IsEngineInstalled())
			{
				// Check for the config file under /Engine/Programs/NotForLicensees/BuildTool
				FileReference NotForLicenseesConfigLocation = FileReference.Combine(BuildTool.EngineDirectory, "Programs", "NotForLicensees", "BuildTool", "BuildConfiguration.xml");
				
				if(FileReference.Exists(NotForLicenseesConfigLocation))
				{
					InputFiles.Add(new InputFile { Location = NotForLicenseesConfigLocation, FolderName = "NotForLicensees" });
				}

				// Check for the user config file under /Engine/Programs/NotForLicensees/BuildTool
				FileReference UserConfigLocation = FileReference.Combine(BuildTool.EngineDirectory, "Saved", "BuildTool", "BuildConfiguration.xml");
				if(!FileReference.Exists(UserConfigLocation))
				{
					CreateDefaultConfigFile(UserConfigLocation);
				}
				InputFiles.Add(new InputFile { Location = UserConfigLocation, FolderName = "User" });
			}

			// Check for the global config file under AppData/Engine/BuildTool
			// C:\Users\<user name>\AppData\Roaming
			string AppDataFolder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
			if(!String.IsNullOrEmpty(AppDataFolder))
			{
				FileReference AppDataConfigLocation = FileReference.Combine(new DirectoryReference(AppDataFolder), "Engine", "BuildTool", "BuildConfiguration.xml");
				if(!FileReference.Exists(AppDataConfigLocation))
				{
					CreateDefaultConfigFile(AppDataConfigLocation);
				}
				InputFiles.Add(new InputFile { Location = AppDataConfigLocation, FolderName = "Global (AppData)" });
			}
			else
			{
				throw new BuildException("{0} Directory doesn't exist.", AppDataFolder);
			}

			// Check for the global config file under My Documents/Engine/BuildTool
			string PersonalFolder = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
			if(!String.IsNullOrEmpty(PersonalFolder))
			{
				FileReference PersonalConfigLocation = FileReference.Combine(new DirectoryReference(PersonalFolder), " Engine", "BuildTool", "BuildConfiguration.xml");
				if(FileReference.Exists(PersonalConfigLocation))
				{
					InputFiles.Add(new InputFile { Location = PersonalConfigLocation, FolderName = "Global (Documents)" });
				}
			}

			return InputFiles;
		}

		// Create a default config file at the given location
		static void CreateDefaultConfigFile(FileReference LocationToRead)
		{
			if(!DirectoryReference.Exists(LocationToRead.Directory))
			{
				DirectoryReference.CreateDirectory(LocationToRead.Directory);
			}

			using (StreamWriter Writer = new StreamWriter(LocationToRead.FullName))
			{
				Writer.WriteLine("<?xml version=\"1.0\" encoding=\"utf-8\" ?>");
				Writer.WriteLine("<Configuration xmlns=\"{0}\">", XMLConfigFile.SchemaNamespaceURI);
				Writer.WriteLine("</Configuration>");
			}
		}

		// Applies config values to the given object
		// <param name="TargetObject">The object instance to be configured</param>
		public static void ApplyTo(object TargetObject)
		{
			for(Type TargetType = TargetObject.GetType(); TargetType != null; TargetType = TargetType.BaseType)
			{
				if (ConfigurationValues.TypeToFieldAndValues.TryGetValue(TargetType, out KeyValuePair<FieldInfo, object>[] FieldValues))
				{
					foreach (KeyValuePair<FieldInfo, object> FieldValuePair in FieldValues)
					{
						if (!FieldValuePair.Key.IsStatic)
						{
							object ValueInstance = InstanceValue(FieldValuePair.Value, FieldValuePair.Key.FieldType);
							FieldValuePair.Key.SetValue(TargetObject, ValueInstance);
						}
					}
				}
			}
		}

		// Instances a value for assignment to a target object
		static object InstanceValue(object Value, Type ValueType)
		{
			if(ValueType == typeof(string[]))
			{
				return ((string[])Value).Clone();
			}
			else
			{
				return Value;
			}
		}

		// Gets a config value for a single value, without writing it to an instance of that class
		public static bool TryGetValue(Type ConfigTypeToFind, string FieldName, out object OutValue)
		{
			// Find all the config values for this type
			if (!ConfigurationValues.TypeToFieldAndValues.TryGetValue(ConfigTypeToFind, out KeyValuePair<FieldInfo, object>[] FieldValues))
			{
				OutValue = null;
				return false;
			}

			// Find the value with the matching name
			foreach (KeyValuePair<FieldInfo, object> FieldPair in FieldValues)
			{
				if(FieldPair.Key.Name == FieldName)
				{
					OutValue = FieldPair.Value;
					return true;
				}
			}

			// Not found
			OutValue = null;
			return false;
		}

		// Find all the configurable fields in the given types by searching for XmlConfigFile attributes.
		private static void FindConfigurableFields(IEnumerable<Type> ConfigTypesToFind, Dictionary<string, Dictionary<string, FieldInfo>> CategoryToFields)
		{
			foreach(Type ConfigType in ConfigTypesToFind)
			{
				foreach(FieldInfo FieldInfo in ConfigType.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.GetField | BindingFlags.Public | BindingFlags.NonPublic))
				{
					IEnumerable<XMLConfigFileAttribute> Attributes = FieldInfo.GetCustomAttributes<XMLConfigFileAttribute>();
					foreach(XMLConfigFileAttribute Attribute in Attributes)
					{
						string CategoryName = Attribute.Category?? ConfigType.Name;

						if (!CategoryToFields.TryGetValue(CategoryName, out Dictionary<string, FieldInfo> NameToField))
						{
							NameToField = new Dictionary<string, FieldInfo>();
							CategoryToFields.Add(CategoryName, NameToField);
						}

						NameToField[Attribute.Name ?? FieldInfo.Name] = FieldInfo;
					}
				}
			}
		}

		// Creates a schema from attributes in the given types
		// <param name="CategoryToFields">Lookup for all field settings</param>
		// <returns>New schema instance</returns>
		private static XmlSchema CreateSchema(Dictionary<string, Dictionary<string, FieldInfo>> CategoryToFields)
		{
			// Create elements for all the categories
			XmlSchemaAll RootAll = new XmlSchemaAll();
			foreach(KeyValuePair<string, Dictionary<string, FieldInfo>> CategoryPair in CategoryToFields)
			{
				string CategoryName = CategoryPair.Key;

				XmlSchemaAll CategoryAll = new XmlSchemaAll();
				foreach (KeyValuePair<string, FieldInfo> FieldPair in CategoryPair.Value)
				{
					XmlSchemaElement Element = CreateSchemaFieldElement(FieldPair.Key, FieldPair.Value.FieldType);
					CategoryAll.Items.Add(Element);
				}

				XmlSchemaComplexType CategoryType = new XmlSchemaComplexType{ Particle = CategoryAll };

				XmlSchemaElement CategoryElement = new XmlSchemaElement
				{
					Name       = CategoryName,
					SchemaType = CategoryType,
					MinOccurs  = 0,
					MaxOccurs  = 1
				};

				RootAll.Items.Add(CategoryElement);
			}

			// Create the root element and schema object

			XmlSchemaElement RootElement = new XmlSchemaElement
			{
				Name       = XMLConfigFile.RootElementName,
				SchemaType = new XmlSchemaComplexType { Particle = RootAll }
			};

			XmlSchema Schema = new XmlSchema
			{
				TargetNamespace    = XMLConfigFile.SchemaNamespaceURI,
				ElementFormDefault = XmlSchemaForm.Qualified
			};
			Schema.Items.Add(RootElement);

			// Finally compile it
			XmlSchemaSet SchemaSetCompiler = new XmlSchemaSet();
			SchemaSetCompiler.Add(Schema);
			SchemaSetCompiler.Compile();
			return SchemaSetCompiler.Schemas().OfType<XmlSchema>().First();
		}

		// Creates an XML schema element for reading a value of the given type
		private static XmlSchemaElement CreateSchemaFieldElement(string FieldName, Type FieldType)
		{
			XmlSchemaElement OutXMLShemaElement = new XmlSchemaElement
			{
				Name      = FieldName,
				MinOccurs = 0,
				MaxOccurs = 1
			};

			if (FieldType == typeof(string))
			{
				OutXMLShemaElement.SchemaTypeName = XmlSchemaType.GetBuiltInSimpleType(XmlTypeCode.String).QualifiedName;
			}
			else if(FieldType == typeof(bool) || FieldType == typeof(bool?))
			{
				OutXMLShemaElement.SchemaTypeName = XmlSchemaType.GetBuiltInSimpleType(XmlTypeCode.Boolean).QualifiedName;
			}
			else if(FieldType == typeof(int))
			{
				OutXMLShemaElement.SchemaTypeName = XmlSchemaType.GetBuiltInSimpleType(XmlTypeCode.Int).QualifiedName;
			}
			else if(FieldType == typeof(float))
			{
				OutXMLShemaElement.SchemaTypeName = XmlSchemaType.GetBuiltInSimpleType(XmlTypeCode.Float).QualifiedName;
			}
			else if(FieldType == typeof(double))
			{
				OutXMLShemaElement.SchemaTypeName = XmlSchemaType.GetBuiltInSimpleType(XmlTypeCode.Double).QualifiedName;
			}
			else if(FieldType == typeof(FileReference))
			{
				OutXMLShemaElement.SchemaTypeName = XmlSchemaType.GetBuiltInSimpleType(XmlTypeCode.String).QualifiedName;
			}
			else if(FieldType.IsEnum)
			{
				XmlSchemaSimpleTypeRestriction Restriction = new XmlSchemaSimpleTypeRestriction
				{
					BaseTypeName = XmlSchemaType.GetBuiltInSimpleType(XmlTypeCode.String).QualifiedName
				};

				foreach (string EnumName in Enum.GetNames(FieldType))
				{
					Restriction.Facets.Add(new XmlSchemaEnumerationFacet { Value = EnumName });
				}

				OutXMLShemaElement.SchemaType = new XmlSchemaSimpleType{ Content = Restriction };
			}
			else if(FieldType == typeof(string[]))
			{
				XmlSchemaElement ItemElement = new XmlSchemaElement
				{
					Name = "Item",
					SchemaTypeName = XmlSchemaType.GetBuiltInSimpleType(XmlTypeCode.String).QualifiedName,
					MinOccurs = 0,
					MaxOccursString = "unbounded"
				};

				XmlSchemaSequence XMLSchemaSequence = new XmlSchemaSequence();
				XMLSchemaSequence.Items.Add(ItemElement);

				OutXMLShemaElement.SchemaType = new XmlSchemaComplexType
				{
					Particle = XMLSchemaSequence
				};
			}
			else
			{
				throw new Exception("Unsupported field type for XmlConfigFile attribute");
			}

			return OutXMLShemaElement;
		}

		// Writes a schema to the given location. Avoids writing it if the file is identical.
		// <param name="Schema">The schema to be written</param>
		// <param name="Location">Location to write to</param>
		private static void WriteSchema(XmlSchema SchemaTobeWritten, FileReference FileToWriteTo)
		{
			XmlWriterSettings Settings = new XmlWriterSettings
			{
				Indent             = true,
				IndentChars        = "\t",
				NewLineChars       = Environment.NewLine,
				OmitXmlDeclaration = true
			};

			if (CachedSchemaSerializer == null)
			{
				CachedSchemaSerializer = XmlSerializer.FromTypes(new Type[] { typeof(XmlSchema) })[0];
			}

			StringBuilder Output = new StringBuilder();
			Output.AppendLine(XMLDeclaration);

			using(XmlWriter Writer = XmlWriter.Create(Output, Settings))
			{
				XmlSerializerNamespaces Namespaces = new XmlSerializerNamespaces();
				Namespaces.Add("", XMLSchemaNamespace);
				CachedSchemaSerializer.Serialize(Writer, SchemaTobeWritten, Namespaces);
			}

			string OutputText = Output.ToString();
			if(!FileReference.Exists(FileToWriteTo) || File.ReadAllText(FileToWriteTo.FullName) != OutputText)
			{
				DirectoryReference.CreateDirectory(FileToWriteTo.Directory);
				File.WriteAllText(FileToWriteTo.FullName, OutputText);
			}
		}

		// Reads an XML config file and merges it to the given cache
		private static bool TryReadFile
		(
			FileReference                                     FileToRead,
			Dictionary<string, Dictionary<string, FieldInfo>> CategoryToFields,
			Dictionary<Type, Dictionary<FieldInfo, object>>   TypeToValues,
			XmlSchema                                         SchemaToValidate
		)
		{
			// Read the XML file, and validate it against the schema
			if (!XMLConfigFile.TryRead(FileToRead, SchemaToValidate, out XMLConfigFile ConfigFile))
			{
				return false;
			}

			// Parse the document
			foreach (XmlElement CategoryElement in ConfigFile.DocumentElement.ChildNodes.OfType<XmlElement>())
			{
				if (CategoryToFields.TryGetValue(CategoryElement.Name, out Dictionary<string, FieldInfo> NameToField))
				{
					foreach (XmlElement KeyElement in CategoryElement.ChildNodes.OfType<XmlElement>())
					{
						if (NameToField.TryGetValue(KeyElement.Name, out FieldInfo Field))
						{
							// Parse the corresponding value
							object Value;
							if (Field.FieldType == typeof(string[]))
							{
								Value = KeyElement.ChildNodes.OfType<XmlElement>().Where(x => x.Name == "Item").Select(x => x.InnerText).ToArray();
							}
							else
							{
								Value = ParseValue(Field.FieldType, KeyElement.InnerText);
							}

							// Add it to the set of values for the type containing this field
							if (!TypeToValues.TryGetValue(Field.DeclaringType, out Dictionary<FieldInfo, object> FieldToValue))
							{
								FieldToValue = new Dictionary<FieldInfo, object>();
								TypeToValues.Add(Field.DeclaringType, FieldToValue);
							}
							FieldToValue[Field] = Value;
						}
					}
				}
			}
			return true;
		}

		// Parse the value for a field from its text based representation in an XML file
		static object ParseValue(Type FieldType, string TextToParse)
		{
			// ignore whitespace in all fields except for Strings which we leave unprocessed
			string TrimmedText = TextToParse.Trim();
			if(FieldType == typeof(string))
			{
				return TextToParse;
			}
			else if(FieldType == typeof(bool) || FieldType == typeof(bool?))
			{
				if (TrimmedText == "1" || TrimmedText.Equals("true", StringComparison.InvariantCultureIgnoreCase))
				{
					return true;
				}
				else if (TrimmedText == "0" || TrimmedText.Equals("false", StringComparison.InvariantCultureIgnoreCase))
				{
					return false;
				} 
				else 
				{
					throw new Exception(String.Format("Unable to convert '{0}' to boolean. 'true/false/0/1' are the supported formats.", TextToParse));
				}
			}
			else if(FieldType == typeof(int))
			{
				return Int32.Parse(TrimmedText);
			}
			else if(FieldType == typeof(float))
			{
				return Single.Parse(TrimmedText, System.Globalization.CultureInfo.InvariantCulture);
			}
			else if(FieldType == typeof(double))
			{
				return Double.Parse(TrimmedText, System.Globalization.CultureInfo.InvariantCulture);
			}
			else if(FieldType.IsEnum)
			{
				return Enum.Parse(FieldType, TrimmedText);
			}
			else if (FieldType == typeof(FileReference))
			{
				return FileReference.FromString(TextToParse);
			}
			else
			{
				throw new Exception(String.Format("Unsupported config type '{0}'", FieldType.Name));
			}
		}

		
		// Checks that the given cache file exists and is newer than the given input files, and attempts to read it.
		// Verifies that the resulting cache was created from the same input files in the same order.
		
		// <param name="CacheFile">Path to the config cache file</param>
		// <param name="InputFiles">The expected set of input files in the cache</param>
		// <returns>True if the cache was valid and could be read, false otherwise.</returns>
		static bool IsCacheUpToDate(FileReference CacheFileToSave, FileReference[] InputFilesInCache)
		{
			// Always rebuild if the cache doesn't exist
			if(!FileReference.Exists(CacheFileToSave))
			{
				return false;
			}

			// Get the timestamp for the cache
			DateTime CacheWriteTime = File.GetLastWriteTimeUtc(CacheFileToSave.FullName);

			// Always rebuild if this executable is newer
			if(File.GetLastWriteTimeUtc(Assembly.GetExecutingAssembly().Location) > CacheWriteTime)
			{
				return false;
			}

			// Check if any of the input files are newer than the cache
			foreach(FileReference InputFile in InputFilesInCache)
			{
				if(CacheWriteTime < File.GetLastWriteTimeUtc(InputFile.FullName))
				{
					return false;
				}
			}

			// Otherwise, it's up to date
			return true;
		}

		// Generates documentation files for the available settings, by merging the XML documentation from the compiler.
		public static void WriteDocumentation(FileReference OutputFileToWrtie)
		{
			// Find all the configurable types
			List<Type> ConfigTypes = FindConfigurableTypes();

			// Find all the configurable fields from the given types
			Dictionary<string, Dictionary<string, FieldInfo>> CategoryToFields = new Dictionary<string, Dictionary<string, FieldInfo>>();
			FindConfigurableFields(ConfigTypes, CategoryToFields);
			CategoryToFields = CategoryToFields.Where(x => 0 < x.Value.Count).ToDictionary(x => x.Key, x => x.Value);

			// Get the path to the XML documentation
			FileReference InputDocumentationFile = new FileReference(Assembly.GetExecutingAssembly().Location).ChangeExtension(".xml");
			if(!FileReference.Exists(InputDocumentationFile))
			{
				throw new BuildException("Generated assembly documentation not found at {0}.", InputDocumentationFile);
			}

			// Read the documentation
			XmlDocument InputDocumentation = new XmlDocument();
			InputDocumentation.Load(InputDocumentationFile.FullName);

			// Make sure we can write to the output file
			if(FileReference.Exists(OutputFileToWrtie))
			{
				FileReference.MakeWriteable(OutputFileToWrtie);
			}
			else
			{
				DirectoryReference.CreateDirectory(OutputFileToWrtie.Directory);
			}

			// Generate the documentation file
			if(OutputFileToWrtie.HasExtension(".xml"))
			{
				WriteDocumentationXML(OutputFileToWrtie, InputDocumentation, CategoryToFields);
			}
			else if(OutputFileToWrtie.HasExtension(".html"))
			{
				WriteDocumentationHTML(OutputFileToWrtie, InputDocumentation, CategoryToFields);
			}
			else
			{
				throw new BuildException("Unable to detect format from extension of output file ({0})", OutputFileToWrtie);
			}

			// Success!
			Log.TraceInformation("Written documentation to {0}.", OutputFileToWrtie);
		}

		// Gets the XML comment for a particular field
		private static bool TryGetXmlComment(XmlDocument Documentation, FieldInfo FieldToSearch, out List<string> Lines)
		{
			XmlNode Node = Documentation.SelectSingleNode(String.Format("//member[@name='F:{0}.{1}']/summary", FieldToSearch.DeclaringType.FullName, FieldToSearch.Name));
			if (Node == null)
			{
				Lines = null;
				return false;
			}
			else
			{
				// Reflow the comments into paragraphs, assuming that each paragraph will be separated by a blank line
				Lines = new List<string>(Node.InnerText.Trim().Split('\n').Select(x => x.Trim()));
				for (int Idx = Lines.Count - 1; 0 < Idx; --Idx)
				{
					if (0 < Lines[Idx - 1].Length && 
						!Lines[Idx].StartsWith("*") && 
						!Lines[Idx].StartsWith("-"))
					{
						Lines[Idx - 1] += " " + Lines[Idx];
						Lines.RemoveAt(Idx);
					}
				}
				return true;
			}
		}

		// Writes out documentation in xml format
		private static void WriteDocumentationXML(FileReference OutputFile, XmlDocument XMLForThisAssembly, Dictionary<string, Dictionary<string, FieldInfo>> CategoryToFields)
		{
			if (OutputFile is null)         { throw new ArgumentNullException(nameof(OutputFile)); }
			if (XMLForThisAssembly is null) { throw new ArgumentNullException(nameof(XMLForThisAssembly)); }
			if (CategoryToFields is null)   { throw new ArgumentNullException(nameof(CategoryToFields)); }

			using (StreamWriter Writer = new StreamWriter(OutputFile.FullName))
			{
				Writer.WriteLine("Availability: NoPublish");
				Writer.WriteLine("Title: Build Configuration Properties Page");
				Writer.WriteLine("Crumbs:");
				Writer.WriteLine("Description: This is a procedurally generated markdown page.");
				Writer.WriteLine("Version: {0}.{1}", ReadOnlyBuildVersion.Current.MajorVersion, ReadOnlyBuildVersion.Current.MinorVersion);
				Writer.WriteLine("");

				foreach (KeyValuePair<string, Dictionary<string, FieldInfo>> CategoryPair in CategoryToFields)
				{
					string CategoryName = CategoryPair.Key;
					Writer.WriteLine("### {0}", CategoryName);
					Writer.WriteLine();

					Dictionary<string, FieldInfo> Fields = CategoryPair.Value;
					foreach (KeyValuePair<string, FieldInfo> FieldPair in Fields)
					{
						// Get the XML comment for this field
						if (!TryGetXmlComment(XMLForThisAssembly, FieldPair.Value, out List<string> Lines) || Lines.Count == 0)
						{
							Log.TraceWarning("Missing XML comment for {0}", FieldPair.Value.Name);
							continue;
						}

						// Write the result to the .udn file
						Writer.WriteLine("$ {0} : {1}", FieldPair.Key, Lines[0]);
						for (int Idx = 1; Idx < Lines.Count; ++Idx)
						{
							if (Lines[Idx].StartsWith("*") || Lines[Idx].StartsWith("-"))
							{
								Writer.WriteLine("        * {0}", Lines[Idx].Substring(1).TrimStart());
							}
							else
							{
								Writer.WriteLine("    * {0}", Lines[Idx]);
							}
						}
						Writer.WriteLine();
					}
				}
			}
		}

		// Writes out documentation in HTML format
		// <param name="OutputFile">The output file</param>
		// <param name="InputDocumentation">The XML documentation for this assembly</param>
		// <param name="CategoryToFields">Map of string to types to fields</param>
		private static void WriteDocumentationHTML(FileReference OutputFile, XmlDocument XMLForThisAssembly, Dictionary<string, Dictionary<string, FieldInfo>> CategoryToFields)
		{
            if (OutputFile is null)         { throw new ArgumentNullException(nameof(OutputFile)); }
            if (XMLForThisAssembly is null) { throw new ArgumentNullException(nameof(XMLForThisAssembly)); }
            if (CategoryToFields is null)   { throw new ArgumentNullException(nameof(CategoryToFields)); }

            using (StreamWriter Writer = new StreamWriter(OutputFile.FullName))
			{
				Writer.WriteLine("<html>");
				Writer.WriteLine("  <body>");
				Writer.WriteLine("  <h2>BuildConfiguration Properties</h2>");
				foreach (KeyValuePair<string, Dictionary<string, FieldInfo>> CategoryPair in CategoryToFields)
				{
					string CategoryName = CategoryPair.Key;
					Writer.WriteLine("    <h3>{0}</h3>", CategoryName);
					Writer.WriteLine("    <dl>");

					Dictionary<string, FieldInfo> Fields = CategoryPair.Value;
					foreach (KeyValuePair<string, FieldInfo> FieldPair in Fields)
					{
						// Get the XML comment for this field
						List<string> Lines;
						if (!TryGetXmlComment(XMLForThisAssembly, FieldPair.Value, out Lines) || Lines.Count == 0)
						{
							Log.TraceWarning("Missing XML comment for {0}", FieldPair.Value.Name);
							continue;
						}

						// Write the result to the .udn file
						Writer.WriteLine("      <dt>{0}</dt>", FieldPair.Key);

						if (Lines.Count == 1)
						{
							Writer.WriteLine("      <dd>{0}</dd>", Lines[0]);
						}
						else
						{
							Writer.WriteLine("      <dd>");
							for (int Idx = 0; Idx < Lines.Count; Idx++)
							{
								if (Lines[Idx].StartsWith("*") || Lines[Idx].StartsWith("-"))
								{
									Writer.WriteLine("        <ul>");
									for (; Idx < Lines.Count && (Lines[Idx].StartsWith("*") || Lines[Idx].StartsWith("-")); Idx++)
									{
										Writer.WriteLine("          <li>{0}</li>", Lines[Idx].Substring(1).TrimStart());
									}
									Writer.WriteLine("        </ul>");
								}
								else
								{
									Writer.WriteLine("        {0}", Lines[Idx]);
								}
							}
							Writer.WriteLine("      </dd>");
						}
					}

					Writer.WriteLine("    </dl>");
				}
				Writer.WriteLine("  </body>");
				Writer.WriteLine("</html>");
			}
		}
	}
}